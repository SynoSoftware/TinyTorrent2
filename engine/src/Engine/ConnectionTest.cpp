#include "Engine/State.h"
#include <Windows.h>
#include <winhttp.h>
#include <libtorrent/session_stats.hpp>
#include <algorithm>
#include <array>
#include <cmath>
#include <condition_variable>
#include <cwchar>
#include <limits>
#include <span>
#include <stdexcept>
#include <system_error>
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
            throw std::runtime_error("connection_test_timeout");
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

double ServerSeconds(HINTERNET request)
{
    std::array<wchar_t, 4096> header{};
    DWORD length = DWORD(sizeof(header));
    if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_CUSTOM, L"Server-Timing",
        header.data(), &length, WINHTTP_NO_HEADER_INDEX))
        return 0;
    std::wstring_view value(header.data(), length / sizeof(wchar_t));
    double total = 0;
    for (std::size_t offset = 0; offset < value.size();)
    {
        auto end = value.find(L',', offset);
        auto metric = value.substr(offset, end == value.npos ? value.size() - offset : end - offset);
        auto duration = metric.find(L"dur=");
        if (duration != metric.npos)
        {
            auto seconds = std::wcstod(metric.data() + duration + 4, nullptr) / 1000;
            if (std::isfinite(seconds) && seconds > 0)
            {
                if (metric.find(L"cfReq") != metric.npos)
                    return seconds;
                if (metric.find(L"cfSpeed") != metric.npos)
                    total += seconds;
            }
        }
        if (end == value.npos)
            break;
        offset = end + 1;
    }
    return total;
}

void Measure(HINTERNET connection, bool upload, ConnectionMeasurement& measurement, std::stop_token stop)
{
    if (stop.stop_requested())
        throw std::runtime_error("connection_test_cancelled");
    auto started = Clock::now();
    measurement.started = started;
    auto deadline = started + measurement.timeLimit;
    std::vector<double> samples;
    const std::array groups{
        std::pair<std::size_t, int>{100'000, upload ? 8 : 10},
        std::pair<std::size_t, int>{1'000'000, upload ? 6 : 8},
        std::pair<std::size_t, int>{10'000'000, upload ? 4 : 6},
    };
    std::size_t group = 0;
    int count = 0;
    double shortest = std::numeric_limits<double>::max();
    std::array<char, 64 * 1024> buffer{};
    try
    {
        while (measurement.bytes < measurement.byteLimit && Clock::now() < deadline)
        {
            if (stop.stop_requested())
                throw std::runtime_error("connection_test_cancelled");
            auto remaining = measurement.byteLimit - measurement.bytes.load();
            auto payload = std::min(groups[group].first, std::size_t(remaining));
            ConnectionRequest request(connection, upload, payload, measurement);
            auto begin = Clock::now();
            request.Send(stop, deadline);
            if (upload)
            {
                for (std::size_t sent = 0; sent < payload;)
                {
                    auto chunk = std::min(buffer.size(), payload - sent);
                    auto written = request.Write(std::span<char const>(buffer.data(), chunk), stop, deadline);
                    if (written == 0)
                        throw std::runtime_error("Incomplete connection test upload");
                    sent += written;
                }
            }
            request.Receive(stop, deadline);
            auto response = Clock::now();
            DWORD status = 0;
            DWORD length = sizeof(status);
            Check(WinHttpQueryHeaders(request.Get(), WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                WINHTTP_HEADER_NAME_BY_INDEX, &status, &length, WINHTTP_NO_HEADER_INDEX));
            if (status != 200)
                throw std::runtime_error("HTTP " + std::to_string(status));
            auto serverSeconds = ServerSeconds(request.Get());
            std::size_t received = 0;
            for (;;)
            {
                auto read = request.Read(buffer, stop, deadline);
                if (read == 0)
                    break;
                received += read;
                if (received > (upload ? 1024 * 1024 : payload))
                    throw std::runtime_error("Connection test response exceeds the requested size");
            }
            if (!upload && received != payload)
                throw std::runtime_error("Incomplete connection test response");
            auto seconds = std::chrono::duration<double>((upload ? response : Clock::now()) - begin).count()
                - (upload ? 0 : serverSeconds);
            // Upload ends at the server response, so buffered socket writes do not count as delivered bytes.
            if (seconds >= 0.01)
            {
                samples.push_back(double(payload) * 8 / seconds / 1'000'000);
                std::sort(samples.begin(), samples.end());
                auto position = double(samples.size() - 1) * 0.9;
                auto lower = std::size_t(position);
                auto upper = std::size_t(std::ceil(position));
                measurement.rate = samples[lower] + (samples[upper] - samples[lower]) * (position - double(lower));
            }
            shortest = std::min(shortest, seconds);
            if (++count >= groups[group].second)
            {
                if (shortest > 1 || group + 1 == groups.size())
                    break;
                ++group;
                count = 0;
                shortest = std::numeric_limits<double>::max();
            }
        }
    }
    catch (std::exception const& failure)
    {
        measurement.finished = Clock::now();
        if (std::string_view(failure.what()) != "connection_test_timeout")
            throw;
    }
    if (measurement.finished.load() == Clock::time_point{})
        measurement.finished = Clock::now();
    if (measurement.rate <= 0)
        throw std::runtime_error("connection_test_sample");
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
        return {{"session_id", sessionId}, {"sequence", snapshotSequence}, {"phase", "idle"}};
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
        {"upload", test.upload.rate.load()}, {"remaining", seconds}, {"failure", test.failure},
        {"download_bytes", test.download.bytes.load()}, {"upload_bytes", test.upload.bytes.load()},
        {"download_seconds", duration(test.download)},
        {"upload_seconds", duration(test.upload)},
        {"byte_limit", test.download.byteLimit}, {"time_limit", test.download.timeLimit.count()}, {"complete", test.complete}};
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
    {
        test->download.byteLimit = test->upload.byteLimit = 32 * 1024 * 1024;
        test->download.timeLimit = test->upload.timeLimit = std::chrono::seconds(8);
    }
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
        if (session->is_paused() == IsPaused())
            test.phase = test.outcome;
        else if (Clock::now() >= test.deadline)
        {
            appliedPause.reset();
            RefreshPolicy();
            test.failure = "connection_test_restore_failed";
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
            test.failure = "connection_test_timeout";
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
    if (values[connected] != 0 || values[halfOpen] != 0 || transferred.uploaded != test.sent || transferred.downloaded != test.received)
        test.quietSince.reset();
    else if (!test.quietSince)
        test.quietSince = now;
    test.sent = transferred.uploaded;
    test.received = transferred.downloaded;
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
        InternetHandle connection(WinHttpConnect(internet.Get(), L"speed.cloudflare.com", INTERNET_DEFAULT_HTTPS_PORT, 0));
        Measure(connection.Get(), false, test->download, stop);
        if (stop.stop_requested())
            throw std::runtime_error("connection_test_cancelled");
        auto phase = ConnectionPhase::Downloading;
        if (!test->phase.compare_exchange_strong(phase, ConnectionPhase::Uploading))
            throw std::runtime_error("connection_test_cancelled");
        Measure(connection.Get(), true, test->upload, stop);
    }, [this, test, deadline](StorageOutcome outcome)
    {
        if (connectionTest != test || test->stop.stop_requested())
            return;
        if (Clock::now() >= deadline)
        {
            MaintainConnectionTest();
            return;
        }
        if (outcome.failure)
        {
            log.Write("connection_test", "", outcome.detail);
            test->failure = outcome.detail == "connection_test_sample" ? "connection_test_sample"
                : outcome.detail == "connection_test_timeout" ? "connection_test_timeout" : "connection_test_network";
            ReleaseConnectionTest(test->connectionId, ConnectionPhase::Failed);
            return;
        }
        test->complete = true;
        test->deadline = Clock::now() + holdTimeout;
        test->phase = ConnectionPhase::Holding;
    });
}
}
