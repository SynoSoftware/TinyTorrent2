#include "Store.h"
#include "Engine.h"
#include <Windows.h>
#include <fstream>
#include <stdexcept>

namespace tiny
{
Store::Store(std::function<void()> wake) : wake_(std::move(wake)), worker_([this]
{
    for (;;)
    {
        Job job;
        {
            std::unique_lock lock(mutex_);
            ready_.wait(lock, [this] { return stopping_ || !jobs_.empty(); });
            if (jobs_.empty()) return;
            job = std::move(jobs_.front());
            jobs_.pop_front();
            working_ = true;
        }
        StorageOutcome outcome;
        try { job.work(); outcome.saved = true; }
        catch (std::exception const& error) { outcome.detail = error.what(); }
        {
            std::lock_guard lock(mutex_);
            completed_.push_back({std::move(job.completion), std::move(outcome)});
            working_ = false;
        }
        if (wake_) wake_();
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
        rejected = jobs_.size() >= 64;
        if (rejected) completed_.push_back({std::move(completion), {false, "storage_overloaded"}});
        else jobs_.push_back({std::move(work), std::move(completion)});
    }
    if (rejected) { if (wake_) wake_(); }
    else ready_.notify_one();
}

void Store::Write(std::filesystem::path path, std::function<std::string()> encode,
    std::function<void(StorageOutcome)> completion)
{
    Run([path = std::move(path), encode = std::move(encode)]
    {
        auto bytes = encode();
        auto temporary = path;
        temporary += L".tmp";
        HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr,
            CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            throw std::runtime_error("CreateFile: " + std::to_string(GetLastError()));
        DWORD written = 0;
        bool success = WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()),
            &written, nullptr) && written == bytes.size();
        if (success) success = FlushFileBuffers(file);
        DWORD error = success ? 0 : GetLastError();
        CloseHandle(file);
        if (!success) throw std::runtime_error("WriteFile: " + std::to_string(error));
        if (!MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw std::runtime_error("MoveFileEx: " + std::to_string(GetLastError()));
    }, std::move(completion));
}

void Store::Drain()
{
    std::deque<Completed> completed;
    {
        std::lock_guard lock(mutex_);
        completed.swap(completed_);
    }
    for (auto& value : completed) value.callback(std::move(value.outcome));
}

bool Store::IsIdle() const
{
    std::lock_guard lock(mutex_);
    return jobs_.empty() && completed_.empty() && !working_;
}

std::string Store::Read(std::filesystem::path const& path)
{
    std::ifstream stream(path, std::ios::binary | std::ios::ate);
    if (!stream) throw std::runtime_error("Cannot read " + Utf8(path.wstring()));
    auto size = stream.tellg();
    if (size < 0 || size > 16 * 1024 * 1024) throw std::runtime_error("File exceeds limit");
    std::string bytes(static_cast<std::size_t>(size), '\0');
    stream.seekg(0);
    if (!stream.read(bytes.data(), bytes.size())) throw std::runtime_error("Incomplete read");
    return bytes;
}
}
