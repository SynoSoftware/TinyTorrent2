#include "Pipe.h"
#include "Owned.h"
#include <algorithm>
#include <limits>
#include <span>
#include <stdexcept>

namespace tt
{
namespace
{
constexpr DWORD messageLimit = 16 * 1024 * 1024;
// Each instance serves one connection at a time.
constexpr DWORD instances = 8;
constexpr DWORD bufferSize = 64 * 1024;
// PipeClient admits at most 32 commands plus 32 reads; this bound must cover both.
constexpr std::size_t requestLimit = 64;
// One engine turn can forward every notice while a reply is being written.
// Leave room for outstanding replies and control messages, then disconnect a slow client.
constexpr std::size_t outputLimit = Notice::limit + requestLimit + 8;

// The fields that the refusal of an oversized message keeps from it, so the
// client can tell which message was refused.
constexpr char const* replyIdentity[] = {"request_id"};
constexpr char const* detailIdentity[] = {"request_id", "type", "session_id", "torrent_id", "context"};
constexpr char const* snapshotIdentity[] = {"request_id", "type"};

// Durations in milliseconds, as GetTickCount64 counts.
constexpr ULONGLONG forwardTimeout = 5000;
constexpr DWORD connectWait = 100;
constexpr DWORD retryDelay = 25;

struct Io
{
    HANDLE handle;
    HANDLE stop;
    HANDLE cancel;
    ULONGLONG deadline = 0;
};

bool Transfer(Io const& io, void* buffer, DWORD size, bool writing)
{
    auto bytes = static_cast<char*>(buffer);
    while (size != 0)
    {
        if (io.deadline && GetTickCount64() >= io.deadline)
        {
            return false;
        }
        if ((io.stop && WaitForSingleObject(io.stop, 0) == WAIT_OBJECT_0) ||
            (io.cancel && WaitForSingleObject(io.cancel, 0) == WAIT_OBJECT_0))
        {
            return false;
        }
        OVERLAPPED operation{};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!operation.hEvent)
        {
            return false;
        }
        DWORD transferred = 0;
        bool success = writing ? WriteFile(io.handle, bytes, size, &transferred, &operation)
                               : ReadFile(io.handle, bytes, size, &transferred, &operation);
        if (!success && GetLastError() == ERROR_IO_PENDING)
        {
            std::vector<HANDLE> events{operation.hEvent};
            if (io.stop)
            {
                events.push_back(io.stop);
            }
            if (io.cancel)
            {
                events.push_back(io.cancel);
            }
            DWORD timeout = INFINITE;
            if (io.deadline)
            {
                auto now = GetTickCount64();
                timeout = now >= io.deadline ? 0 : static_cast<DWORD>(io.deadline - now);
            }
            if (WaitForMultipleObjects(static_cast<DWORD>(events.size()), events.data(),
                FALSE, timeout) != WAIT_OBJECT_0)
            {
                CancelIoEx(io.handle, &operation);
            }
            success = GetOverlappedResult(io.handle, &operation, &transferred, TRUE);
        }
        CloseHandle(operation.hEvent);
        if (!success || transferred == 0)
        {
            return false;
        }
        bytes += transferred;
        size -= transferred;
    }
    return true;
}

bool Read(Io const& io, Json& message)
{
    DWORD size = 0;
    if (!Transfer(io, &size, sizeof(size), false) || size == 0 || size > messageLimit)
    {
        return false;
    }
    std::string bytes(size, '\0');
    if (!Transfer(io, bytes.data(), size, false))
    {
        return false;
    }
    message = Json::parse(bytes, nullptr, false);
    return !message.is_discarded();
}

bool Write(Io const& io, std::string& bytes)
{
    DWORD size = static_cast<DWORD>(bytes.size());
    return Transfer(io, &size, sizeof(size), true) && Transfer(io, bytes.data(), size, true);
}
}

Pipe::Connection::Connection(ULONG processId, std::string connectionId)
    : processId(processId), connectionId(std::move(connectionId))
{
}

void Pipe::Connection::Send(Json message)
{
    std::lock_guard lock(mutex_);
    if (closed_ || std::ranges::find(output_, message) != output_.end())
    {
        return;
    }
    Queue(std::move(message));
}

void Pipe::Connection::Answer(std::int64_t requestId, Json message)
{
    message["request_id"] = requestId;
    std::lock_guard lock(mutex_);
    if (closed_)
    {
        return;
    }
    auto type = message.value("type", std::string());
    if (type == "detail")
    {
        detail_ = std::move(message);
        changed_.notify_one();
    }
    else if (type == "snapshot")
    {
        snapshot_ = std::move(message);
        changed_.notify_one();
    }
    else
    {
        requests_.erase(requestId);
        Queue(std::move(message));
    }
}

void Pipe::Connection::Queue(Json message)
{
    if (output_.size() >= outputLimit)
    {
        Close();
        return;
    }
    output_.push_back(std::move(message));
    changed_.notify_one();
}

void Pipe::Connection::Close()
{
    closed_ = true;
    SetEvent(cancel_);
    changed_.notify_one();
}

std::wstring Pipe::Name(std::wstring const& sid)
{
    return L"\\\\.\\pipe\\TinyTorrent." + sid;
}

Pipe::Pipe(std::wstring const& sid, SECURITY_ATTRIBUTES& security, Json hello, Dispatch dispatch)
    : hello_(std::move(hello)), dispatch_(std::move(dispatch))
{
    if (!stop_)
    {
        throw std::runtime_error("Cannot create the pipe stop event: " + std::to_string(GetLastError()));
    }
    auto name = Name(sid);
    for (DWORD index = 0; index != instances; ++index)
    {
        auto handle = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED |
            (index == 0 ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0),
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            instances, bufferSize, bufferSize, 0, &security);
        if (handle == INVALID_HANDLE_VALUE)
        {
            auto error = GetLastError();
            for (auto existing : handles_)
            {
                CloseHandle(existing);
            }
            CloseHandle(stop_);
            throw std::runtime_error("Cannot securely claim the named pipe: " + std::to_string(error));
        }
        handles_.push_back(handle);
    }
    for (auto handle : handles_)
    {
        workers_.emplace_back([this, handle] { Serve(handle); });
    }
}

Pipe::~Pipe()
{
    Stop();
    CloseHandle(stop_);
}

void Pipe::Stop()
{
    if (stopping_.exchange(true))
    {
        return;
    }
    SetEvent(stop_);
    for (auto& worker : workers_)
    {
        worker.join();
    }
    for (auto handle : handles_)
    {
        CloseHandle(handle);
    }
}

void Pipe::Serve(HANDLE handle)
{
    while (!stopping_)
    {
        OVERLAPPED operation{};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        bool connected = ConnectNamedPipe(handle, &operation);
        if (!connected && GetLastError() == ERROR_IO_PENDING)
        {
            HANDLE events[]{operation.hEvent, stop_};
            if (WaitForMultipleObjects(2, events, FALSE, INFINITE) != WAIT_OBJECT_0)
            {
                CancelIoEx(handle, &operation);
            }
            DWORD transferred = 0;
            connected = GetOverlappedResult(handle, &operation, &transferred, TRUE);
        }
        else if (!connected && GetLastError() == ERROR_PIPE_CONNECTED)
        {
            connected = true;
        }
        CloseHandle(operation.hEvent);
        if (!connected)
        {
            if (stopping_)
            {
                break;
            }
            DisconnectNamedPipe(handle);
            continue;
        }
        if (stopping_)
        {
            break;
        }
        ULONG processId = 0;
        GetNamedPipeClientProcessId(handle, &processId);
        auto client = std::make_shared<Connection>(processId, std::to_string(++sequence_));
        client->Send(hello_);
        std::thread writer([this, client, handle] { Deliver(client, handle); });
        bool dispatched = false;
        Json request;
        while (!stopping_ && Read(Io{handle, stop_, client->cancel_}, request))
        {
            if (!request.is_object() || !request.contains("request_id") ||
                !request["request_id"].is_number_integer() || !request.contains("command") ||
                !request["command"].is_string())
            {
                break;
            }
            if (request["request_id"].is_number_unsigned() &&
                request["request_id"].get<std::uint64_t>() > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))
                break;
            auto requestId = request["request_id"].get<std::int64_t>();
            bool occupied;
            {
                std::lock_guard lock(client->mutex_);
                occupied = client->requests_.size() >= requestLimit ||
                    !client->requests_.insert(requestId).second;
            }
            if (occupied)
            {
                break;
            }
            auto reply = [client, requestId](Json response) { client->Answer(requestId, std::move(response)); };
            dispatch_(client, std::move(request), std::move(reply));
            dispatched = true;
        }
        {
            std::lock_guard lock(client->mutex_);
            client->Close();
        }
        writer.join();
        if (dispatched)
        {
            dispatch_(client, nullptr, nullptr);
        }
        DisconnectNamedPipe(handle);
    }
}

void Pipe::Deliver(std::shared_ptr<Connection> client, HANDLE handle)
{
    std::unique_lock lock(client->mutex_);
    bool sentReply = false;
    bool sentSnapshot = false;
    while (!client->closed_)
    {
        client->changed_.wait(lock, [&]
        {
            return client->closed_ || !client->output_.empty() || client->detail_ || client->snapshot_;
        });
        if (client->closed_)
        {
            break;
        }
        Json message;
        std::span<char const* const> identity;
        if (!client->output_.empty() && (!sentReply || (!client->detail_ && !client->snapshot_)))
        {
            message = std::move(client->output_.front());
            client->output_.pop_front();
            identity = replyIdentity;
            sentReply = true;
        }
        else if (client->detail_ && (!client->snapshot_ || sentSnapshot))
        {
            message = std::move(*client->detail_);
            client->detail_.reset();
            identity = detailIdentity;
            sentReply = false;
            sentSnapshot = false;
        }
        else
        {
            message = std::move(*client->snapshot_);
            client->snapshot_.reset();
            identity = snapshotIdentity;
            sentReply = false;
            sentSnapshot = true;
        }
        lock.unlock();
        auto bytes = message.dump(-1, ' ', false, Json::error_handler_t::replace);
        if (bytes.size() > messageLimit)
        {
            auto refusal = Failure(ErrorCode::ResponseTooLarge);
            for (auto key : identity)
                refusal[key] = message.value(key, Json());
            bytes = refusal.dump();
        }
        bool success = Write(Io{handle, stop_, client->cancel_}, bytes);
        lock.lock();
        if (!success)
        {
            client->Close();
        }
    }
}

Forwarded Pipe::Forward(std::wstring const& sid, Json request,
    std::optional<std::filesystem::path> const& store, std::optional<DWORD> processId)
{
    auto name = Name(sid);
    auto deadline = GetTickCount64() + forwardTimeout;
    HANDLE handle = INVALID_HANDLE_VALUE;
    while (handle == INVALID_HANDLE_VALUE)
    {
        if (GetTickCount64() >= deadline)
        {
            return {};
        }
        if (WaitNamedPipeW(name.c_str(), connectWait))
        {
            handle = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED, nullptr);
        }
        if (handle != INVALID_HANDLE_VALUE)
        {
            break;
        }
        Sleep(retryDelay);
    }
    Forwarded forwarded;
    ULONG server = 0;
    bool identified = GetNamedPipeServerProcessId(handle, &server);
    if (processId && (!identified || server != *processId))
    {
        CloseHandle(handle);
        return forwarded;
    }
    if (identified)
    {
        forwarded.process.reset(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, server));
        if (forwarded.process)
        {
            forwarded.engine = ProcessImage(forwarded.process.get());
        }
    }
    Json hello;
    Io io{handle, nullptr, nullptr, GetTickCount64() + forwardTimeout};
    if (Read(io, hello))
    {
        forwarded.outcome = hello.value("version", 0) == version ? Forwarding::Accepted : Forwarding::OtherVersion;
    }
    if (forwarded.outcome == Forwarding::Accepted && store &&
        !SameFolder(Wide(hello.value("data_directory", std::string())), *store))
    {
        forwarded.outcome = Forwarding::OtherStore;
    }
    if (forwarded.outcome == Forwarding::Accepted)
    {
        if (identified)
        {
            AllowSetForegroundWindow(server);
        }
        request["request_id"] = 1;
        auto bytes = request.dump();
        Json reply;
        bool accepted = Write(io, bytes) && Read(io, reply) &&
            reply.value("request_id", 0) == 1 && reply.value("ok", false);
        forwarded.outcome = accepted ? Forwarding::Accepted : Forwarding::Refused;
    }
    CloseHandle(handle);
    return forwarded;
}
}
