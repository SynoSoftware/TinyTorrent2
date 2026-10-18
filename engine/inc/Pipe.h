#pragma once

#include "Engine.h"
#include "Owned.h"
#include <windows.h>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <mutex>
#include <optional>
#include <set>
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

// How a forwarded request ended, and the running engine's executable when
// Windows reveals it. The process handle keeps the prompt to exit that engine
// tied to it, even if another engine starts while the prompt is open.
struct Forwarded
{
    Forwarding outcome = Forwarding::Refused;
    std::optional<std::filesystem::path> engine;
    OwnedHandle process;
};

class Pipe
{
public:
    struct Connection
    {
        // Read when the client connects, because the pipe instance serves
        // other clients after this one disconnects.
        ULONG processId = 0;
        HANDLE cancel = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        std::string connectionId;
        std::mutex mutex;
        std::condition_variable changed;
        std::deque<Json> output;
        std::optional<Json> detail;
        std::optional<Json> snapshot;
        bool closed = false;
        bool dispatched = false;
        std::set<std::int64_t> requests;
        ~Connection() { CloseHandle(cancel); }
        void Send(Json message);
    };
    // The protocol version that the hello message announces.
    static constexpr int version = 20;
    // Receives each request with the reply that answers it, and a null request
    // with no reply when the client disconnects.
    using Dispatch = std::function<void(std::shared_ptr<Connection>, Json, Reply)>;

    Pipe(std::wstring const& sid, SECURITY_ATTRIBUTES& security, Json hello, Dispatch dispatch);
    ~Pipe();
    Pipe(Pipe const&) = delete;
    Pipe& operator=(Pipe const&) = delete;
    void Stop();
    // With a store, the request is not sent to an engine that uses another data
    // folder, because it would open or add to that folder's torrents.
    // With a process ID, the request goes only to that engine, such as the one
    // the person chose to exit.
    static Forwarded Forward(std::wstring const& sid, Json request,
        std::optional<std::filesystem::path> const& store = std::nullopt,
        std::optional<DWORD> processId = std::nullopt);

private:
    static std::wstring Name(std::wstring const& sid);
    void Serve(HANDLE handle);
    void Deliver(std::shared_ptr<Connection> client, HANDLE handle);
    Json hello_;
    Dispatch dispatch_;
    std::atomic<bool> stopping_ = false;
    HANDLE stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::atomic<unsigned> sequence_ = 0;
    std::vector<HANDLE> handles_;
    std::vector<std::thread> workers_;
};
}
