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

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
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
        bool literal = false;
        std::string registration;
        std::vector<std::string> sources;
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
            else if (!literal && argument == tt::option::registration)
            {
                if (++index == count)
                {
                    throw std::runtime_error("The --registration option needs an operation.");
                }
                registration = tt::Utf8(arguments[index]);
                if (registration.empty())
                {
                    throw std::runtime_error("The --registration option needs an operation.");
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
            throw std::runtime_error("Registration operations cannot include torrent sources.");
        }
        if (!sources.empty() && !tt::desktop::Application::ValidSources(sources))
        {
            throw std::runtime_error("Torrent sources exceed the supported count or length.");
        }
        if (directory.empty())
        {
            PWSTR local = nullptr;
            if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)))
            {
                throw std::runtime_error("Cannot locate the local application data folder.");
            }
            directory = std::filesystem::path(local) / tt::productName;
            CoTaskMemFree(local);
        }
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
            bool forwarded = true;
            if (!registration.empty())
            {
                forwarded =
                    tt::Pipe::Forward(sid, {{"command", "registration"}, {"operation", registration}});
            }
            else if (!sources.empty())
            {
                forwarded = tt::Pipe::Forward(sid, {{"command", "activate_sources"}, {"sources", sources}});
            }
            else if (!background && !headless)
            {
                forwarded = tt::Pipe::Forward(sid);
            }
            mutex.reset();
            if (!forwarded)
            {
                throw std::runtime_error("The running engine could not accept the activation request.");
            }
            return 0;
        }
        if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
        {
            throw std::runtime_error("Cannot acquire engine instance ownership.");
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
