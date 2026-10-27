#include "Rats/Connection.h"
#include <boost/asio/post.hpp>
#include <boost/asio/write.hpp>
#include <array>
#include <deque>
#include <mutex>
#include <utility>

namespace tt::rats
{
namespace
{
using Error = libtorrent::error_code;
constexpr auto dialTime = std::chrono::seconds(10);
}

struct Connection::State : std::enable_shared_from_this<State>
{
    libtorrent::io_context& context;
    std::shared_ptr<Dialer::Route const> route;
    libtorrent::tcp::endpoint peer;
    libtorrent::tcp::socket socket;
    std::shared_ptr<Dialer> dialer;
    Session session;
    // The front buffer is the one being written.
    std::deque<std::vector<char>> outgoing;
    std::array<char, 64 * 1024> incoming;
    bool connected = false;
    bool closing = false;
    bool ended = false;
    std::mutex mutex;
    std::function<void()> wake;
    Update update;

    State(libtorrent::io_context& io, std::shared_ptr<Dialer::Route const> path,
        libtorrent::tcp::endpoint endpoint, std::function<void()> notify) :
        context(io), route(std::move(path)), peer(endpoint), socket(io), wake(std::move(notify)) {}

    void Start()
    {
        dialer = std::make_shared<Dialer>(context, route);
        dialer->Connect(peer, std::chrono::steady_clock::now() + dialTime,
            [self = shared_from_this()](std::optional<Dialer::Failure> failure, Error error)
        {
            auto dialed = std::exchange(self->dialer, {});
            if (failure)
            {
                self->End(*failure == Dialer::Failure::Route ? "route unavailable" : "connect failed: " + error.message());
                return;
            }
            self->socket = std::move(dialed->Socket());
            self->connected = true;
            self->Run([self] { return self->session.Open(); });
            self->Read();
        });
    }

    // Sends what a Session call returns; a refused call ends the connection.
    template <typename Call>
    void Run(Call call)
    {
        try
        {
            Send(call());
        }
        catch (std::exception const& exception)
        {
            End(exception.what());
        }
    }

    void Send(std::vector<char> bytes)
    {
        // Once Shutdown has run, nothing more can be sent.
        if (ended || bytes.empty() || (closing && outgoing.empty()))
            return;
        outgoing.push_back(std::move(bytes));
        if (outgoing.size() == 1)
            Write();
    }

    void Write()
    {
        boost::asio::async_write(socket, boost::asio::buffer(outgoing.front()),
            [self = shared_from_this()](Error error, std::size_t)
        {
            if (self->ended)
                return;
            if (error)
            {
                self->End("send failed: " + error.message());
                return;
            }
            self->outgoing.pop_front();
            if (!self->outgoing.empty())
                self->Write();
            else if (self->closing)
                self->Shutdown();
        });
    }

    void Read()
    {
        socket.async_read_some(boost::asio::buffer(incoming),
            [self = shared_from_this()](Error error, std::size_t count)
        {
            if (self->ended)
                return;
            if (error)
            {
                if (error != boost::asio::error::eof)
                    self->End("receive failed: " + error.message());
                else
                    self->End(self->closing ? "closed" : "closed by peer");
                return;
            }
            std::vector<char> output;
            std::vector<Event> events;
            try
            {
                events = self->session.Receive({self->incoming.data(), count}, output);
            }
            catch (std::exception const& exception)
            {
                self->End(exception.what());
                return;
            }
            self->Send(std::move(output));
            self->Publish(std::move(events), std::nullopt);
            self->Read();
        });
    }

    void Close()
    {
        closing = true;
        if (!connected)
            End("closed");
        else if (outgoing.empty())
            Shutdown();
    }

    // Ends sending and keeps reading until the peer closes. Closing the socket
    // itself would reset the connection, because the Dialer sets a zero
    // linger and the peer may have sent more, and a reset can discard the
    // bytes just sent, such as the closing COMPLETE.
    void Shutdown()
    {
        Error error;
        socket.shutdown(libtorrent::tcp::socket::shutdown_send, error);
        if (error)
            End("send failed: " + error.message());
    }

    void End(std::string reason)
    {
        if (ended)
            return;
        ended = true;
        if (dialer)
            std::exchange(dialer, {})->Close();
        Error ignored;
        socket.close(ignored);
        Publish({}, std::move(reason));
    }

    void Publish(std::vector<Event> events, std::optional<std::string> closed)
    {
        if (events.empty() && !closed)
            return;
        std::lock_guard lock(mutex);
        update.events.insert(update.events.end(), std::make_move_iterator(events.begin()),
            std::make_move_iterator(events.end()));
        if (closed)
            update.closed = std::move(closed);
        if (wake)
            wake();
    }

    template <typename Call>
    static void Post(std::shared_ptr<State> const& state, Call call)
    {
        boost::asio::post(state->context, [state, call = std::move(call)]
        {
            if (!state->ended)
                call(*state);
        });
    }
};

Connection::Connection(libtorrent::io_context& context, std::shared_ptr<Dialer::Route const> route,
    libtorrent::tcp::endpoint peer, std::function<void()> wake) :
    state_(std::make_shared<State>(context, std::move(route), peer, std::move(wake)))
{
    State::Post(state_, [](State& state) { state.Start(); });
}

Connection::~Connection()
{
    {
        std::lock_guard lock(state_->mutex);
        state_->wake = {};
    }
    State::Post(state_, [](State& state) { state.End("closed"); });
}

void Connection::RequestBatch(int limit)
{
    State::Post(state_, [limit](State& state) { state.Run([&] { return state.session.RequestBatch(limit); }); });
}

void Connection::RequestSnapshot()
{
    State::Post(state_, [](State& state) { state.Run([&] { return state.session.RequestSnapshot(); }); });
}

void Connection::Acknowledge(std::int64_t bytes)
{
    State::Post(state_, [bytes](State& state) { state.Run([&] { return state.session.Acknowledge(bytes); }); });
}

void Connection::Cancel()
{
    State::Post(state_, [](State& state) { state.Run([&] { return state.session.Cancel(); }); });
}

void Connection::Close()
{
    State::Post(state_, [](State& state) { state.Close(); });
}

Connection::Update Connection::Drain()
{
    std::lock_guard lock(state_->mutex);
    return std::exchange(state_->update, {});
}
}
