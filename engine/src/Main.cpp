#include "Desktop/Application.h"
#include "Desktop/Instance.h"
#include "Registration.h"
#include "Strings.h"
#include <sddl.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <stdexcept>

#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

namespace
{
std::wstring LogonSid()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
    {
        throw std::runtime_error("Cannot read the current logon token.");
    }
    DWORD size = 0;
    GetTokenInformation(token, TokenGroups, nullptr, 0, &size);
    std::vector<char> storage(size);
    bool success = GetTokenInformation(token, TokenGroups, storage.data(), size, &size);
    CloseHandle(token);
    if (!success)
    {
        throw std::runtime_error("Cannot read the current logon groups.");
    }
    auto groups = reinterpret_cast<TOKEN_GROUPS*>(storage.data());
    for (DWORD index = 0; index < groups->GroupCount; ++index)
    {
        auto const& group = groups->Groups[index];
        if ((group.Attributes & SE_GROUP_LOGON_ID) != SE_GROUP_LOGON_ID)
        {
            continue;
        }
        LPWSTR text = nullptr;
        if (!ConvertSidToStringSidW(group.Sid, &text))
        {
            break;
        }
        std::wstring sid(text);
        LocalFree(text);
        return sid;
    }
    throw std::runtime_error("No logon SID is available.");
}

// A failed desktop restart reports the inaccessible store instead of looping.
constexpr wchar_t breakaway[] = L"TINYTORRENT_BREAKAWAY";

bool IsAppContainer()
{
    HANDLE handle = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &handle))
    {
        throw std::runtime_error("Cannot read the current process token.");
    }
    tt::OwnedHandle token(handle);
    DWORD container = 0;
    DWORD size = 0;
    if (!GetTokenInformation(token.get(), TokenIsAppContainer, &container, sizeof(container), &size))
    {
        throw std::runtime_error("Cannot check application sandbox isolation.");
    }
    return container != 0;
}

std::filesystem::path FinalPath(HANDLE handle)
{
    std::wstring path(32768, L'\0');
    auto length = GetFinalPathNameByHandleW(handle, path.data(), static_cast<DWORD>(path.size()), 0);
    if (!length || length >= path.size())
    {
        throw std::runtime_error("Cannot resolve the application data location.");
    }
    path.resize(length);
    return path;
}

// A launcher can redirect new AppData files into its private copy without
// giving its child a package identity or changing the known-folder path, so a
// disposable file shows where a write really lands. Windows keeps that copy
// inside the same AppData folder, while a data folder the person moved with a
// link resolves elsewhere and is not redirected.
bool IsRedirected(std::filesystem::path const& directory)
{
    std::filesystem::create_directories(directory);
    auto folder = CreateFileW(directory.parent_path().c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (folder == INVALID_HANDLE_VALUE)
    {
        throw std::runtime_error("Cannot open the application data folder.");
    }
    tt::OwnedHandle parent(folder);
    auto name = L"startup-" + std::to_wstring(GetCurrentProcessId()) + L".tmp";
    auto probe = CreateFileW((directory / name).c_str(), GENERIC_WRITE | DELETE, 0,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
    if (probe == INVALID_HANDLE_VALUE)
    {
        throw std::runtime_error("Cannot access the application data folder.");
    }
    tt::OwnedHandle file(probe);
    auto root = FinalPath(parent.get()).wstring();
    auto expected = (std::filesystem::path(root) / directory.filename() / name).wstring();
    auto actual = FinalPath(file.get()).wstring();
    auto inside = root + L"\\";
    return _wcsicmp(expected.c_str(), actual.c_str()) != 0 &&
        _wcsnicmp(actual.c_str(), inside.c_str(), inside.size()) == 0;
}

// Starts this engine again with Explorer as its parent, so it runs in the
// person's desktop context instead of the launcher's.
void Restart()
{
    DWORD shellId = 0;
    GetWindowThreadProcessId(GetShellWindow(), &shellId);
    tt::OwnedHandle shell(OpenProcess(PROCESS_CREATE_PROCESS, FALSE, shellId));
    if (!shell)
    {
        throw std::runtime_error("Cannot start TinyTorrent from the Windows desktop.");
    }
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    std::vector<char> storage(size);
    auto attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage.data());
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &size))
    {
        throw std::runtime_error("Cannot prepare the desktop start.");
    }
    HANDLE parent = shell.get();
    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.lpAttributeList = attributes;
    auto executable = tt::Executable();
    std::wstring command = GetCommandLineW();
    PROCESS_INFORMATION process{};
    bool started = UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, &parent,
            sizeof(parent), nullptr, nullptr) &&
        SetEnvironmentVariableW(breakaway, L"1") &&
        CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, EXTENDED_STARTUPINFO_PRESENT,
            nullptr, nullptr, &startup.StartupInfo, &process);
    auto error = GetLastError();
    DeleteProcThreadAttributeList(attributes);
    if (!started)
    {
        throw std::runtime_error("Cannot start from the Windows desktop: Windows error " + std::to_string(error));
    }
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
}

// Another version keeps this launch out until that engine closes, and so does
// another data folder for a headless launch, which cannot ask to exit it. Its
// folder identifies it, and Retry starts this launch again once it has closed.
tt::desktop::Refusal OtherEngine(tt::Forwarded const& forwarded)
{
    tt::desktop::Refusal refusal{
        .key = forwarded.outcome == tt::Forwarding::OtherVersion ? "version" : "other_store", .retry = true};
    // The detail line shortens a path in its middle, so the folder rather than
    // the program path keeps the folder that tells engines apart in view.
    if (forwarded.engine)
    {
        refusal.folder = forwarded.engine->parent_path();
        refusal.detail = refusal.folder->wstring();
    }
    return refusal;
}

// MSVC's system and file system errors describe themselves in the ANSI code
// page rather than UTF-8, so text that is not UTF-8 is read in that page.
std::wstring Describe(std::exception const& error)
{
    std::string text = error.what();
    try
    {
        return tt::Wide(text);
    }
    catch (std::runtime_error const&)
    {
        auto size = MultiByteToWideChar(CP_ACP, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
        std::wstring wide(size, L'\0');
        MultiByteToWideChar(CP_ACP, 0, text.data(), static_cast<int>(text.size()), wide.data(), size);
        return wide;
    }
}

// Writes why this launch cannot start to the error output for scripts, and
// shows it in the failure popup unless the launch is headless.
int Report(tt::desktop::Refusal refusal, bool headless)
{
    tt::Strings strings;
    auto message = strings.Text("error", refusal.key);
    if (!refusal.detail.empty())
    {
        message += L"\n" + refusal.detail;
    }
    auto output = GetStdHandle(STD_ERROR_HANDLE);
    if (output && output != INVALID_HANDLE_VALUE)
    {
        auto bytes = tt::Utf8(message) + "\n";
        DWORD written = 0;
        WriteFile(output, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
    }
    if (headless)
    {
        OutputDebugStringW(message.c_str());
    }
    else
    {
        tt::desktop::Application::ShowRefusal(strings, std::move(refusal));
    }
    return 1;
}
}

int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ PWSTR, _In_ int)
{
    // Prevent Windows from reporting application crashes to Microsoft.
    SetErrorMode(GetErrorMode() | SEM_NOGPFAULTERRORBOX);
    // The popup's accessibility objects reach other processes through COM,
    // which needs an apartment on this thread. A single-threaded one delivers
    // their calls through this thread's message loop, so they never run
    // beside the popup's own code.
    struct Apartment
    {
        HRESULT result = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        ~Apartment()
        {
            if (SUCCEEDED(result))
            {
                CoUninitialize();
            }
        }
    } apartment;
    tt::OwnedHandle mutex;
    bool headless = false;
    try
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (FAILED(SetCurrentProcessExplicitAppUserModelID(tt::appId)))
        {
            throw std::runtime_error("Cannot set the application's Windows identity.");
        }
        int count = 0;
        auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
        bool background = false;
#ifdef _DEBUG
        bool notice = false;
#endif
        bool exiting = false;
        bool literal = false;
        std::string registration;
        std::vector<std::string> sources;
        std::vector<std::wstring> repairClasses;
        std::filesystem::path directory;
        for (int index = 1; index < count; ++index)
        {
            std::wstring argument(arguments[index]);
            if (!literal && argument == tt::option::literal)
            {
                literal = true;
            }
            else if (!literal && argument == tt::option::headless)
            {
                headless = true;
            }
            else if (!literal && argument == tt::option::background)
            {
                background = true;
            }
            else if (!literal && argument == tt::option::exit)
            {
                exiting = true;
            }
            else if (!literal && argument == tt::option::registration)
            {
                if (++index == count)
                {
                    throw tt::desktop::Refusal{.key = "argument_registration"};
                }
                registration = tt::Utf8(arguments[index]);
                if (registration.empty())
                {
                    throw tt::desktop::Refusal{.key = "argument_registration"};
                }
            }
            else if (!literal && argument == tt::option::data)
            {
                if (++index == count || std::wstring(arguments[index]) == tt::option::literal)
                {
                    throw tt::desktop::Refusal{.key = "argument_folder"};
                }
                directory = arguments[index];
            }
#ifdef _DEBUG
            else if (!literal && argument == tt::option::notice)
            {
                notice = true;
                background = true;
            }
#endif
            else if (!literal && argument == tt::option::repairClass)
            {
                if (++index == count)
                {
                    throw tt::desktop::Refusal{.key = "argument_class"};
                }
                repairClasses.push_back(arguments[index]);
            }
            else
            {
                auto source = tt::Utf8(argument);
                tt::NormaliseMagnet(source);
                if (!argument.empty() && !tt::IsMagnet(source))
                {
                    source = tt::Utf8(std::filesystem::absolute(std::filesystem::path(argument)).wstring());
                }
                sources.push_back(std::move(source));
            }
        }
        LocalFree(arguments);
        if (!registration.empty() && !sources.empty())
        {
            throw tt::desktop::Refusal{.key = "argument_sources"};
        }
        if (exiting && (!registration.empty() || !sources.empty() || background || !directory.empty()))
        {
            throw tt::desktop::Refusal{.key = "argument_exit"};
        }
        if (!sources.empty() && !tt::desktop::Application::ValidSources(sources))
        {
            throw tt::desktop::Refusal{.key = "sources_limit"};
        }
        // The window starts this repair as an administrator, and forwarding
        // would hand it to the person's own engine, which cannot change
        // all-users entries. It changes nothing an engine owns, so it needs no
        // instance ownership.
        if (!repairClasses.empty())
        {
            if (!registration.empty() || !sources.empty() || exiting || background || !directory.empty())
            {
                throw tt::desktop::Refusal{.key = "argument_repair"};
            }
            tt::Registration().RepairMachine(repairClasses);
            return 0;
        }
        std::filesystem::path standard;
        PWSTR local = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_NO_PACKAGE_REDIRECTION, nullptr, &local)))
        {
            standard = std::filesystem::path(local) / tt::productName;
        }
        CoTaskMemFree(local);
        if (directory.empty())
        {
            if (standard.empty())
            {
                throw std::runtime_error("Cannot locate the local application data folder.");
            }
            directory = standard;
        }
        // An engine with its own store, such as a test's, is not the person's
        // copy, so only the default store moves the registrations to it. A
        // restart names the default store explicitly, so compare the folders.
        bool personal = tt::SameFolder(directory, standard);
        if (personal && !exiting)
        {
            if (IsAppContainer())
            {
                throw tt::desktop::Refusal{.key = "sandbox"};
            }
            if (IsRedirected(standard))
            {
                wchar_t buffer[2]{};
                if (GetEnvironmentVariableW(breakaway, buffer, 2))
                {
                    throw tt::desktop::Refusal{.key = "package"};
                }
                Restart();
                return 0;
            }
        }
        SetEnvironmentVariableW(breakaway, nullptr);
        auto sid = LogonSid();
        {
            tt::Security security(sid);
            mutex.reset(CreateMutexW(&security.Attributes(), FALSE, tt::desktop::EngineLockName(sid).c_str()));
        }
        if (!mutex)
        {
            throw std::runtime_error("Cannot claim engine instance ownership.");
        }
        auto acquired = WaitForSingleObject(mutex.get(), 0);
        while (acquired == WAIT_TIMEOUT)
        {
            tt::Forwarded forwarded{tt::Forwarding::Accepted};
            if (exiting)
            {
                forwarded = tt::Pipe::Forward(sid, {{"command", "exit"}});
            }
            else if (!registration.empty())
            {
                // An engine of another store, such as a test's, would register
                // its own program with Windows.
                forwarded = tt::Pipe::Forward(
                    sid, {{"command", "registration"}, {"action", registration}}, directory);
            }
            else if (!sources.empty())
            {
                forwarded = tt::Pipe::Forward(
                    sid, {{"command", "activate_sources"}, {"sources", sources}}, directory);
            }
#ifdef _DEBUG
            // The running engine shows the sample, as it takes another
            // launch's sources.
            else if (notice)
            {
                forwarded = tt::Pipe::Forward(sid, {{"command", "sample_notice"}});
            }
#endif
            else if (!background && !headless)
            {
                forwarded = tt::Pipe::Forward(sid, {{"command", "open"}}, directory);
            }
            if (forwarded.outcome == tt::Forwarding::OtherStore && !headless)
            {
                // Exit and Force exit need the identified process and program.
                if (!forwarded.process || !forwarded.engine)
                {
                    throw tt::desktop::Refusal{.key = "other_store_unidentified", .retry = true};
                }
                if (!tt::desktop::ExitOther(tt::Strings(), sid, forwarded))
                {
                    return 1;
                }
                acquired = WaitForSingleObject(mutex.get(), 0);
                continue;
            }
            if (forwarded.outcome == tt::Forwarding::OtherVersion || forwarded.outcome == tt::Forwarding::OtherStore)
            {
                throw OtherEngine(forwarded);
            }
            if (exiting)
            {
                acquired = WaitForSingleObject(mutex.get(), 30'000);
                if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
                {
                    throw tt::desktop::Refusal{.key = "engine_running", .retry = true};
                }
            }
            else
            {
                mutex.reset();
                if (forwarded.outcome == tt::Forwarding::Refused)
                {
                    throw tt::desktop::Refusal{.key = "activation_refused", .retry = true};
                }
                return 0;
            }
        }
        if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
        {
            throw std::runtime_error("Cannot acquire engine instance ownership.");
        }
        if (exiting)
        {
            if (tt::desktop::HasWindow(sid))
            {
                throw tt::desktop::Refusal{.key = "window_open", .retry = true};
            }
            ReleaseMutex(mutex.get());
            return 0;
        }
        int result;
        if (!registration.empty())
        {
            auto response = tt::Registration().Execute(registration);
            result = response.value("ok", false) ? 0 : 1;
            if (result)
            {
                throw tt::desktop::Refusal{.key = "registration_failed",
                    .detail = tt::Wide(response.at("error").at("detail").get<std::string>())};
            }
        }
        else
        {
            if (personal)
            {
                tt::Registration().Repair();
            }
            tt::desktop::Application application(directory, sid, headless);
#ifdef _DEBUG
            if (notice)
            {
                application.ShowSampleNotice();
            }
#endif
            result = application.Run(background, std::move(sources));
        }
        ReleaseMutex(mutex.get());
        return result;
    }
    catch (tt::desktop::Refusal& refusal)
    {
        // Another launch can start while this one waits for the person.
        mutex.reset();
        return Report(std::move(refusal), headless);
    }
    catch (std::exception const& error)
    {
        mutex.reset();
        return Report({.key = "start", .detail = Describe(error)}, headless);
    }
}
