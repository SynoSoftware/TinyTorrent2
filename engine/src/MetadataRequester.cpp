#include "MetadataRequester.h"
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
    std::shared_ptr<Dialer::Route const> route;
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

    State(libtorrent::io_context& io, Config settings, Dialer::Route path, std::function<void()> notify) :
        context(io), config(settings), route(std::make_shared<Dialer::Route const>(std::move(path))),
        wake(std::move(notify))
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
    std::shared_ptr<Dialer> dialer;
    Clock::time_point started = Clock::now();
    Clock::time_point waiting;
    Clock::time_point endpointAt;
    std::uint64_t generation = 0;
    MetadataStage stage = MetadataStage::Connect;
    bool done = false;
    bool timedOut = false;
    std::vector<char> frame;
    std::vector<char> info;
    std::vector<bool> received;
    std::size_t remaining = 0;
    unsigned extension = 0;

    Job(std::shared_ptr<State> owner, Result value, std::vector<libtorrent::tcp::endpoint> endpoints) :
        state(std::move(owner)), result(std::move(value)), peers(std::move(endpoints)),
        socket(state->context), timer(state->context) {}

    bool Stopped()
    {
        std::lock_guard lock(state->gate);
        return state->cancelled;
    }

    void Close()
    {
        Error ignored;
        timer.cancel();
        if (dialer)
            std::exchange(dialer, {})->Close();
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
        if (!Dialer::Usable(*state->route))
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

    void Expire(std::uint64_t current)
    {
        if (done || generation != current)
            return;
        timedOut = true;
        if (dialer)
            Fail(MetadataStage::Connect, boost::asio::error::timed_out);
        else
        {
            Error ignored;
            socket.close(ignored);
        }
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
        dialer = std::make_shared<Dialer>(state->context, state->route);
        dialer->Connect(result.attempts.back().peer, endpointAt + std::chrono::seconds(3),
            [self = shared_from_this(), epoch = generation](std::optional<Dialer::Failure> failure, Error error)
        {
            if (self->done || self->generation != epoch)
                return;
            auto dialed = std::exchange(self->dialer, {});
            if (failure)
            {
                self->timedOut = error == boost::asio::error::timed_out;
                self->Fail(*failure == Dialer::Failure::Route ? MetadataStage::Route : MetadataStage::Connect, error);
            }
            else if (self->Stopped())
                self->Finish(MetadataStage::Cancelled);
            else
            {
                self->socket = std::move(dialed->Socket());
                self->Connected();
            }
        });
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
        result.attempts.back().connected = true;
        Handshake();
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
    // A closed Dialer never calls back, so a job that is connecting would never
    // finish on its own. Finish removes the job from `jobs`, so walk a copy.
    auto running = jobs;
    for (auto const& [id, reference] : running)
        if (auto job = reference.lock())
            job->Finish(MetadataStage::Cancelled);
}

MetadataRequester::MetadataRequester(libtorrent::io_context& context, Config config, Dialer::Route route,
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
