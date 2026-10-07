#include "Store.h"
#include "Engine.h"
#include <Windows.h>
#include <fstream>
#include <stdexcept>

namespace tt
{
namespace
{
constexpr std::size_t jobLimit = 64;
// The largest file Store reads. Writes keep to it too, so every file written
// can be read back.
constexpr std::streamoff fileLimit = 16 * 1024 * 1024;
}

Store::Store(std::function<void()> wake) : wake_(std::move(wake)), worker_([this]
{
    for (;;)
    {
        Job job;
        {
            std::unique_lock lock(mutex_);
            ready_.wait(lock, [this] { return stopping_ || !jobs_.empty(); });
            if (jobs_.empty())
            {
                return;
            }
            job = std::move(jobs_.front());
            jobs_.pop_front();
            working_ = true;
        }
        StorageOutcome outcome;
        try
        {
            job.work();
            outcome.succeeded = true;
        }
        catch (std::exception const& error)
        {
            outcome.detail = error.what();
        }
        {
            std::lock_guard lock(mutex_);
            completed_.push_back({std::move(job.completion), std::move(outcome)});
            working_ = false;
        }
        idle_.notify_all();
        if (wake_)
        {
            wake_();
        }
    }
}) {}

Store::~Store()
{
    {
        std::lock_guard lock(mutex_);
        stopping_ = true;
    }
    ready_.notify_one();
    worker_.join();
}

void Store::Run(std::function<void()> work, std::function<void(StorageOutcome)> completion)
{
    bool rejected;
    {
        std::lock_guard lock(mutex_);
        rejected = jobs_.size() >= jobLimit;
        if (rejected)
        {
            completed_.push_back({std::move(completion), {false, "storage_overloaded"}});
        }
        else
        {
            jobs_.push_back({std::move(work), std::move(completion)});
        }
    }
    if (rejected)
    {
        if (wake_)
        {
            wake_();
        }
    }
    else
    {
        ready_.notify_one();
    }
}

void Store::Write(std::filesystem::path path, std::function<std::string()> encode,
    std::function<void(StorageOutcome)> completion)
{
    Run([path = std::move(path), encode = std::move(encode)]
    {
        auto bytes = encode();
        // A refused write keeps the previous file, which startup can still read.
        if (bytes.size() > static_cast<std::size_t>(fileLimit))
        {
            throw std::runtime_error("File exceeds limit");
        }
        auto temporary = path;
        temporary += L".tmp";
        HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr,
            CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            throw std::runtime_error("CreateFile: " + std::to_string(GetLastError()));
        }
        DWORD written = 0;
        bool success = WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()),
            &written, nullptr) && written == bytes.size();
        if (success)
        {
            success = FlushFileBuffers(file);
        }
        DWORD error = success ? 0 : GetLastError();
        CloseHandle(file);
        char const* operation = "WriteFile";
        if (success)
        {
            operation = "MoveFileEx";
            success = MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
            error = success ? 0 : GetLastError();
        }
        if (!success)
        {
            auto detail = std::string(operation) + ": " + std::to_string(error);
            if (!DeleteFileW(temporary.c_str()))
            {
                detail += "; DeleteFile: " + std::to_string(GetLastError());
            }
            throw std::runtime_error(detail);
        }
    }, std::move(completion));
}

void Store::Drain()
{
    std::deque<Completed> completed;
    {
        std::lock_guard lock(mutex_);
        completed.swap(completed_);
    }
    for (auto& value : completed)
    {
        value.completion(std::move(value.outcome));
    }
}

bool Store::IsIdle() const
{
    std::lock_guard lock(mutex_);
    return jobs_.empty() && completed_.empty() && !working_;
}

// Drops queued work and ends the running job's blocking file I/O, so a slow
// source cannot delay destruction. No completion runs afterwards.
void Store::Abandon()
{
    std::unique_lock lock(mutex_);
    jobs_.clear();
    // CancelSynchronousIo ends only the call in progress, and the job may
    // start another, so repeat it until the job returns.
    while (working_)
    {
        CancelSynchronousIo(worker_.native_handle());
        idle_.wait_for(lock, std::chrono::milliseconds(10));
    }
    completed_.clear();
}

std::string Store::Read(std::filesystem::path const& path)
{
    std::ifstream stream(path, std::ios::binary | std::ios::ate);
    if (!stream)
    {
        throw std::runtime_error("Cannot read " + Utf8(path.wstring()));
    }
    auto size = stream.tellg();
    if (size < 0 || size > fileLimit)
    {
        throw std::runtime_error("File exceeds limit");
    }
    std::string bytes(static_cast<std::size_t>(size), '\0');
    stream.seekg(0);
    if (!stream.read(bytes.data(), bytes.size()))
    {
        throw std::runtime_error("Incomplete read");
    }
    return bytes;
}
}
