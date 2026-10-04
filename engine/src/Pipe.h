#pragma once

#include "Engine.h"
#include <windows.h>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

namespace tiny
{
class Pipe
{
public:
    struct Connection
    {
        HANDLE handle = INVALID_HANDLE_VALUE;
        HANDLE cancel = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        std::string connection_id;
        std::mutex mutex;
        std::condition_variable changed;
        std::deque<Json> output;
        bool closed = false;
        bool dispatched = false;
        Json request_id;
        ~Connection() { CloseHandle(cancel); }
        void Send(Json message);
    };
    using Client = std::shared_ptr<Connection>;
    using Dispatch = std::function<void(Client, Json)>;

    Pipe(std::wstring name, SECURITY_ATTRIBUTES& security, Json hello, Dispatch dispatch);
    ~Pipe();
    Pipe(Pipe const&) = delete;
    Pipe& operator=(Pipe const&) = delete;
    void Stop();
    static bool Forward(std::wstring const& name);

private:
    void Serve(HANDLE handle);
    Json hello_;
    Dispatch dispatch_;
    std::atomic<bool> stopping_ = false;
    HANDLE stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::atomic<unsigned> sequence_ = 0;
    std::vector<HANDLE> handles_;
    std::vector<std::thread> workers_;
};
}
