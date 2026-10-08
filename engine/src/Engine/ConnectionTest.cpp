#include "Engine/State.h"
#include <Windows.h>
#include <winhttp.h>
#include <libtorrent/session_stats.hpp>
#include <algorithm>
#include <array>
#include <stdexcept>
#include <system_error>

#pragma comment(lib, "winhttp.lib")

namespace tt
{
namespace
{
using Clock = std::chrono::steady_clock;
constexpr auto stoppingTimeout = std::chrono::seconds(10);
constexpr auto measuringTimeout = std::chrono::seconds(45);
constexpr auto holdTimeout = std::chrono::minutes(3);
constexpr std::size_t byteLimit = 128 * 1024 * 1024;

class InternetHandle
{
public:
    explicit InternetHandle(HINTERNET handle) : handle(handle)
    {
        if (!handle)
            throw std::system_error(GetLastError(), std::system_category(), "WinHTTP handle");
    }
    ~InternetHandle() { Close(); }
    HINTERNET Get() const { return handle.load(); }
    void Close()
    {
        if (auto current = handle.exchange(nullptr))
            WinHttpCloseHandle(current);
    }
private:
    std::atomic<HINTERNET> handle;
};

void Check(bool succeeded, std::stop_token stop, Clock::time_point deadline)
{
    if (stop.stop_requested())
        throw std::runtime_error("connection_test_cancelled");
    if (Clock::now() >= deadline)
        throw std::runtime_error("connection_test_timeout");
    if (!succeeded)
        throw std::system_error(GetLastError(), std::system_category(), "WinHTTP request");
}

double Measure(HINTERNET connection, bool upload, std::stop_token stop, Clock::time_point deadline)
{
    auto started = Clock::now();
    std::size_t total = 0;
    std::size_t size = 64 * 1024;
    double rate = 0;
    std::array<char, 64 * 1024> buffer{};
    while (total < byteLimit && Clock::now() - started < std::chrono::seconds(10))
    {
        size = std::min(size, byteLimit - total);
        auto path = std::wstring(upload ? L"/__up?bytes=" : L"/__down?bytes=") + std::to_wstring(size);
        InternetHandle request(WinHttpOpenRequest(connection, upload ? L"POST" : L"GET", path.c_str(),
            nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE));
        std::stop_callback cancel(stop, [&request] { request.Close(); });
        auto begin = Clock::now();
        Check(WinHttpSendRequest(request.Get(), upload ? L"Content-Type: application/octet-stream\r\n" :
            WINHTTP_NO_ADDITIONAL_HEADERS, upload ? DWORD(-1) : 0, WINHTTP_NO_REQUEST_DATA, 0,
            upload ? DWORD(size) : 0, 0), stop, deadline);
        if (upload)
        {
            for (std::size_t sent = 0; sent < size;)
            {
                DWORD written = 0;
                auto count = DWORD(std::min(buffer.size(), size - sent));
                Check(WinHttpWriteData(request.Get(), buffer.data(), count, &written) && written > 0, stop, deadline);
                sent += written;
            }
        }
        Check(WinHttpReceiveResponse(request.Get(), nullptr), stop, deadline);
        DWORD status = 0;
        DWORD length = sizeof(status);
        Check(WinHttpQueryHeaders(request.Get(), WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX, &status, &length, WINHTTP_NO_HEADER_INDEX), stop, deadline);
        if (status != 200)
            throw std::runtime_error("HTTP " + std::to_string(status));
        std::size_t received = 0;
        for (;;)
        {
            DWORD read = 0;
            Check(WinHttpReadData(request.Get(), buffer.data(), DWORD(buffer.size()), &read), stop, deadline);
            if (read == 0)
                break;
            received += read;
            if (received > (upload ? 1024 * 1024 : size))
                throw std::runtime_error("Connection test response exceeds the requested size");
        }
        if (!upload && received != size)
            throw std::runtime_error("Incomplete connection test response");
        auto seconds = std::chrono::duration<double>(Clock::now() - begin).count();
        // Upload ends at the server response, so buffered socket writes do not count as delivered bytes.
        rate = double(size) * 8 / seconds / 1'000'000;
        total += size;
        size = std::min(size * 4, std::size_t(10'000'000));
    }
    if (rate <= 0)
        throw std::runtime_error("connection_test_network");
    return rate;
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
        return {{"phase", "idle"}};
    auto const& test = *connectionTest;
    auto seconds = test.phase == ConnectionPhase::Holding ?
        std::max(0LL, std::chrono::duration_cast<std::chrono::seconds>(test.deadline - Clock::now()).count()) : 0;
    return {{"id", test.id}, {"phase", PhaseName(test.phase)}, {"download", test.download},
        {"upload", test.upload}, {"remaining", seconds}, {"failure", test.failure}};
}

bool Engine::State::StartConnectionTest(std::string const& connectionId)
{
    if (settings.proxy.type != ProxyType::None || !settings.networkAdapter.empty() ||
        (connectionTest && connectionTest->phase == ConnectionPhase::Restoring) ||
        (SuspendsForConnectionTest() && (connectionTest->connectionId != connectionId ||
            connectionTest->phase != ConnectionPhase::Holding)))
        return false;
    auto test = std::make_shared<ConnectionTest>();
    test->id = NewId();
    test->connectionId = connectionId;
    test->deadline = Clock::now() + stoppingTimeout;
    connectionTest = std::move(test);
    RefreshPolicy();
    session->post_session_stats();
    return true;
}

void Engine::State::ReleaseConnectionTest(std::string const& connectionId, bool cancelled)
{
    if (!connectionTest || connectionTest->connectionId != connectionId || !SuspendsForConnectionTest())
        return;
    auto& test = *connectionTest;
    test.stop.request_stop();
    test.outcome = cancelled ? ConnectionPhase::Cancelled : ConnectionPhase::Completed;
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
            test.stop.request_stop();
            test.failure = "connection_test_timeout";
            ReleaseConnectionTest(test.connectionId);
            test.outcome = ConnectionPhase::Failed;
        }
        return;
    }
    if (test.phase == ConnectionPhase::Stopping)
        session->post_session_stats();
}

void Engine::State::ObserveConnectionTest(lt::session_stats_alert const& alert)
{
    if (!connectionTest || connectionTest->phase != ConnectionPhase::Stopping)
        return;
    static auto connected = lt::find_metric_idx("peer.num_peers_connected");
    static auto halfOpen = lt::find_metric_idx("peer.num_peers_half_open");
    static auto sent = lt::find_metric_idx("net.sent_payload_bytes");
    static auto received = lt::find_metric_idx("net.recv_payload_bytes");
    auto values = alert.counters();
    auto& test = *connectionTest;
    auto now = Clock::now();
    if (now >= test.deadline)
    {
        MaintainConnectionTest();
        return;
    }
    if (values[connected] != 0 || values[halfOpen] != 0 || values[sent] != test.sent || values[received] != test.received)
        test.quietSince.reset();
    else if (!test.quietSince)
        test.quietSince = now;
    test.sent = values[sent];
    test.received = values[received];
    if (test.quietSince && now - *test.quietSince >= std::chrono::seconds(1))
        MeasureConnection(connectionTest);
}

void Engine::State::MeasureConnection(std::shared_ptr<ConnectionTest> const& test)
{
    test->phase = ConnectionPhase::Downloading;
    test->deadline = Clock::now() + measuringTimeout;
    auto rates = std::make_shared<std::array<double, 2>>();
    auto deadline = test->deadline;
    checks.Run([test, rates, deadline]
    {
        InternetHandle internet(WinHttpOpen(L"TinyTorrent connection test", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
        WinHttpSetTimeouts(internet.Get(), 5000, 5000, 5000, 5000);
        InternetHandle connection(WinHttpConnect(internet.Get(), L"speed.cloudflare.com", INTERNET_DEFAULT_HTTPS_PORT, 0));
        auto stop = test->stop.get_token();
        (*rates)[0] = Measure(connection.Get(), false, stop, deadline);
        if (stop.stop_requested())
            throw std::runtime_error("connection_test_cancelled");
        auto phase = ConnectionPhase::Downloading;
        if (!test->phase.compare_exchange_strong(phase, ConnectionPhase::Uploading))
            throw std::runtime_error("connection_test_cancelled");
        (*rates)[1] = Measure(connection.Get(), true, stop, deadline);
    }, [this, test, rates, deadline](StorageOutcome outcome)
    {
        if (connectionTest != test || test->stop.stop_requested())
            return;
        if (Clock::now() >= deadline)
        {
            MaintainConnectionTest();
            return;
        }
        if (!outcome.succeeded)
        {
            log.Write("connection_test", "", outcome.detail);
            test->failure = outcome.detail == "connection_test_timeout"
                ? "connection_test_timeout" : "connection_test_network";
            ReleaseConnectionTest(test->connectionId);
            test->outcome = ConnectionPhase::Failed;
            return;
        }
        test->download = (*rates)[0];
        test->upload = (*rates)[1];
        test->deadline = Clock::now() + holdTimeout;
        test->phase = ConnectionPhase::Holding;
    });
}
}
