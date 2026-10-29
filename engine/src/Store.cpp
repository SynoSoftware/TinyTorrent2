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

// Runs `attempt`, which returns ERROR_SUCCESS or a Windows error, again while
// another program holds the file for a moment, as a virus scanner or an
// indexer that opens a new file does. It gives up after about a second and
// returns the last error.
template <typename Attempt>
DWORD Retried(Attempt attempt)
{
    auto error = attempt();
    for (int pause : {50, 100, 200, 300, 400})
    {
        if (error != ERROR_SHARING_VIOLATION && error != ERROR_ACCESS_DENIED)
            break;
        Sleep(pause);
        error = attempt();
    }
    return error;
}

// The file and Windows' own words for `error`, in the person's language,
// because the person reads the failure.
std::string WriteFailure(std::filesystem::path const& path, DWORD error)
{
    wchar_t reason[512]{};
    auto length = FormatMessageW(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS |
        FORMAT_MESSAGE_MAX_WIDTH_MASK, nullptr, error, 0, reason, static_cast<DWORD>(std::size(reason)), nullptr);
    std::wstring text(reason, length);
    while (!text.empty() && text.back() == L' ')
    {
        text.pop_back();
    }
    if (text.empty())
    {
        text = std::to_wstring(error);
    }
    return Utf8(path.wstring() + L": " + text);
}
}

Outcome StorageOutcome::ToOutcome(ErrorCode code) const
{
    if (!failure)
    {
        return {};
    }
    if (*failure == StorageFailure::Overloaded)
    {
        return {ErrorCode::Overloaded};
    }
    return {code, detail};
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
        }
        catch (boost::system::system_error const& error)
        {
            outcome = {StorageFailure::Failed, error.what(), error.code()};
        }
        catch (std::system_error const& error)
        {
            auto const& code = error.code();
            outcome = {StorageFailure::Failed, error.what(),
                {code.value(), code.category() == std::generic_category() ?
                    libtorrent::generic_category() : libtorrent::system_category()}};
        }
        catch (std::exception const& error)
        {
            outcome = {StorageFailure::Failed, error.what()};
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
    if (worker_.joinable())
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
            completed_.push_back({std::move(completion), {StorageFailure::Overloaded}});
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
    std::function<void(StorageOutcome)> completion, std::size_t limit)
{
    Run([path = std::move(path), encode = std::move(encode), limit]
    {
        auto bytes = encode();
        // A refused write keeps the previous file, which startup can still read.
        if (bytes.size() > limit)
        {
            throw std::runtime_error("File exceeds limit");
        }
        auto temporary = path;
        temporary += L".tmp";
        HANDLE file = INVALID_HANDLE_VALUE;
        if (auto error = Retried([&]
        {
            file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL,
                nullptr);
            return file == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
        }); error != ERROR_SUCCESS)
        {
            throw std::runtime_error(WriteFailure(path, error));
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
        if (success)
        {
            error = Retried([&]
            {
                return MoveFileExW(temporary.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING |
                    MOVEFILE_WRITE_THROUGH) ? ERROR_SUCCESS : GetLastError();
            });
            success = error == ERROR_SUCCESS;
        }
        if (!success)
        {
            // A temporary file left behind is replaced by the next write.
            DeleteFileW(temporary.c_str());
            throw std::runtime_error(WriteFailure(path, error));
        }
    }, std::move(completion));
}

void Store::Drain(std::function<void(std::exception const&)> const& failed)
{
    std::deque<Completed> completed;
    {
        std::lock_guard lock(mutex_);
        completed.swap(completed_);
    }
    for (auto& value : completed)
    {
        try
        {
            value.completion(std::move(value.outcome));
        }
        catch (std::exception const& error)
        {
            failed(error);
        }
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

void Store::Interrupt()
{
    std::lock_guard lock(mutex_);
    if (working_)
        CancelSynchronousIo(worker_.native_handle());
}

void Store::Detach()
{
    std::lock_guard lock(mutex_);
    jobs_.clear();
    completed_.clear();
    if (worker_.joinable())
        worker_.detach();
}

std::string Store::Read(std::filesystem::path const& path, std::size_t limit)
{
    std::ifstream stream(path, std::ios::binary | std::ios::ate);
    if (!stream)
    {
        throw std::runtime_error("Cannot read " + Utf8(path.wstring()));
    }
    auto size = stream.tellg();
    if (size < 0 || static_cast<std::uintmax_t>(size) > limit)
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
