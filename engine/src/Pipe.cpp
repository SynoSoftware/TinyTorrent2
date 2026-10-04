#include "Pipe.h"
#include <stdexcept>

namespace tiny
{
namespace
{
constexpr DWORD maximum = 16 * 1024 * 1024;

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
        if (io.deadline && GetTickCount64() >= io.deadline) return false;
        if ((io.stop && WaitForSingleObject(io.stop, 0) == WAIT_OBJECT_0) ||
            (io.cancel && WaitForSingleObject(io.cancel, 0) == WAIT_OBJECT_0)) return false;
        OVERLAPPED operation{};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        DWORD transferred = 0;
        bool success = writing ? WriteFile(io.handle, bytes, size, &transferred, &operation)
                               : ReadFile(io.handle, bytes, size, &transferred, &operation);
        if (!success && GetLastError() == ERROR_IO_PENDING)
        {
            HANDLE events[]{operation.hEvent, io.stop, io.cancel};
            auto count = io.cancel ? 3u : io.stop ? 2u : 1u;
            DWORD timeout = INFINITE;
            if (io.deadline)
            {
                auto now = GetTickCount64();
                timeout = now >= io.deadline ? 0 : static_cast<DWORD>(io.deadline - now);
            }
            if (WaitForMultipleObjects(count, events, FALSE, timeout) != WAIT_OBJECT_0)
                CancelIoEx(io.handle, &operation);
            success = GetOverlappedResult(io.handle, &operation, &transferred, TRUE);
        }
        CloseHandle(operation.hEvent);
        if (!success || transferred == 0) return false;
        bytes += transferred;
        size -= transferred;
    }
    return true;
}

bool Read(Io const& io, Json& message)
{
    DWORD size = 0;
    if (!Transfer(io, &size, sizeof(size), false) || size == 0 || size > maximum) return false;
    std::string bytes(size, '\0');
    if (!Transfer(io, bytes.data(), size, false)) return false;
    message = Json::parse(bytes, nullptr, false);
    return !message.is_discarded();
}

bool Write(Io const& io, std::string& bytes)
{
    DWORD size = static_cast<DWORD>(bytes.size());
    return Transfer(io, &size, sizeof(size), true) && Transfer(io, bytes.data(), size, true);
}
}

void Pipe::Connection::Send(Json message)
{
    std::lock_guard lock(mutex);
    if (closed) return;
    if (output.size() >= 4)
    {
        closed = true;
        SetEvent(cancel);
    }
    if (message.contains("request_id") && message["request_id"] == request_id) request_id = nullptr;
    if (!closed) output.push_back(std::move(message));
    changed.notify_one();
}

Pipe::Pipe(std::wstring name, SECURITY_ATTRIBUTES& security, Json hello, Dispatch dispatch)
    : hello_(std::move(hello)), dispatch_(std::move(dispatch))
{
    for (unsigned index = 0; index != 8; ++index)
    {
        auto handle = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED |
            (index == 0 ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0),
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            8, 65536, 65536, 0, &security);
        if (handle == INVALID_HANDLE_VALUE)
        {
            auto error = GetLastError();
            for (auto existing : handles_) CloseHandle(existing);
            CloseHandle(stop_);
            throw std::runtime_error("Cannot securely claim the named pipe: " + std::to_string(error));
        }
        handles_.push_back(handle);
    }
    for (auto handle : handles_) workers_.emplace_back([this, handle] { Serve(handle); });
}

Pipe::~Pipe() { Stop(); CloseHandle(stop_); }

void Pipe::Stop()
{
    if (stopping_.exchange(true)) return;
    SetEvent(stop_);
    for (auto& worker : workers_) worker.join();
    for (auto handle : handles_) CloseHandle(handle);
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
                CancelIoEx(handle, &operation);
            DWORD transferred = 0;
            connected = GetOverlappedResult(handle, &operation, &transferred, TRUE);
        }
        else if (!connected && GetLastError() == ERROR_PIPE_CONNECTED) connected = true;
        CloseHandle(operation.hEvent);
        if (!connected)
        {
            if (stopping_) break;
            DisconnectNamedPipe(handle);
            continue;
        }
        if (stopping_) break;
        auto client = std::make_shared<Connection>();
        client->handle = handle;
        client->connection_id = std::to_string(++sequence_);
        client->Send(hello_);
        std::thread writer([client, this]
        {
            std::unique_lock lock(client->mutex);
            while (!client->closed)
            {
                client->changed.wait(lock, [&] { return client->closed || !client->output.empty(); });
                if (client->closed) break;
                auto message = std::move(client->output.front());
                client->output.pop_front();
                lock.unlock();
                auto bytes = message.dump(-1, ' ', false, Json::error_handler_t::replace);
                if (bytes.size() > maximum)
                    bytes = Json{{"request_id", message.value("request_id", Json())}, {"ok", false},
                        {"error", {{"code", "response_too_large"}, {"detail", ""}}}}.dump();
                bool success = Write(Io{client->handle, stop_, client->cancel}, bytes);
                lock.lock();
                if (!success)
                {
                    client->closed = true;
                    SetEvent(client->cancel);
                }
            }
        });
        Json request;
        while (!stopping_ && Read(Io{handle, stop_, client->cancel}, request))
        {
            if (!request.is_object() || !request.contains("request_id") ||
                !request["request_id"].is_number_integer() || !request.contains("command") ||
                !request["command"].is_string()) break;
            request["connection_id"] = client->connection_id;
            bool occupied;
            {
                std::lock_guard lock(client->mutex);
                occupied = !client->request_id.is_null();
                if (!occupied) client->request_id = request["request_id"];
            }
            if (occupied)
                break;
            dispatch_(client, std::move(request));
        }
        {
            std::lock_guard lock(client->mutex);
            client->closed = true;
            SetEvent(client->cancel);
            client->changed.notify_one();
        }
        writer.join();
        dispatch_(client, nullptr);
        DisconnectNamedPipe(handle);
    }
}

bool Pipe::Forward(std::wstring const& name)
{
    auto deadline = GetTickCount64() + 5000;
    while (!WaitNamedPipeW(name.c_str(), 100))
    {
        if (GetTickCount64() >= deadline) return false;
        Sleep(25);
    }
    auto handle = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
        OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return false;
    Json hello;
    Io io{handle, nullptr, nullptr, GetTickCount64() + 5000};
    bool success = Read(io, hello) && hello.value("version", 0) == 1;
    if (success)
    {
        ULONG process = 0;
        if (GetNamedPipeServerProcessId(handle, &process)) AllowSetForegroundWindow(process);
        auto bytes = Json{{"request_id", 1}, {"command", "open"}}.dump();
        Json reply;
        success = Write(io, bytes) && Read(io, reply) && reply.value("ok", false);
    }
    CloseHandle(handle);
    return success;
}
}
