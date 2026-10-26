#include "MetadataRequester.h"
#include "Engine.h"
#include <winsock2.h>
#include <ws2tcpip.h>
#include <libtorrent/bdecode.hpp>
#include <libtorrent/hasher.hpp>
#include <boost/asio/post.hpp>
#include <boost/asio/read.hpp>
#include <boost/asio/steady_timer.hpp>
#include <boost/asio/write.hpp>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstring>
#include <list>
#include <map>
#include <mutex>
#include <random>
#include <utility>

namespace tt
{
// Translated from Bitmagnet. Copyright (c) 2023 bitmagnet contributors.
// The MIT license and notice are in licenses/Bitmagnet/Bitmagnet.txt.
namespace
{
using Clock = std::chrono::steady_clock;
using Socket = libtorrent::tcp::socket;
using Error = libtorrent::error_code;
constexpr std::size_t blockSize = 16 * 1024;
constexpr std::size_t metadataLimit = 10 * 1024 * 1024;
constexpr char protocol[] = "\x13" "BitTorrent protocol";

void Length(std::vector<char>& bytes, std::size_t value)
{
    for (int shift : {24, 16, 8, 0})
        bytes.push_back(static_cast<char>((value >> shift) & 255));
}
}

struct MetadataRequester::State : std::enable_shared_from_this<State>
{
    struct Job;
    struct Limiter
    {
        double tokens = 4;
        Clock::time_point updated;
        Clock::time_point expires;
        std::list<std::string>::iterator position;
    };

    libtorrent::io_context& context;
    Config config;
    Route route;
    std::array<char, 20> peerId;
    std::mutex gate;
    bool cancelled = false;
    std::function<void()> wake;
    std::mutex mutex;
    std::vector<Result> completed;
    std::atomic<std::size_t> active{0};
    std::map<std::uintptr_t, std::weak_ptr<Job>> jobs;
    std::map<std::string, Limiter> limiters;
    std::list<std::string> recent;

    State(libtorrent::io_context& io, Config settings, Route path, std::function<void()> notify) :
        context(io), config(settings), route(std::move(path)), wake(std::move(notify))
    {
        std::random_device random;
        for (auto& byte : peerId)
            byte = static_cast<char>(random());
    }

    Clock::duration Reserve(std::string const& address)
    {
        auto now = Clock::now();
        auto found = limiters.find(address);
        if (found != limiters.end() && found->second.expires <= now)
        {
            recent.erase(found->second.position);
            limiters.erase(found);
            found = limiters.end();
        }
        if (found == limiters.end())
        {
            // Bitmagnet's limiter cache is fixed at 1,000; KeyMutexSize is unused there.
            if (limiters.size() == 1000)
            {
                limiters.erase(recent.back());
                recent.pop_back();
            }
            recent.push_front(address);
            found = limiters.emplace(address, Limiter{4, now, now + std::chrono::seconds(20), recent.begin()}).first;
        }
        auto& limiter = found->second;
        recent.splice(recent.begin(), recent, limiter.position);
        limiter.tokens = std::min(4.0, limiter.tokens + std::chrono::duration<double>(now - limiter.updated).count() * 2);
        limiter.updated = now;
        limiter.tokens -= 1;
        return std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(std::max(0.0, -limiter.tokens / 2)));
    }

    void Complete(Result result)
    {
        jobs.erase(result.requestId);
        {
            std::lock_guard lock(mutex);
            completed.push_back(std::move(result));
        }
        --active;
        std::lock_guard lock(gate);
        if (wake)
            wake();
    }

    void Stop();
};

struct MetadataRequester::State::Job : std::enable_shared_from_this<Job>
{
    std::shared_ptr<State> state;
    Result result;
    std::vector<libtorrent::tcp::endpoint> peers;
    std::size_t next = 0;
    Socket socket;
    boost::asio::steady_timer timer;
    boost::asio::steady_timer dial;
    Clock::time_point started = Clock::now();
    Clock::time_point waiting;
    Clock::time_point endpointAt;
    Clock::time_point dialDeadline;
    std::size_t physical = 0;
    std::uint64_t generation = 0;
    std::uint64_t connection = 0;
    MetadataStage stage = MetadataStage::Connect;
    bool done = false;
    bool timedOut = false;
    bool dialExpired = false;
    std::vector<char> frame;
    std::vector<char> info;
    std::vector<bool> received;
    std::size_t remaining = 0;
    unsigned extension = 0;
    std::string response;

    Job(std::shared_ptr<State> owner, Result value, std::vector<libtorrent::tcp::endpoint> endpoints) :
        state(std::move(owner)), result(std::move(value)), peers(std::move(endpoints)),
        socket(state->context), timer(state->context), dial(state->context) {}

    bool Stopped()
    {
        std::lock_guard lock(state->gate);
        return state->cancelled;
    }

    void Close()
    {
        Error ignored;
        timer.cancel();
        dial.cancel();
        socket.close(ignored);
    }

    void Finish(std::optional<MetadataStage> failure)
    {
        if (done)
            return;
        done = true;
        Close();
        result.failure = failure;
        result.elapsed = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - started);
        state->Complete(std::move(result));
    }

    void Fail(MetadataStage failure, Error error = {})
    {
        if (done)
            return;
        Close();
        auto& attempt = result.attempts.back();
        attempt.failure = failure;
        attempt.error = timedOut ? boost::asio::error::timed_out : error.value();
        attempt.elapsed = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - endpointAt);
        if (Stopped())
            Finish(MetadataStage::Cancelled);
        else
            Next();
    }

    void Next()
    {
        if (Stopped())
        {
            Finish(MetadataStage::Cancelled);
            return;
        }
        while (next < peers.size() && !peers[next].address().is_v4())
            ++next;
        if (next == peers.size())
        {
            Finish(result.attempts.empty() ? MetadataStage::Connect : result.attempts.back().failure);
            return;
        }
        auto const& route = state->route;
        if (!route.allowed || (route.proxy != ProxyType::None && route.endpoints.empty()))
        {
            Finish(MetadataStage::Route);
            return;
        }
        auto peer = peers[next++];
        result.attempts.push_back(Attempt{.peer = peer});
        info.clear();
        received.clear();
        timedOut = false;
        ++generation;
        waiting = Clock::now();
        auto delay = state->Reserve(peer.address().to_string());
        timer.expires_after(delay);
        timer.async_wait([self = shared_from_this()](Error error)
        {
            if (self->done)
                return;
            if (error || self->Stopped())
                self->Finish(MetadataStage::Cancelled);
            else
                self->Connect();
        });
    }

    bool Bind(libtorrent::tcp::endpoint const& physical, Error& error)
    {
        auto const& route = state->route;
        socket.open(physical.protocol(), error);
        if (error)
            return false;
        socket.set_option(boost::asio::socket_base::linger(true, 0), error);
        if (!error)
            socket.set_option(libtorrent::tcp::no_delay(true), error);
        if (error || !route.adapter)
            return !error;
        bool ipv4 = physical.address().is_v4();
        auto index = ipv4 ? htonl(route.adapter->ipv4Index) : route.adapter->ipv6Index;
        if (!index)
        {
            error = boost::asio::error::address_family_not_supported;
            return false;
        }
        if (setsockopt(socket.native_handle(), ipv4 ? IPPROTO_IP : IPPROTO_IPV6,
            ipv4 ? IP_UNICAST_IF : IPV6_UNICAST_IF, reinterpret_cast<char const*>(&index), sizeof(index)) != 0)
        {
            error = Error(WSAGetLastError(), boost::system::system_category());
            return false;
        }
        for (auto const& text : route.adapter->addresses)
        {
            auto address = libtorrent::make_address(text, error);
            if (!error && address.is_v4() == ipv4)
            {
                if (!ipv4 && address.to_v6().is_link_local() && !physical.address().to_v6().is_link_local())
                    continue;
                socket.bind({address, 0}, error);
                if (!error)
                    return true;
            }
        }
        error = boost::asio::error::address_family_not_supported;
        return false;
    }

    void Expire(std::uint64_t current)
    {
        if (done || generation != current)
            return;
        timedOut = true;
        Error ignored;
        socket.close(ignored);
    }

    void Connect()
    {
        endpointAt = Clock::now();
        result.attempts.back().waiting = std::chrono::duration_cast<std::chrono::microseconds>(endpointAt - waiting);
        stage = MetadataStage::Connect;
        timer.expires_after(state->config.requestTimeout);
        timer.async_wait([self = shared_from_this(), current = generation](Error error)
        {
            if (!error)
                self->Expire(current);
        });
        dialDeadline = endpointAt + std::chrono::seconds(3);
        physical = 0;
        Dial();
    }

    void DialFailed(MetadataStage failure, Error error)
    {
        dial.cancel();
        if (!timedOut && state->route.proxy != ProxyType::None &&
            physical < state->route.endpoints.size() && Clock::now() < dialDeadline)
            Dial();
        else
        {
            timedOut = timedOut || dialExpired || Clock::now() >= dialDeadline;
            Fail(failure, error);
        }
    }

    void Dial()
    {
        if (Stopped())
        {
            Finish(MetadataStage::Cancelled);
            return;
        }
        auto now = Clock::now();
        if (now >= dialDeadline)
        {
            timedOut = true;
            Fail(MetadataStage::Connect);
            return;
        }
        auto const& route = state->route;
        auto endpoint = route.proxy == ProxyType::None ? result.attempts.back().peer : route.endpoints[physical++];
        auto remaining = route.proxy == ProxyType::None ? std::size_t(1) : route.endpoints.size() - physical + 1;
        auto current = ++connection;
        auto epoch = generation;
        dialExpired = false;
        Error error;
        socket.close(error);
        error.clear();
        if (!Bind(endpoint, error))
        {
            DialFailed(MetadataStage::Route, error);
            return;
        }
        dial.expires_at(now + (dialDeadline - now) / remaining);
        dial.async_wait([self = shared_from_this(), epoch, current](Error error)
        {
            if (!error && !self->done && self->generation == epoch && self->connection == current &&
                self->stage == MetadataStage::Connect)
            {
                self->dialExpired = true;
                Error ignored;
                self->socket.close(ignored);
            }
        });
        bool cancelled;
        {
            std::lock_guard lock(state->gate);
            cancelled = state->cancelled;
            if (!cancelled)
                socket.async_connect(endpoint,
                    [self = shared_from_this(), epoch, current](Error error)
                {
                    if (self->done || self->generation != epoch || self->connection != current)
                        return;
                    if (self->dialExpired || self->timedOut)
                        error = boost::asio::error::timed_out;
                    if (error)
                        self->DialFailed(MetadataStage::Connect, error);
                    else
                    {
                        auto connected = ++self->connection;
                        self->dial.cancel();
                        self->dial.expires_at(self->dialDeadline);
                        self->dial.async_wait([self, epoch, connected](Error error)
                        {
                            if (!error && self->generation == epoch && self->connection == connected &&
                                self->stage == MetadataStage::Connect)
                                self->Expire(epoch);
                        });
                        self->Tunnel();
                    }
                });
        }
        if (cancelled)
            Finish(MetadataStage::Cancelled);
    }

    void Write(std::vector<char> bytes, std::function<void()> continuation)
    {
        auto buffer = std::make_shared<std::vector<char>>(std::move(bytes));
        Send(std::move(buffer), 0, std::move(continuation));
    }

    void Send(std::shared_ptr<std::vector<char>> buffer, std::size_t offset,
        std::function<void()> continuation)
    {
        bool cancelled;
        {
            std::lock_guard lock(state->gate);
            cancelled = state->cancelled;
            if (!cancelled)
                socket.async_write_some(boost::asio::buffer(buffer->data() + offset, buffer->size() - offset),
                    [self = shared_from_this(), buffer, offset, continuation = std::move(continuation)](Error error, std::size_t count)
                {
                    if (self->done)
                        return;
                    if (error)
                        self->Fail(self->stage, error);
                    else if (count == 0)
                        self->Fail(self->stage, boost::asio::error::eof);
                    else if (offset + count < buffer->size())
                        self->Send(buffer, offset + count, std::move(continuation));
                    else
                        continuation();
                });
        }
        if (cancelled)
            Finish(MetadataStage::Cancelled);
    }

    void Read(std::size_t size, std::function<void()> continuation)
    {
        frame.resize(size);
        boost::asio::async_read(socket, boost::asio::buffer(frame),
            [self = shared_from_this(), continuation = std::move(continuation)](Error error, std::size_t count)
        {
            if (self->done)
                return;
            self->result.attempts.back().received += count;
            if (error)
                self->Fail(self->stage, error);
            else if (self->Stopped())
                self->Finish(MetadataStage::Cancelled);
            else
                continuation();
        });
    }

    void Connected()
    {
        dial.cancel();
        result.attempts.back().connected = true;
        Handshake();
    }

    void Tunnel()
    {
        auto const& route = state->route;
        auto peer = result.attempts.back().peer;
        if (route.proxy == ProxyType::None)
            Connected();
        else if (route.proxy == ProxyType::Http)
        {
            auto target = peer.address().to_string() + ":" + std::to_string(peer.port());
            auto request = "CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\n";
            if (!route.username.empty())
                request += "Proxy-Authorization: Basic " + Base64(route.username + ":" + route.password) + "\r\n";
            request += "\r\n";
            response.clear();
            Write({request.begin(), request.end()}, [self = shared_from_this()] { self->Http(); });
        }
        else if (route.proxy == ProxyType::Socks4)
        {
            if (route.username.find('\0') != std::string::npos)
            {
                Fail(MetadataStage::Route);
                return;
            }
            std::vector<char> request{4, 1, static_cast<char>(peer.port() >> 8), static_cast<char>(peer.port())};
            auto address = peer.address().to_v4().to_bytes();
            request.insert(request.end(), address.begin(), address.end());
            request.insert(request.end(), route.username.begin(), route.username.end());
            request.push_back(0);
            Write(std::move(request), [self = shared_from_this()]
            {
                self->Read(8, [self]
                {
                    if (self->frame[0] == 0 && static_cast<unsigned char>(self->frame[1]) == 90)
                        self->Connected();
                    else
                        self->Fail(MetadataStage::Connect);
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
                    if (self->frame[0] != 5)
                        self->Fail(MetadataStage::Connect);
                    else if (self->frame[1] == 0)
                        self->Socks();
                    else if (self->frame[1] == 2 && !self->state->route.username.empty())
                    {
                        auto const& route = self->state->route;
                        if (route.username.size() > 255 || route.password.size() > 255)
                        {
                            self->Fail(MetadataStage::Route);
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
                                if (self->frame[0] == 1 && self->frame[1] == 0)
                                    self->Socks();
                                else
                                    self->Fail(MetadataStage::Connect);
                            });
                        });
                    }
                    else
                        self->Fail(MetadataStage::Connect);
                });
            });
        }
    }

    void Http()
    {
        Read(1, [self = shared_from_this()]
        {
            self->response.push_back(self->frame[0]);
            if (self->response.ends_with("\r\n\r\n"))
            {
                if (self->response.starts_with("HTTP/1.") && self->response.size() >= 12 &&
                    self->response.substr(9, 3) == "200")
                    self->Connected();
                else
                    self->Fail(MetadataStage::Connect);
            }
            else if (self->response.size() >= 16 * 1024)
                self->Fail(MetadataStage::Connect);
            else
                self->Http();
        });
    }

    void Socks()
    {
        auto peer = result.attempts.back().peer;
        std::vector<char> request{5, 1, 0, 1};
        auto address = peer.address().to_v4().to_bytes();
        request.insert(request.end(), address.begin(), address.end());
        request.push_back(static_cast<char>(peer.port() >> 8));
        request.push_back(static_cast<char>(peer.port()));
        Write(std::move(request), [self = shared_from_this()]
        {
            self->Read(4, [self]
            {
                if (self->frame[0] != 5 || self->frame[1] != 0 || self->frame[2] != 0)
                {
                    self->Fail(MetadataStage::Connect);
                    return;
                }
                auto type = self->frame[3];
                if (type == 1 || type == 4)
                    self->Read(type == 1 ? 6 : 18, [self] { self->Connected(); });
                else if (type == 3)
                    self->Read(1, [self]
                    {
                        auto count = static_cast<unsigned char>(self->frame[0]);
                        self->Read(count + 2, [self] { self->Connected(); });
                    });
                else
                    self->Fail(MetadataStage::Connect);
            });
        });
    }

    void Handshake()
    {
        stage = MetadataStage::Handshake;
        std::vector<char> bytes(protocol, protocol + 20);
        bytes.resize(28);
        bytes[25] = 0x10;
        bytes[27] = 1;
        auto hash = result.hash.to_string();
        bytes.insert(bytes.end(), hash.begin(), hash.end());
        bytes.insert(bytes.end(), state->peerId.begin(), state->peerId.end());
        Write(std::move(bytes), [self = shared_from_this()]
        {
            self->Read(68, [self]
            {
                auto const& bytes = self->frame;
                auto hash = self->result.hash.to_string();
                if (std::memcmp(bytes.data(), protocol, 20) != 0 || !(bytes[25] & 0x10) ||
                    !std::equal(hash.begin(), hash.end(), bytes.begin() + 28))
                {
                    self->Fail(MetadataStage::Handshake);
                    return;
                }
                self->stage = MetadataStage::Extension;
                constexpr char handshake[] = "\0\0\0\x1a\x14\0d1:md11:ut_metadatai1eee";
                self->Write({handshake, handshake + sizeof(handshake) - 1}, [self] { self->Message(); });
            });
        });
    }

    void Message()
    {
        Read(4, [self = shared_from_this()]
        {
            std::size_t size = 0;
            for (unsigned char byte : self->frame)
                size = (size << 8) | byte;
            if (size > metadataLimit)
            {
                self->Fail(self->stage);
                return;
            }
            if (size == 0)
                self->Message();
            else
                self->Read(size, [self] { self->Decode(); });
        });
    }

    void Decode()
    {
        if (frame.size() < 2 || frame[0] != 20)
        {
            Message();
            return;
        }
        if (stage == MetadataStage::Receive && frame[1] != 1)
        {
            Message();
            return;
        }
        Error error;
        auto node = libtorrent::bdecode(libtorrent::span<char const>(frame.data() + 2, frame.size() - 2), error);
        if (error || node.type() != libtorrent::bdecode_node::dict_t)
        {
            Fail(stage, error);
            return;
        }
        if (stage == MetadataStage::Extension)
        {
            auto size = node.dict_find_int_value("metadata_size", 0);
            auto methods = node.dict_find_dict("m");
            auto method = methods ? methods.dict_find_int_value("ut_metadata", 0) : 0;
            if (frame[1] != 0 || size <= 0 || size >= static_cast<std::int64_t>(metadataLimit) || method <= 0 || method > 255)
            {
                Fail(stage);
                return;
            }
            result.attempts.back().metadataSupported = true;
            info.resize(static_cast<std::size_t>(size));
            received.assign((info.size() + blockSize - 1) / blockSize, false);
            remaining = received.size();
            extension = static_cast<unsigned>(method);
            stage = MetadataStage::Receive;
            std::vector<char> requests;
            for (std::size_t piece = 0; piece < received.size(); ++piece)
            {
                auto dictionary = "d8:msg_typei0e5:piecei" + std::to_string(piece) + "ee";
                Length(requests, dictionary.size() + 2);
                requests.push_back(20);
                requests.push_back(static_cast<char>(extension));
                requests.insert(requests.end(), dictionary.begin(), dictionary.end());
            }
            Write(std::move(requests), [self = shared_from_this()] { self->Message(); });
            return;
        }
        auto type = node.dict_find_int_value("msg_type", -1);
        if (type == 2)
        {
            Fail(stage);
            return;
        }
        if (type != 1)
        {
            Message();
            return;
        }
        auto piece = node.dict_find_int_value("piece", -1);
        auto offset = 2 + static_cast<std::size_t>(node.data_section().size());
        if (piece < 0 || static_cast<std::size_t>(piece) >= received.size() || offset > frame.size())
        {
            Fail(stage);
            return;
        }
        auto index = static_cast<std::size_t>(piece);
        auto count = std::min(blockSize, info.size() - index * blockSize);
        if (frame.size() - offset != count || node.dict_find_int_value("total_size", info.size()) != static_cast<std::int64_t>(info.size()))
        {
            Fail(stage);
            return;
        }
        if (!received[index])
        {
            std::copy(frame.begin() + offset, frame.end(), info.begin() + index * blockSize);
            received[index] = true;
            --remaining;
        }
        if (remaining != 0)
            Message();
        else
        {
            stage = MetadataStage::Validate;
            if (libtorrent::hasher(libtorrent::span<char const>(info)).final() != result.hash)
            {
                Fail(stage);
                return;
            }
            result.attempts.back().elapsed = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - endpointAt);
            result.info = std::move(info);
            Finish(std::nullopt);
        }
    }
};

void MetadataRequester::State::Stop()
{
    for (auto const& [id, reference] : jobs)
        if (auto job = reference.lock())
            job->Close();
}

MetadataRequester::MetadataRequester(libtorrent::io_context& context, Config config, Route route,
    std::function<void()> wake) : state_(std::make_shared<State>(context, config, std::move(route), std::move(wake))) {}

MetadataRequester::~MetadataRequester()
{
    {
        std::lock_guard lock(state_->gate);
        state_->wake = {};
    }
    Cancel();
}

void MetadataRequester::Request(std::uintptr_t requestId, libtorrent::sha1_hash hash,
    std::vector<libtorrent::tcp::endpoint> peers)
{
    ++state_->active;
    boost::asio::post(state_->context, [state = state_, requestId, hash, peers = std::move(peers)]() mutable
    {
        auto job = std::make_shared<State::Job>(state, Result{.requestId = requestId, .hash = hash}, std::move(peers));
        state->jobs[requestId] = job;
        job->Next();
    });
}

void MetadataRequester::Cancel()
{
    {
        std::lock_guard lock(state_->gate);
        state_->cancelled = true;
    }
    boost::asio::post(state_->context, [state = state_] { state->Stop(); });
}

std::vector<MetadataRequester::Result> MetadataRequester::Drain()
{
    std::lock_guard lock(state_->mutex);
    return std::exchange(state_->completed, {});
}

std::size_t MetadataRequester::Active() const
{
    return state_->active.load();
}
}
