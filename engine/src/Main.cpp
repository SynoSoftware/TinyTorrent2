#include "Desktop.h"
#include <sddl.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <stdexcept>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    HANDLE mutex = nullptr;
    bool headless = false;
    try
    {
        if (FAILED(SetCurrentProcessExplicitAppUserModelID(L"Syno.TinyTorrent")))
            throw std::runtime_error("Cannot set the application's Windows identity.");
        int count = 0;
        auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
        bool background = false;
        std::filesystem::path directory;
        for (int index = 1; index < count; ++index)
        {
            std::wstring argument(arguments[index]);
            if (argument == L"--headless") headless = true;
            else if (argument == L"--background") background = true;
            else if (argument == L"--data" && index + 1 < count) directory = arguments[++index];
        }
        LocalFree(arguments);
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
            bool forwarded = background || headless || tiny::Pipe::Forward(L"\\\\.\\pipe\\TinyTorrent." + sid);
            CloseHandle(mutex);
            mutex = nullptr;
            if (!forwarded)
                throw std::runtime_error("The running engine could not accept the Open request.");
            return 0;
        }
        if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
            throw std::runtime_error("Cannot acquire engine instance ownership.");
        int result;
        {
            tiny::Desktop desktop(directory, sid, headless);
            result = desktop.Run(background);
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
