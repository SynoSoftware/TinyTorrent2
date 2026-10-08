#include "Engine/State.h"
#include <winsock2.h>
#include <ws2tcpip.h>
#include <algorithm>
#include <memory>

namespace tt
{
namespace
{
using Clock = std::chrono::steady_clock;

// As long as libtorrent waits for a peer's handshake.
constexpr auto checkLimit = std::chrono::seconds(10);
// How often a waiting check looks for the engine closing.
constexpr auto checkSlice = std::chrono::milliseconds(100);
// The longest HTTP status line the check reads.
constexpr std::size_t statusLimit = 512;

constexpr std::pair<std::string_view, ProxyOutcome> outcomes[] = {
    {"connected", ProxyOutcome::Connected},
    {"sign_in_failed", ProxyOutcome::SignInFailed},
    {"unreachable", ProxyOutcome::Unreachable},
    {"not_found", ProxyOutcome::NotFound},
    {"wrong_type", ProxyOutcome::WrongType},
    {"timed_out", ProxyOutcome::TimedOut}};

// The SOCKS4 and HTTP checks ask the proxy to connect to this address, a port
// on the proxy's own computer, so the check reaches no other host.
constexpr char target[] = "127.0.0.1:1";
constexpr char socks4Target[] = {0x00, 0x01, 0x7F, 0x00, 0x00, 0x01};

struct Winsock
{
    Winsock()
    {
        WSADATA data;
        WSAStartup(MAKEWORD(2, 2), &data);
    }
    ~Winsock()
    {
        WSACleanup();
    }
};

// One TCP connection to the proxy. A step that cannot complete throws the
// outcome that ends the check.
class Connection
{
public:
    Connection(std::stop_token stop, Clock::time_point deadline) : stop_(std::move(stop)), deadline_(deadline) {}
    Connection(Connection const&) = delete;
    Connection& operator=(Connection const&) = delete;
    ~Connection()
    {
        Close();
    }

    void Open(std::string const& host, int port)
    {
        ADDRINFOW hints{};
        hints.ai_socktype = SOCK_STREAM;
        hints.ai_protocol = IPPROTO_TCP;
        ADDRINFOW* found = nullptr;
        if (GetAddrInfoW(Wide(host).c_str(), std::to_wstring(port).c_str(), &hints, &found) != 0)
        {
            throw ProxyOutcome::NotFound;
        }
        std::unique_ptr<ADDRINFOW, decltype(&FreeAddrInfoW)> addresses(found, FreeAddrInfoW);
        for (auto address = found; address; address = address->ai_next)
        {
            if (Connect(*address))
            {
                return;
            }
        }
        throw ProxyOutcome::Unreachable;
    }

    // A proxy that closes the connection or fails while the check talks to
    // it does not speak the protocol the check uses.
    void Send(std::string_view bytes)
    {
        while (!bytes.empty())
        {
            Wait(false);
            auto sent = send(socket_, bytes.data(), static_cast<int>(bytes.size()), 0);
            if (sent == SOCKET_ERROR && WSAGetLastError() != WSAEWOULDBLOCK)
            {
                throw ProxyOutcome::WrongType;
            }
            bytes.remove_prefix(static_cast<std::size_t>(std::max(sent, 0)));
        }
    }

    std::string Receive(std::size_t size)
    {
        std::string bytes;
        while (bytes.size() < size)
        {
            bytes += Read(size - bytes.size());
        }
        return bytes;
    }

    std::string ReceiveLine()
    {
        std::string line;
        while (!line.ends_with('\n'))
        {
            if (line.size() == statusLimit)
            {
                throw ProxyOutcome::WrongType;
            }
            line += Read(1);
        }
        return line;
    }

private:
    bool Connect(ADDRINFOW const& address)
    {
        Close();
        socket_ = socket(address.ai_family, address.ai_socktype, address.ai_protocol);
        u_long nonBlocking = 1;
        if (socket_ == INVALID_SOCKET || ioctlsocket(socket_, FIONBIO, &nonBlocking) != 0)
        {
            return false;
        }
        if (connect(socket_, address.ai_addr, static_cast<int>(address.ai_addrlen)) == 0)
        {
            return true;
        }
        if (WSAGetLastError() != WSAEWOULDBLOCK)
        {
            return false;
        }
        Wait(false);
        int error = 0;
        int size = sizeof error;
        getsockopt(socket_, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &size);
        return error == 0;
    }

    std::string Read(std::size_t size)
    {
        std::string bytes(size, '\0');
        for (;;)
        {
            Wait(true);
            auto read = recv(socket_, bytes.data(), static_cast<int>(size), 0);
            if (read > 0)
            {
                bytes.resize(read);
                return bytes;
            }
            if (read == 0 || WSAGetLastError() != WSAEWOULDBLOCK)
            {
                throw ProxyOutcome::WrongType;
            }
        }
    }

    // Waits until the socket can be read or written. Windows reports a failed
    // connect only to the exception set.
    void Wait(bool reading)
    {
        for (;;)
        {
            auto remaining = std::chrono::duration_cast<std::chrono::microseconds>(deadline_ - Clock::now());
            if (remaining.count() <= 0 || stop_.stop_requested())
            {
                throw ProxyOutcome::TimedOut;
            }
            auto slice = std::min<std::chrono::microseconds>(remaining, checkSlice);
            timeval timeout{static_cast<long>(slice.count() / 1000000), static_cast<long>(slice.count() % 1000000)};
            fd_set ready;
            fd_set failed;
            FD_ZERO(&ready);
            FD_ZERO(&failed);
            FD_SET(socket_, &ready);
            FD_SET(socket_, &failed);
            if (select(0, reading ? &ready : nullptr, reading ? nullptr : &ready, &failed, &timeout) > 0)
            {
                return;
            }
        }
    }

    void Close()
    {
        if (socket_ != INVALID_SOCKET)
        {
            closesocket(socket_);
            socket_ = INVALID_SOCKET;
        }
    }

    std::stop_token stop_;
    Clock::time_point deadline_;
    SOCKET socket_ = INVALID_SOCKET;
};

std::string Sized(std::string const& text)
{
    return static_cast<char>(text.size()) + text;
}

ProxyOutcome Socks5(Connection& connection, std::string const& username, std::string const& password)
{
    bool signsIn = !username.empty();
    // Version 5, then the methods offered: none, and with a user name also
    // user name and password.
    connection.Send(signsIn ? std::string("\x05\x02\x00\x02", 4) : std::string("\x05\x01\x00", 3));
    auto choice = connection.Receive(2);
    if (choice[0] != 0x05)
    {
        return ProxyOutcome::WrongType;
    }
    switch (static_cast<unsigned char>(choice[1]))
    {
    case 0x00:
        return ProxyOutcome::Connected;
    case 0x02:
        if (!signsIn)
        {
            return ProxyOutcome::SignInFailed;
        }
        break;
    // The proxy accepts none of the methods offered.
    case 0xFF:
        return ProxyOutcome::SignInFailed;
    default:
        return ProxyOutcome::WrongType;
    }
    connection.Send("\x01" + Sized(username) + Sized(password));
    auto status = connection.Receive(2);
    if (status[0] != 0x01)
    {
        return ProxyOutcome::WrongType;
    }
    return status[1] == 0x00 ? ProxyOutcome::Connected : ProxyOutcome::SignInFailed;
}

// SOCKS4 has no sign-in, so a proxy that answers in SOCKS4 works.
ProxyOutcome Socks4(Connection& connection)
{
    // Version 4, the connect command, the target, and an empty user ID, as
    // libtorrent sends.
    connection.Send(std::string("\x04\x01", 2) + std::string(socks4Target, sizeof socks4Target) + std::string(1, '\0'));
    auto reply = connection.Receive(8);
    return reply[0] == 0x00 ? ProxyOutcome::Connected : ProxyOutcome::WrongType;
}

ProxyOutcome Http(Connection& connection, std::string const& username, std::string const& password)
{
    std::string request = std::string("CONNECT ") + target + " HTTP/1.1\r\nHost: " + target + "\r\n";
    if (!username.empty())
    {
        request += "Proxy-Authorization: Basic " + Base64(username + ":" + password) + "\r\n";
    }
    connection.Send(request + "\r\n");
    auto status = connection.ReceiveLine();
    if (!status.starts_with("HTTP/1.") || status.size() < 12)
    {
        return ProxyOutcome::WrongType;
    }
    // Any other answer means the proxy accepted the request, even when it
    // cannot reach the target.
    return status.substr(9, 3) == "407" ? ProxyOutcome::SignInFailed : ProxyOutcome::Connected;
}
}

std::string_view Engine::State::Name(ProxyOutcome outcome)
{
    return Word(outcomes, outcome);
}

void Engine::State::CheckProxy(Settings::Proxy proxy, std::function<void(std::optional<ProxyCheck>)> completion)
{
    auto check = std::make_shared<ProxyCheck>();
    checks.Run([proxy = std::move(proxy), check, stop = checkStop.get_token()]
    {
        auto started = Clock::now();
        Winsock winsock;
        Connection connection(stop, started + checkLimit);
        try
        {
            connection.Open(proxy.host, proxy.port);
            check->outcome = proxy.type == ProxyType::Socks4 ? Socks4(connection) :
                proxy.type == ProxyType::Http ? Http(connection, proxy.username, proxy.password) :
                Socks5(connection, proxy.username, proxy.password);
        }
        catch (ProxyOutcome outcome)
        {
            check->outcome = outcome;
        }
        check->elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - started);
    }, [check, completion](StorageOutcome outcome)
    {
        completion(outcome.succeeded ? std::optional(*check) : std::nullopt);
    });
}
}
