#include "Engine/State.h"
#include <Windows.h>
#include <winhttp.h>
#include <libtorrent/session_stats.hpp>
#include <algorithm>
#include <array>
#include <condition_variable>
#include <exception>
#include <span>
#include <stdexcept>
#include <system_error>
#include <thread>
#include <utility>

#pragma comment(lib, "winhttp.lib")

namespace tt
{
namespace
{
using Clock = std::chrono::steady_clock;
constexpr auto stoppingTimeout = std::chrono::seconds(10);
constexpr auto measuringTimeout = std::chrono::seconds(45);
constexpr auto holdTimeout = std::chrono::minutes(3);
constexpr auto warmup = std::chrono::seconds(2);
constexpr int parallelConnections = 4;
// The largest size the provider is known to serve in both directions.
constexpr std::size_t requestBytes = 10'000'000;

class InternetHandle
{
public:
    explicit InternetHandle(HINTERNET handle) : handle(handle)
    {
        if (!handle)
            throw std::system_error(GetLastError(), std::system_category(), "WinHTTP handle");
    }
    InternetHandle(InternetHandle const&) = delete;
    InternetHandle& operator=(InternetHandle const&) = delete;
    ~InternetHandle() { Close(); }
    HINTERNET Get() const { return handle; }
    void Close()
    {
        if (auto current = std::exchange(handle, nullptr))
            WinHttpCloseHandle(current);
    }
private:
    HINTERNET handle;
};

void Check(bool succeeded)
{
    if (!succeeded)
        throw std::system_error(GetLastError(), std::system_category(), "WinHTTP request");
}

// Thrown at a request's deadline, which ends a direction's transfers normally.
struct Expired
{
};

class ConnectionRequest
{
public:
    ConnectionRequest(HINTERNET connection, bool upload, std::size_t bytes, ConnectionMeasurement& measurement)
        : upload(upload), bytes(DWORD(bytes)), measurement(measurement),
          request(WinHttpOpenRequest(connection, upload ? L"POST" : L"GET",
              (std::wstring(upload ? L"/__up?bytes=" : L"/__down?bytes=") + std::to_wstring(bytes)).c_str(),
              nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE))
    {
        auto context = reinterpret_cast<DWORD_PTR>(this);
        Check(WinHttpSetOption(request.Get(), WINHTTP_OPTION_CONTEXT_VALUE, &context, sizeof(context)));
        Check(WinHttpSetStatusCallback(request.Get(), OnStatus,
            WINHTTP_CALLBACK_FLAG_ALL_COMPLETIONS | WINHTTP_CALLBACK_FLAG_HANDLES, 0) != WINHTTP_INVALID_STATUS_CALLBACK);
    }

    ~ConnectionRequest()
    {
        request.Close();
        // WinHTTP can still use the buffers and callback context after Close returns.
        std::unique_lock lock(gate);
        ready.wait(lock, [this] { return closed; });
    }

    HINTERNET Get() const { return request.Get(); }

    void Send(std::stop_token stop, Clock::time_point deadline)
    {
        Await(WinHttpSendRequest(request.Get(), upload ? L"Content-Type: application/octet-stream\r\n" :
            WINHTTP_NO_ADDITIONAL_HEADERS, upload ? DWORD(-1) : 0, WINHTTP_NO_REQUEST_DATA, 0,
            upload ? bytes : 0, reinterpret_cast<DWORD_PTR>(this)), stop, deadline);
    }

    DWORD Write(std::span<char const> buffer, std::stop_token stop, Clock::time_point deadline)
    {
        return Await(WinHttpWriteData(request.Get(), buffer.data(), DWORD(buffer.size()), nullptr), stop, deadline);
    }

    void Receive(std::stop_token stop, Clock::time_point deadline)
    {
        Await(WinHttpReceiveResponse(request.Get(), nullptr), stop, deadline);
    }

    DWORD Read(std::span<char> buffer, std::stop_token stop, Clock::time_point deadline)
    {
        return Await(WinHttpReadData(request.Get(), buffer.data(), DWORD(buffer.size()), nullptr), stop, deadline);
    }

private:
    DWORD Await(bool succeeded, std::stop_token stop, Clock::time_point deadline)
    {
        Check(succeeded);
        std::unique_lock lock(gate);
        auto completed = ready.wait_until(lock, stop, deadline, [this] { return finished; });
        if (stop.stop_requested())
            throw std::runtime_error("connection_test_cancelled");
        if (error)
            throw std::system_error(error, std::system_category(), "WinHTTP request");
        if (!completed || completedAt >= deadline)
            throw Expired{};
        finished = false;
        return transferred;
    }

    static void CALLBACK OnStatus(HINTERNET, DWORD_PTR context, DWORD status, void* information, DWORD length)
    {
        if (!context)
            return;
        auto& self = *reinterpret_cast<ConnectionRequest*>(context);
        std::lock_guard lock(self.gate);
        switch (status)
        {
        case WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING:
            self.closed = true;
            self.ready.notify_all();
            return;
        case WINHTTP_CALLBACK_STATUS_REQUEST_ERROR:
            self.error = static_cast<WINHTTP_ASYNC_RESULT*>(information)->dwError;
            self.finished = true;
            break;
        case WINHTTP_CALLBACK_STATUS_WRITE_COMPLETE:
            self.transferred = *static_cast<DWORD*>(information);
            if (self.upload)
                self.measurement.bytes += self.transferred;
            self.finished = true;
            break;
        case WINHTTP_CALLBACK_STATUS_READ_COMPLETE:
            self.transferred = length;
            if (!self.upload)
                self.measurement.bytes += length;
            self.finished = true;
            break;
        case WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE:
        case WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE:
            self.finished = true;
            break;
        default:
            return;
        }
        self.completedAt = Clock::now();
        self.ready.notify_all();
    }

    bool upload;
    DWORD bytes;
    ConnectionMeasurement& measurement;
    InternetHandle request;
    std::mutex gate;
    std::condition_variable_any ready;
    bool finished = false;
    bool closed = false;
    Clock::time_point completedAt{};
    DWORD transferred = 0;
    DWORD error = 0;
};

// Repeats full-size requests on one connection until the direction's deadline
// closes the request in progress.
void Transfer(HINTERNET connection, bool upload, ConnectionMeasurement& measurement, std::stop_token stop,
    Clock::time_point deadline)
{
    std::array<char, 64 * 1024> buffer{};
    try
    {
        for (;;)
        {
            ConnectionRequest request(connection, upload, requestBytes, measurement);
            request.Send(stop, deadline);
            if (upload)
            {
                for (std::size_t sent = 0; sent < requestBytes;)
                {
                    auto chunk = std::min(buffer.size(), requestBytes - sent);
                    auto written = request.Write(std::span<char const>(buffer.data(), chunk), stop, deadline);
                    if (written == 0)
                        throw std::runtime_error("Incomplete connection test upload");
                    sent += written;
                }
            }
            request.Receive(stop, deadline);
            DWORD status = 0;
            DWORD length = sizeof(status);
            Check(WinHttpQueryHeaders(request.Get(), WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                WINHTTP_HEADER_NAME_BY_INDEX, &status, &length, WINHTTP_NO_HEADER_INDEX));
            if (status != 200)
                throw std::runtime_error("HTTP " + std::to_string(status));
            std::size_t received = 0;
            for (;;)
            {
                auto read = request.Read(buffer, stop, deadline);
                if (read == 0)
                    break;
                received += read;
                if (received > (upload ? 1024 * 1024 : requestBytes))
                    throw std::runtime_error("Connection test response exceeds the requested size");
            }
            if (!upload && received != requestBytes)
                throw std::runtime_error("Incomplete connection test response");
        }
    }
    catch (Expired const&)
    {
    }
}

// The rate is the average after the warm-up, so connection set-up and an
// ISP's short burst above the line's sustained speed do not count. Upload
// counts bytes the socket accepted; over the measured seconds the socket
// buffer stays full, so it does not change the average. Returns false when no
// rate was measured after the warm-up.
bool Measure(HINTERNET connection, bool upload, ConnectionMeasurement& measurement, std::stop_token stop)
{
    if (stop.stop_requested())
        throw std::runtime_error("connection_test_cancelled");
    auto started = Clock::now();
    measurement.started = started;
    auto deadline = started + measurement.duration;
    std::stop_source halt;
    std::stop_callback forward(stop, [&halt] { halt.request_stop(); });
    std::mutex gate;
    std::exception_ptr failure;
    auto from = started;
    std::uint64_t base = 0;
    bool warmed = false;
    {
        std::vector<std::jthread> workers;
        for (int index = 0; index < parallelConnections; ++index)
            workers.emplace_back([&]
            {
                try
                {
                    Transfer(connection, upload, measurement, halt.get_token(), deadline);
                }
                catch (...)
                {
                    std::lock_guard lock(gate);
                    if (!failure)
                        failure = std::current_exception();
                    halt.request_stop();
                }
            });
        for (;;)
        {
            auto now = std::min(Clock::now(), deadline);
            if (!warmed && now >= started + warmup)
            {
                warmed = true;
                from = now;
                base = measurement.bytes;
            }
            auto seconds = std::chrono::duration<double>(now - from).count();
            if (seconds > 0)
                measurement.rate = double(measurement.bytes - base) * 8 / seconds / 1'000'000;
            if (now >= deadline || halt.stop_requested())
            {
                measurement.finished = now;
                break;
            }
            std::this_thread::sleep_until(std::min(deadline, now + std::chrono::milliseconds(250)));
        }
    }
    if (stop.stop_requested())
        throw std::runtime_error("connection_test_cancelled");
    if (failure)
        std::rethrow_exception(failure);
    return warmed && measurement.rate > 0;
}

std::string_view PhaseName(ConnectionPhase phase)
{
    switch (phase)
    {
    case ConnectionPhase::Idle: return "idle";
    case ConnectionPhase::Stopping: return "stopping";
    case ConnectionPhase::Downloading: return "downloading";
    case ConnectionPhase::Uploading: return "uploading";
    case ConnectionPhase::Holding: return "holding";
    case ConnectionPhase::Restoring: return "restoring";
    case ConnectionPhase::Completed: return "completed";
    case ConnectionPhase::Cancelled: return "cancelled";
    case ConnectionPhase::Failed: return "failed";
    }
    return "idle";
}

std::string_view FailureName(ConnectionFailure failure)
{
    switch (failure)
    {
    case ConnectionFailure::Network: return "connection_test_network";
    case ConnectionFailure::Timeout: return "connection_test_timeout";
    case ConnectionFailure::Sample: return "connection_test_sample";
    case ConnectionFailure::Restore: return "connection_test_restore_failed";
    }
    return "connection_test_network";
}
}

bool Engine::State::SuspendsForConnectionTest() const
{
    if (!connectionTest)
        return false;
    auto phase = connectionTest->phase.load();
    return phase == ConnectionPhase::Stopping || phase == ConnectionPhase::Downloading ||
        phase == ConnectionPhase::Uploading || phase == ConnectionPhase::Holding;
}

Json Engine::State::ConnectionTestSnapshot() const
{
    if (!connectionTest)
        return {{"session_id", sessionId}, {"sequence", snapshotSequence}, {"phase", PhaseName(ConnectionPhase::Idle)}};
    auto const& test = *connectionTest;
    auto seconds = test.phase == ConnectionPhase::Holding ?
        std::max(0LL, std::chrono::duration_cast<std::chrono::seconds>(test.deadline - Clock::now()).count()) : 0;
    auto duration = [](ConnectionMeasurement const& measurement)
    {
        auto started = measurement.started.load();
        auto finished = measurement.finished.load();
        return started == Clock::time_point{} ? 0.0 :
            std::chrono::duration<double>((finished == Clock::time_point{} ? Clock::now() : finished) - started).count();
    };
    return {{"session_id", sessionId}, {"sequence", snapshotSequence},
        {"id", test.id}, {"phase", PhaseName(test.phase)}, {"download", test.download.rate.load()},
        {"upload", test.upload.rate.load()}, {"remaining", seconds},
        {"failure", test.failure ? FailureName(*test.failure) : std::string_view()},
        {"download_bytes", test.download.bytes.load()}, {"upload_bytes", test.upload.bytes.load()},
        {"download_seconds", duration(test.download)},
        {"upload_seconds", duration(test.upload)},
        {"time_limit", test.download.duration.count()}, {"complete", test.complete}};
}

bool Engine::State::StartConnectionTest(std::string const& connectionId, bool longer)
{
    if (settings.proxy.type != ProxyType::None || !settings.networkAdapter.empty() ||
        (connectionTest && connectionTest->phase == ConnectionPhase::Restoring) ||
        (SuspendsForConnectionTest() && (connectionTest->connectionId != connectionId ||
            connectionTest->phase != ConnectionPhase::Holding)))
        return false;
    auto test = std::make_shared<ConnectionTest>();
    test->id = NewId();
    test->connectionId = connectionId;
    if (longer)
        test->download.duration = test->upload.duration = std::chrono::seconds(15);
    test->deadline = Clock::now() + stoppingTimeout;
    connectionTest = std::move(test);
    RefreshPolicy();
    session->post_session_stats();
    return true;
}

void Engine::State::ReleaseConnectionTest(std::string const& connectionId, ConnectionPhase outcome)
{
    if (!connectionTest || connectionTest->connectionId != connectionId || !SuspendsForConnectionTest())
        return;
    auto& test = *connectionTest;
    test.stop.request_stop();
    test.outcome = outcome;
    test.phase = ConnectionPhase::Restoring;
    test.deadline = Clock::now() + std::chrono::seconds(5);
    RefreshPolicy();
}

void Engine::State::MaintainConnectionTest()
{
    if (connectionTest && connectionTest->phase == ConnectionPhase::Restoring)
    {
        auto& test = *connectionTest;
        if (session->is_paused() == Paused().has_value())
            test.phase = test.outcome;
        else if (Clock::now() >= test.deadline)
        {
            // The session did not follow the pause, so it is told once more.
            if (Paused())
                session->pause();
            else
                session->resume();
            test.failure = ConnectionFailure::Restore;
            test.phase = ConnectionPhase::Failed;
        }
        return;
    }
    if (!SuspendsForConnectionTest())
        return;
    auto& test = *connectionTest;
    if (Clock::now() >= test.deadline)
    {
        if (test.phase == ConnectionPhase::Holding)
            ReleaseConnectionTest(test.connectionId);
        else
        {
            test.failure = ConnectionFailure::Timeout;
            ReleaseConnectionTest(test.connectionId, ConnectionPhase::Failed);
        }
        return;
    }
}

void Engine::State::ObserveConnectionTest(lt::session_stats_alert const& alert)
{
    if (!connectionTest || connectionTest->phase != ConnectionPhase::Stopping)
        return;
    static auto connected = lt::find_metric_idx("peer.num_peers_connected");
    static auto halfOpen = lt::find_metric_idx("peer.num_peers_half_open");
    auto values = alert.counters();
    auto& test = *connectionTest;
    auto now = Clock::now();
    if (now >= test.deadline)
    {
        MaintainConnectionTest();
        return;
    }
    if (values[connected] != 0 || values[halfOpen] != 0 || test.sampled != transferred)
        test.quietSince.reset();
    else if (!test.quietSince)
        test.quietSince = now;
    test.sampled = transferred;
    if (test.quietSince && now - *test.quietSince >= std::chrono::seconds(1))
        MeasureConnection(connectionTest);
}

void Engine::State::MeasureConnection(std::shared_ptr<ConnectionTest> const& test)
{
    test->phase = ConnectionPhase::Downloading;
    test->deadline = Clock::now() + measuringTimeout;
    auto deadline = test->deadline;
    checks.Run([test]
    {
        auto stop = test->stop.get_token();
        if (stop.stop_requested())
            throw std::runtime_error("connection_test_cancelled");
        InternetHandle internet(WinHttpOpen(L"TinyTorrent connection test", WINHTTP_ACCESS_TYPE_NO_PROXY,
            WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, WINHTTP_FLAG_ASYNC));
        WinHttpSetTimeouts(internet.Get(), 5000, 5000, 5000, 5000);
        DWORD connections = parallelConnections;
        Check(WinHttpSetOption(internet.Get(), WINHTTP_OPTION_MAX_CONNS_PER_SERVER, &connections, sizeof(connections)));
        InternetHandle connection(WinHttpConnect(internet.Get(), L"speed.cloudflare.com", INTERNET_DEFAULT_HTTPS_PORT, 0));
        if (!Measure(connection.Get(), false, test->download, stop))
            return false;
        if (stop.stop_requested())
            throw std::runtime_error("connection_test_cancelled");
        auto phase = ConnectionPhase::Downloading;
        if (!test->phase.compare_exchange_strong(phase, ConnectionPhase::Uploading))
            throw std::runtime_error("connection_test_cancelled");
        return Measure(connection.Get(), true, test->upload, stop);
    }, [this, test, deadline](std::expected<bool, StorageOutcome> measured)
    {
        if (connectionTest != test || test->stop.stop_requested())
            return;
        if (Clock::now() >= deadline)
        {
            MaintainConnectionTest();
            return;
        }
        if (!measured.value_or(false))
        {
            auto failure = measured ? ConnectionFailure::Sample : ConnectionFailure::Network;
            log.Write("connection_test", "", measured ? std::string(FailureName(failure)) : measured.error().detail);
            test->failure = failure;
            ReleaseConnectionTest(test->connectionId, ConnectionPhase::Failed);
            return;
        }
        test->complete = true;
        test->deadline = Clock::now() + holdTimeout;
        test->phase = ConnectionPhase::Holding;
    });
}
}
