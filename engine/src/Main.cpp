#include "Desktop.h"
#include "Registration.h"
#include <sddl.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <stdexcept>

#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='*' publicKeyToken='6595b64144ccf1df' language='*'\"")

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    HANDLE mutex = nullptr;
    bool headless = false;
    try
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (FAILED(SetCurrentProcessExplicitAppUserModelID(L"Syno.TinyTorrent")))
            throw std::runtime_error("Cannot set the application's Windows identity.");
        int count = 0;
        auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
        bool background = false;
        bool literal = false;
        std::string registration;
        tiny::Json sources = tiny::Json::array();
        std::filesystem::path directory;
        for (int index = 1; index < count; ++index)
        {
            std::wstring argument(arguments[index]);
            if (!literal && argument == L"--") literal = true;
            else if (!literal && argument == L"--headless") headless = true;
            else if (!literal && argument == L"--background") background = true;
            else if (!literal && argument == L"--registration")
            {
                if (++index == count) throw std::runtime_error("The --registration option needs an operation.");
                registration = tiny::Utf8(arguments[index]);
                if (registration.empty()) throw std::runtime_error("The --registration option needs an operation.");
            }
            else if (!literal && argument == L"--data")
            {
                if (++index == count || std::wstring(arguments[index]) == L"--")
                    throw std::runtime_error("The --data option needs a folder.");
                directory = arguments[index];
            }
            else
            {
                if (argument.size() >= 7 && CompareStringOrdinal(argument.c_str(), 7,
                    L"magnet:", 7, TRUE) == CSTR_EQUAL)
                    argument.replace(0, 7, L"magnet:");
                if (!argument.empty() && !argument.starts_with(L"magnet:"))
                    argument = std::filesystem::absolute(std::filesystem::path(argument)).wstring();
                sources.push_back(tiny::Utf8(argument));
            }
        }
        LocalFree(arguments);
        if (!registration.empty() && !sources.empty())
            throw std::runtime_error("Registration operations cannot include torrent sources.");
        if (!sources.empty() && !tiny::Desktop::ValidSources(sources))
            throw std::runtime_error("Torrent sources exceed the supported count or length.");
        if (directory.empty())
        {
            PWSTR local = nullptr;
            if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local)))
                throw std::runtime_error("Cannot locate the local application data folder.");
            directory = std::filesystem::path(local) / L"TinyTorrent";
            CoTaskMemFree(local);
        }
        auto sid = tiny::LogonSid();
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        auto acl = L"D:P(A;;GA;;;" + sid + L")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(acl.c_str(), SDDL_REVISION_1,
            &descriptor, nullptr)) throw std::runtime_error("Cannot secure engine instance ownership.");
        SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE};
        mutex = CreateMutexW(&security, FALSE, (L"Local\\TinyTorrent.Engine." + sid).c_str());
        LocalFree(descriptor);
        if (!mutex) throw std::runtime_error("Cannot claim engine instance ownership.");
        auto acquired = WaitForSingleObject(mutex, 0);
        if (acquired == WAIT_TIMEOUT)
        {
            bool forwarded = !registration.empty() ? tiny::Pipe::Forward(L"\\\\.\\pipe\\TinyTorrent." + sid,
                {{"command", "registration"}, {"operation", registration}}) : sources.empty() ? background || headless ||
                tiny::Pipe::Forward(L"\\\\.\\pipe\\TinyTorrent." + sid) :
                tiny::Pipe::Forward(L"\\\\.\\pipe\\TinyTorrent." + sid,
                    {{"command", "activate_sources"}, {"sources", sources}});
            CloseHandle(mutex);
            mutex = nullptr;
            if (!forwarded)
                throw std::runtime_error("The running engine could not accept the activation request.");
            return 0;
        }
        if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
            throw std::runtime_error("Cannot acquire engine instance ownership.");
        int result;
        if (!registration.empty())
        {
            auto response = tiny::Registration().Execute(registration);
            result = response.value("ok", false) ? 0 : 1;
            if (result) throw std::runtime_error(response.at("error").at("detail").get<std::string>());
        }
        else {
            tiny::Desktop desktop(directory, sid, headless);
            result = desktop.Run(background, std::move(sources));
        }
        ReleaseMutex(mutex);
        CloseHandle(mutex);
        return result;
    }
    catch (std::exception const& error)
    {
        if (headless) OutputDebugStringA(error.what());
        else
        {
            auto message = tiny::Wide(tiny::LoadStrings()["error"]["start"].get<std::string>()) +
                L"\n\n" + tiny::Wide(error.what());
            MessageBoxW(nullptr, message.c_str(), L"TinyTorrent", MB_OK | MB_ICONERROR);
        }
        if (mutex) CloseHandle(mutex);
        return 1;
    }
}
