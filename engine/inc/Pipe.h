#pragma once

#include "Engine.h"
#include <windows.h>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <sddl.h>
#include <stdexcept>
#include <thread>
#include <vector>

namespace tt
{
// Security attributes that grant full access to the logon session `sid` and
// to no one else. They stay valid for the lifetime of this object.
class Security
{
public:
    explicit Security(std::wstring const& sid)
    {
        auto acl = L"D:P(A;;GA;;;" + sid + L")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(acl.c_str(), SDDL_REVISION_1,
            &attributes_.lpSecurityDescriptor, nullptr))
        {
            throw std::runtime_error("Cannot create the security descriptor.");
        }
    }
    ~Security() { LocalFree(attributes_.lpSecurityDescriptor); }
    Security(Security const&) = delete;
    Security& operator=(Security const&) = delete;
    SECURITY_ATTRIBUTES& Attributes() { return attributes_; }

private:
    SECURITY_ATTRIBUTES attributes_{sizeof(SECURITY_ATTRIBUTES), nullptr, FALSE};
};

class Pipe
{
public:
    struct Connection
    {
        // Read when the client connects, because the pipe instance serves
        // other clients after this one disconnects.
        ULONG process = 0;
        HANDLE cancel = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        std::string connectionId;
        std::mutex mutex;
        std::condition_variable changed;
        std::deque<Json> output;
        bool closed = false;
        bool dispatched = false;
        Json requestId;
        ~Connection() { CloseHandle(cancel); }
        void Send(Json message);
    };
    // The protocol version that the hello message announces.
    static constexpr int version = 1;
    using Client = std::shared_ptr<Connection>;
    // Receives each request with the reply that answers it, and a null request
    // with no reply when the client disconnects.
    using Dispatch = std::function<void(Client, Json, Reply)>;

    Pipe(std::wstring const& sid, SECURITY_ATTRIBUTES& security, Json hello, Dispatch dispatch);
    ~Pipe();
    Pipe(Pipe const&) = delete;
    Pipe& operator=(Pipe const&) = delete;
    void Stop();
    static bool Forward(std::wstring const& sid, Json request = {{"command", "open"}});

private:
    static std::wstring Name(std::wstring const& sid);
    void Serve(HANDLE handle);
    void Deliver(Client client, HANDLE handle);
    Json hello_;
    Dispatch dispatch_;
    std::atomic<bool> stopping_ = false;
    HANDLE stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::atomic<unsigned> sequence_ = 0;
    std::vector<HANDLE> handles_;
    std::vector<std::thread> workers_;
};
}
