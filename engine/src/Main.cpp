#include "Desktop/Application.h"
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
}

int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ PWSTR, _In_ int)
{
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
                    throw std::runtime_error("The --registration option needs an action.");
                }
                registration = tt::Utf8(arguments[index]);
                if (registration.empty())
                {
                    throw std::runtime_error("The --registration option needs an action.");
                }
            }
            else if (!literal && argument == tt::option::data)
            {
                if (++index == count || std::wstring(arguments[index]) == tt::option::literal)
                {
                    throw std::runtime_error("The --data option needs a folder.");
                }
                directory = arguments[index];
            }
            else if (!literal && argument == tt::option::repairClass)
            {
                if (++index == count)
                {
                    throw std::runtime_error("The --repair-class option needs a class.");
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
            throw std::runtime_error("Registration actions cannot include torrent sources.");
        }
        if (exiting && (!registration.empty() || !sources.empty() || background || !directory.empty()))
        {
            throw std::runtime_error("The --exit option cannot include other operations or torrent sources.");
        }
        if (!sources.empty() && !tt::desktop::Application::ValidSources(sources))
        {
            throw std::runtime_error("Torrent sources exceed the supported count or length.");
        }
        // The window starts this repair as an administrator, and forwarding
        // would hand it to the person's own engine, which cannot change
        // all-users entries. It changes nothing an engine owns, so it needs no
        // instance ownership.
        if (!repairClasses.empty())
        {
            if (!registration.empty() || !sources.empty() || exiting || background || !directory.empty())
            {
                throw std::runtime_error("The --repair-class option cannot include other operations.");
            }
            tt::Registration().RepairMachine(repairClasses);
            return 0;
        }
        std::filesystem::path standard;
        PWSTR local = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)))
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
        std::error_code error;
        bool personal = directory == standard || std::filesystem::equivalent(directory, standard, error);
        auto sid = LogonSid();
        {
            tt::Security security(sid);
            mutex.reset(CreateMutexW(&security.Attributes(), FALSE, (L"Local\\TinyTorrent.Engine." + sid).c_str()));
        }
        if (!mutex)
        {
            throw std::runtime_error("Cannot claim engine instance ownership.");
        }
        auto acquired = WaitForSingleObject(mutex.get(), 0);
        if (acquired == WAIT_TIMEOUT)
        {
            auto forwarding = tt::Forwarding::Accepted;
            if (exiting)
            {
                forwarding = tt::Pipe::Forward(sid, {{"command", "exit"}});
            }
            else if (!registration.empty())
            {
                forwarding =
                    tt::Pipe::Forward(sid, {{"command", "registration"}, {"action", registration}});
            }
            else if (!sources.empty())
            {
                forwarding = tt::Pipe::Forward(sid, {{"command", "activate_sources"}, {"sources", sources}});
            }
            else if (!background && !headless)
            {
                forwarding = tt::Pipe::Forward(sid);
            }
            if (forwarding == tt::Forwarding::OtherVersion)
            {
                throw std::runtime_error(tt::Utf8(tt::Strings().Text("error", "version")));
            }
            if (exiting)
            {
                acquired = WaitForSingleObject(mutex.get(), 30'000);
                if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
                {
                    throw std::runtime_error("TinyTorrent is still running. Finish any open prompt or operation, then retry.");
                }
            }
            else
            {
                mutex.reset();
                if (forwarding == tt::Forwarding::Refused)
                {
                    throw std::runtime_error("The running engine could not accept the activation request.");
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
            // The window can survive an engine crash and still hold application files.
            tt::OwnedHandle window(OpenMutexW(SYNCHRONIZE | MUTEX_MODIFY_STATE, FALSE,
                (L"Local\\TinyTorrent.Window." + sid).c_str()));
            if (!window && GetLastError() != ERROR_FILE_NOT_FOUND)
            {
                throw std::runtime_error("Cannot check window instance ownership.");
            }
            if (window)
            {
                auto closed = WaitForSingleObject(window.get(), 0);
                if (closed != WAIT_OBJECT_0 && closed != WAIT_ABANDONED)
                {
                    throw std::runtime_error(tt::Utf8(tt::Strings().Text("error", "window_open")));
                }
                ReleaseMutex(window.get());
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
                throw std::runtime_error(response.at("error").at("detail").get<std::string>());
            }
        }
        else
        {
            if (personal)
            {
                tt::Registration().Repair();
            }
            tt::desktop::Application application(directory, sid, headless);
            result = application.Run(background, std::move(sources));
        }
        ReleaseMutex(mutex.get());
        return result;
    }
    catch (std::exception const& error)
    {
        if (headless)
        {
            OutputDebugStringA(error.what());
        }
        else
        {
            auto message = tt::Strings().Text("error", "start") + L"\n\n" + tt::Wide(error.what());
            MessageBoxW(nullptr, message.c_str(), tt::productName, MB_OK | MB_ICONERROR);
        }
        return 1;
    }
}
