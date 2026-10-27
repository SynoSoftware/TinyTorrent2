#include "Dialer.h"
#include "Engine.h"
#include <winsock2.h>
#include <ws2tcpip.h>
#include <boost/asio/read.hpp>
#include <boost/asio/write.hpp>

namespace tt
{
namespace
{
using Clock = std::chrono::steady_clock;
using Error = libtorrent::error_code;
}

bool Dialer::Usable(Route const& route)
{
    return route.allowed && (route.proxy == ProxyType::None || !route.endpoints.empty());
}

Dialer::Dialer(libtorrent::io_context& context, std::shared_ptr<Route const> route) :
    route_(std::move(route)), socket_(context), timer_(context) {}

void Dialer::Connect(libtorrent::tcp::endpoint peer, Clock::time_point deadline, Done done)
{
    peer_ = peer;
    deadline_ = deadline;
    done_ = std::move(done);
    physical_ = 0;
    if (Usable(*route_))
        Dial();
    else
        Finish(Failure::Route);
}

void Dialer::Close()
{
    closed_ = true;
    Error ignored;
    timer_.cancel();
    socket_.close(ignored);
}

libtorrent::tcp::socket& Dialer::Socket()
{
    return socket_;
}

bool Dialer::Bind(libtorrent::tcp::endpoint const& physical, Error& error)
{
    socket_.open(physical.protocol(), error);
    if (error)
        return false;
    socket_.set_option(boost::asio::socket_base::linger(true, 0), error);
    if (!error)
        socket_.set_option(libtorrent::tcp::no_delay(true), error);
    if (error || !route_->adapter)
        return !error;
    bool ipv4 = physical.address().is_v4();
    auto index = ipv4 ? htonl(route_->adapter->ipv4Index) : route_->adapter->ipv6Index;
    if (!index)
    {
        error = boost::asio::error::address_family_not_supported;
        return false;
    }
    if (setsockopt(socket_.native_handle(), ipv4 ? IPPROTO_IP : IPPROTO_IPV6,
        ipv4 ? IP_UNICAST_IF : IPV6_UNICAST_IF, reinterpret_cast<char const*>(&index), sizeof(index)) != 0)
    {
        error = Error(WSAGetLastError(), boost::system::system_category());
        return false;
    }
    for (auto const& text : route_->adapter->addresses)
    {
        auto address = libtorrent::make_address(text, error);
        if (!error && address.is_v4() == ipv4)
        {
            if (!ipv4 && address.to_v6().is_link_local() && !physical.address().to_v6().is_link_local())
                continue;
            socket_.bind({address, 0}, error);
            if (!error)
                return true;
        }
    }
    error = boost::asio::error::address_family_not_supported;
    return false;
}

void Dialer::Dial()
{
    auto now = Clock::now();
    if (now >= deadline_)
    {
        Finish(Failure::Connect, boost::asio::error::timed_out);
        return;
    }
    bool direct = route_->proxy == ProxyType::None;
    auto endpoint = direct ? peer_ : route_->endpoints[physical_++];
    auto remaining = direct ? std::size_t(1) : route_->endpoints.size() - physical_ + 1;
    auto current = ++attempt_;
    expired_ = false;
    Error error;
    socket_.close(error);
    error.clear();
    if (!Bind(endpoint, error))
    {
        Retry(Failure::Route, error);
        return;
    }
    // Each remaining proxy endpoint gets an equal share of the time left.
    timer_.expires_at(now + (deadline_ - now) / remaining);
    timer_.async_wait([self = shared_from_this(), current](Error error)
    {
        if (!error && !self->closed_ && self->attempt_ == current)
        {
            self->expired_ = true;
            Error ignored;
            self->socket_.close(ignored);
        }
    });
    socket_.async_connect(endpoint, [self = shared_from_this(), current](Error error)
    {
        if (self->closed_ || self->attempt_ != current)
            return;
        if (self->expired_)
            error = boost::asio::error::timed_out;
        if (error)
        {
            self->Retry(Failure::Connect, error);
            return;
        }
        // The tunnel must also finish by the deadline.
        auto tunnel = ++self->attempt_;
        self->timer_.cancel();
        self->timer_.expires_at(self->deadline_);
        self->timer_.async_wait([self, tunnel](Error error)
        {
            if (!error && !self->closed_ && self->attempt_ == tunnel)
            {
                self->expired_ = true;
                Error ignored;
                self->socket_.close(ignored);
            }
        });
        self->Tunnel();
    });
}

void Dialer::Retry(Failure failure, Error error)
{
    timer_.cancel();
    auto now = Clock::now();
    if (route_->proxy != ProxyType::None && physical_ < route_->endpoints.size() && now < deadline_)
        Dial();
    else
        Finish(failure, expired_ || now >= deadline_ ? boost::asio::error::timed_out : error);
}

void Dialer::Write(std::vector<char> bytes, std::function<void()> next)
{
    auto buffer = std::make_shared<std::vector<char>>(std::move(bytes));
    boost::asio::async_write(socket_, boost::asio::buffer(*buffer),
        [self = shared_from_this(), buffer, next = std::move(next)](Error error, std::size_t)
    {
        if (self->closed_)
            return;
        if (error)
            self->Finish(Failure::Connect, self->expired_ ? boost::asio::error::timed_out : error);
        else
            next();
    });
}

void Dialer::Read(std::size_t size, std::function<void()> next)
{
    frame_.resize(size);
    boost::asio::async_read(socket_, boost::asio::buffer(frame_),
        [self = shared_from_this(), next = std::move(next)](Error error, std::size_t)
    {
        if (self->closed_)
            return;
        if (error)
            self->Finish(Failure::Connect, self->expired_ ? boost::asio::error::timed_out : error);
        else
            next();
    });
}

void Dialer::Tunnel()
{
    auto const& route = *route_;
    if (route.proxy == ProxyType::None)
        Finish(std::nullopt);
    else if (route.proxy == ProxyType::Http)
    {
        auto target = peer_.address().to_string() + ":" + std::to_string(peer_.port());
        auto request = "CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\n";
        if (!route.username.empty())
            request += "Proxy-Authorization: Basic " + Base64(route.username + ":" + route.password) + "\r\n";
        request += "\r\n";
        response_.clear();
        Write({request.begin(), request.end()}, [self = shared_from_this()] { self->Http(); });
    }
    else if (route.proxy == ProxyType::Socks4)
    {
        if (route.username.find('\0') != std::string::npos)
        {
            Finish(Failure::Route);
            return;
        }
        std::vector<char> request{4, 1, static_cast<char>(peer_.port() >> 8), static_cast<char>(peer_.port())};
        auto address = peer_.address().to_v4().to_bytes();
        request.insert(request.end(), address.begin(), address.end());
        request.insert(request.end(), route.username.begin(), route.username.end());
        request.push_back(0);
        Write(std::move(request), [self = shared_from_this()]
        {
            self->Read(8, [self]
            {
                if (self->frame_[0] == 0 && static_cast<unsigned char>(self->frame_[1]) == 90)
                    self->Finish(std::nullopt);
                else
                    self->Finish(Failure::Connect);
            });
        });
    }
    else
    {
        auto signsIn = !route.username.empty();
        Write(signsIn ? std::vector<char>{5, 2, 0, 2} : std::vector<char>{5, 1, 0},
            [self = shared_from_this()]
        {
            self->Read(2, [self]
            {
                auto const& route = *self->route_;
                if (self->frame_[0] != 5)
                    self->Finish(Failure::Connect);
                else if (self->frame_[1] == 0)
                    self->Socks();
                else if (self->frame_[1] == 2 && !route.username.empty())
                {
                    if (route.username.size() > 255 || route.password.size() > 255)
                    {
                        self->Finish(Failure::Route);
                        return;
                    }
                    std::vector<char> credentials{1, static_cast<char>(route.username.size())};
                    credentials.insert(credentials.end(), route.username.begin(), route.username.end());
                    credentials.push_back(static_cast<char>(route.password.size()));
                    credentials.insert(credentials.end(), route.password.begin(), route.password.end());
                    self->Write(std::move(credentials), [self]
                    {
                        self->Read(2, [self]
                        {
                            if (self->frame_[0] == 1 && self->frame_[1] == 0)
                                self->Socks();
                            else
                                self->Finish(Failure::Connect);
                        });
                    });
                }
                else
                    self->Finish(Failure::Connect);
            });
        });
    }
}

void Dialer::Http()
{
    Read(1, [self = shared_from_this()]
    {
        self->response_.push_back(self->frame_[0]);
        if (self->response_.ends_with("\r\n\r\n"))
        {
            if (self->response_.starts_with("HTTP/1.") && self->response_.size() >= 12 &&
                self->response_.substr(9, 3) == "200")
                self->Finish(std::nullopt);
            else
                self->Finish(Failure::Connect);
        }
        else if (self->response_.size() >= 16 * 1024)
            self->Finish(Failure::Connect);
        else
            self->Http();
    });
}

void Dialer::Socks()
{
    std::vector<char> request{5, 1, 0, 1};
    auto address = peer_.address().to_v4().to_bytes();
    request.insert(request.end(), address.begin(), address.end());
    request.push_back(static_cast<char>(peer_.port() >> 8));
    request.push_back(static_cast<char>(peer_.port()));
    Write(std::move(request), [self = shared_from_this()]
    {
        self->Read(4, [self]
        {
            if (self->frame_[0] != 5 || self->frame_[1] != 0 || self->frame_[2] != 0)
            {
                self->Finish(Failure::Connect);
                return;
            }
            auto type = self->frame_[3];
            if (type == 1 || type == 4)
                self->Read(type == 1 ? 6 : 18, [self] { self->Finish(std::nullopt); });
            else if (type == 3)
                self->Read(1, [self]
                {
                    auto count = static_cast<unsigned char>(self->frame_[0]);
                    self->Read(count + 2, [self] { self->Finish(std::nullopt); });
                });
            else
                self->Finish(Failure::Connect);
        });
    });
}

void Dialer::Finish(std::optional<Failure> failure, Error error)
{
    if (closed_ || !done_)
        return;
    // Stale timer and connect handlers compare against this.
    ++attempt_;
    timer_.cancel();
    if (failure)
    {
        Error ignored;
        socket_.close(ignored);
    }
    std::exchange(done_, {})(failure, error);
}
}
