#include "Gozler.h"
#include "Engine.h"
#include "Owned.h"
#include <Windows.h>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <memory>
#include <mutex>

namespace tt::Gozler
{
namespace
{
// A call normally takes well under a second.
constexpr auto deadline = std::chrono::seconds(30);
// Exit code 7: the service runs but did not answer, as when its pipe is not
// open yet, so the call is tried again after this pause.
constexpr auto pause = std::chrono::seconds(5);
constexpr int tries = 3;
// Above what a search for five names and sizes can match.
constexpr int resultLimit = 1000;

struct Run
{
    DWORD exit = 0;
    std::string output;
};

struct CloseService
{
    void operator()(SC_HANDLE handle) const { CloseServiceHandle(handle); }
};
using OwnedService = std::unique_ptr<std::remove_pointer_t<SC_HANDLE>, CloseService>;

// One argument as the Microsoft C runtime reads it back from a command line.
std::wstring Quote(std::wstring const& argument)
{
    if (!argument.empty() && argument.find_first_of(L" \t\"") == std::wstring::npos)
        return argument;
    std::wstring quoted = L"\"";
    std::size_t slashes = 0;
    for (auto character : argument)
    {
        if (character == L'\\')
        {
            ++slashes;
            continue;
        }
        quoted.append(character == L'"' ? slashes * 2 + 1 : slashes, L'\\');
        slashes = 0;
        quoted.push_back(character);
    }
    quoted.append(slashes * 2, L'\\');
    quoted.push_back(L'"');
    return quoted;
}

// Runs the program directly, never through a shell, with only its output
// pipe and NUL inherited. Nothing when it could not start, passed the
// deadline, or `stop` was requested; it is ended then.
std::optional<Run> Execute(std::filesystem::path const& program, std::vector<std::wstring> const& arguments,
    std::stop_token stop)
{
    SECURITY_ATTRIBUTES inherited{sizeof(inherited), nullptr, TRUE};
    HANDLE reading = nullptr;
    HANDLE writing = nullptr;
    if (!CreatePipe(&reading, &writing, &inherited, 1 << 20))
        return std::nullopt;
    OwnedHandle reader(reading);
    OwnedHandle writer(writing);
    SetHandleInformation(reading, HANDLE_FLAG_INHERIT, 0);
    auto nul = CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &inherited,
        OPEN_EXISTING, 0, nullptr);
    if (nul == INVALID_HANDLE_VALUE)
        return std::nullopt;
    OwnedHandle sink(nul);
    // Only these two handles reach the child, whatever other threads open.
    HANDLE handles[] = {writing, nul};
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    std::vector<std::byte> list(size);
    auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(list.data());
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &size))
        return std::nullopt;
    std::unique_ptr<std::remove_pointer_t<LPPROC_THREAD_ATTRIBUTE_LIST>, decltype(&DeleteProcThreadAttributeList)>
        cleanup(attributes, DeleteProcThreadAttributeList);
    if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, sizeof(handles),
        nullptr, nullptr))
    {
        return std::nullopt;
    }
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = nul;
    startup.StartupInfo.hStdOutput = writing;
    startup.StartupInfo.hStdError = nul;
    startup.lpAttributeList = attributes;
    auto line = Quote(program.wstring());
    for (auto const& argument : arguments)
        line += L" " + Quote(argument);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(program.c_str(), line.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr, &startup.StartupInfo, &process))
    {
        return std::nullopt;
    }
    OwnedHandle child(process.hProcess);
    OwnedHandle thread(process.hThread);
    // The read ends once the child's copy of the pipe closes with it.
    writer.reset();
    sink.reset();
    Run run;
    auto drain = [&run, reading]
    {
        DWORD available = 0;
        while (PeekNamedPipe(reading, nullptr, 0, nullptr, &available, nullptr) && available > 0)
        {
            std::string chunk(available, '\0');
            DWORD read = 0;
            if (!ReadFile(reading, chunk.data(), available, &read, nullptr))
                return;
            run.output.append(chunk, 0, read);
        }
    };
    auto end = std::chrono::steady_clock::now() + deadline;
    while (WaitForSingleObject(process.hProcess, 50) != WAIT_OBJECT_0)
    {
        drain();
        if (stop.stop_requested() || std::chrono::steady_clock::now() >= end)
        {
            TerminateProcess(process.hProcess, 1);
            WaitForSingleObject(process.hProcess, 1000);
            return std::nullopt;
        }
    }
    drain();
    GetExitCodeProcess(process.hProcess, &run.exit);
    return run;
}

// gozler still builds the index of a volume, or scans it again; both end by
// themselves. A volume whose live updates stopped or whose index failed
// does not get ready by waiting.
bool Building(Json const& status)
{
    auto volumes = status.find("volumes");
    if (volumes == status.end() || !volumes->is_array())
        return false;
    for (auto const& volume : *volumes)
    {
        if (!volume.is_object())
            continue;
        auto phase = volume.find("phase");
        if (phase == volume.end())
            continue;
        auto const& named = phase->is_object() && phase->contains("phase") ? phase->at("phase") : *phase;
        if (!named.is_string())
            continue;
        auto text = named.get<std::string>();
        if (_stricmp(text.c_str(), "Bootstrapping") == 0 || _stricmp(text.c_str(), "Rescanning") == 0)
            return true;
    }
    return false;
}
}

std::optional<std::filesystem::path> Program()
{
    OwnedService manager(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    if (!manager)
        return std::nullopt;
    OwnedService service(OpenServiceW(manager.get(), L"gozler", SERVICE_QUERY_CONFIG));
    if (!service)
        return std::nullopt;
    DWORD needed = 0;
    QueryServiceConfigW(service.get(), nullptr, 0, &needed);
    std::vector<std::byte> buffer(needed);
    auto config = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(buffer.data());
    if (needed == 0 || !QueryServiceConfigW(service.get(), config, needed, &needed) || !config->lpBinaryPathName)
        return std::nullopt;
    // The image path may hold variables, quotes and arguments.
    std::wstring image(config->lpBinaryPathName);
    std::wstring expanded(ExpandEnvironmentStringsW(image.c_str(), nullptr, 0), L'\0');
    if (!expanded.empty() && ExpandEnvironmentStringsW(image.c_str(), expanded.data(), static_cast<DWORD>(expanded.size())))
        image = expanded.c_str();
    std::wstring daemon;
    if (image.starts_with(L"\""))
        daemon = image.substr(1, image.find(L'"', 1) - 1);
    else
    {
        auto lower = image;
        CharLowerBuffW(lower.data(), static_cast<DWORD>(lower.size()));
        auto end = lower.find(L".exe");
        daemon = end == std::wstring::npos ? image : image.substr(0, end + 4);
    }
    auto program = std::filesystem::path(daemon).parent_path() / L"gozler.exe";
    std::error_code error;
    if (!std::filesystem::is_regular_file(program, error))
        return std::nullopt;
    return program;
}

std::optional<Answer> Find(std::filesystem::path const& program, std::vector<Wanted> const& files,
    std::stop_token stop)
{
    Answer answer;
    if (files.empty())
        return answer;
    std::wstring query;
    for (auto const& file : files)
    {
        if (!query.empty())
            query += L"|";
        query += L"<wfn:\"" + file.name + L"\" size:" + std::to_wstring(file.size) + L">";
    }
    std::vector<std::wstring> arguments{L"--json", L"-n", std::to_wstring(resultLimit), L"-a-d", query,
        LR"(!"*\$Recycle.Bin\*")"};
    std::optional<Run> run;
    for (int attempt = 1; attempt <= tries; ++attempt)
    {
        run = Execute(program, arguments, stop);
        if (!run || run->exit != 7 || attempt == tries)
            break;
        std::mutex gate;
        std::condition_variable_any woken;
        std::unique_lock lock(gate);
        woken.wait_for(lock, stop, pause, [] { return false; });
        if (stop.stop_requested())
            return std::nullopt;
    }
    // 0: complete; 2: a volume is not ready, so matches may be missing; 10:
    // more rows than the limit. Any other code is a failure, 8 a stopped
    // service.
    if (!run || (run->exit != 0 && run->exit != 2 && run->exit != 10))
        return std::nullopt;
    auto parsed = Json::parse(run->output, nullptr, false);
    if (parsed.is_discarded() || !parsed.is_object())
        return std::nullopt;
    if (auto items = parsed.find("items"); items != parsed.end() && items->is_array())
    {
        for (auto const& item : *items)
        {
            if (!item.is_object() || item.value("is_dir", false) || !item.contains("path") || !item.at("path").is_string())
                continue;
            answer.found.emplace_back(Wide(item.at("path").get<std::string>()));
        }
    }
    if (run->exit == 2)
    {
        auto status = Execute(program, {L"--json", L"--status"}, stop);
        auto described = status ? Json::parse(status->output, nullptr, false) : Json();
        answer.building = described.is_object() && Building(described);
    }
    return answer;
}
}
