#include "Registration.h"
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <stdexcept>

namespace tiny
{
namespace
{
std::wstring Executable()
{
    wchar_t path[32768];
    auto size = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
    if (!size || size == std::size(path)) throw std::runtime_error("Cannot locate the engine executable.");
    return std::wstring(path, size);
}

std::wstring Read(std::wstring const& key, wchar_t const* name = nullptr)
{
    DWORD size = 0;
    if (RegGetValueW(HKEY_CURRENT_USER, key.c_str(), name, RRF_RT_REG_SZ, nullptr, nullptr, &size) != ERROR_SUCCESS)
        return {};
    std::wstring value(size / sizeof(wchar_t), L'\0');
    if (RegGetValueW(HKEY_CURRENT_USER, key.c_str(), name, RRF_RT_REG_SZ, nullptr, value.data(), &size) != ERROR_SUCCESS)
        return {};
    value.resize(size / sizeof(wchar_t));
    if (!value.empty() && value.back() == L'\0') value.pop_back();
    return value;
}

bool Has(std::wstring const& key, wchar_t const* name, DWORD expected)
{
    DWORD type = 0;
    DWORD size = 0;
    return RegGetValueW(HKEY_CURRENT_USER, key.c_str(), name, RRF_RT_ANY, &type, nullptr, &size) == ERROR_SUCCESS && type == expected;
}

void Check(LSTATUS status)
{
    if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND)
        throw std::runtime_error("Windows registration error " + std::to_string(status));
}

void Write(std::wstring const& key, wchar_t const* name, std::wstring const& value)
{
    HKEY handle = nullptr;
    Check(RegCreateKeyExW(HKEY_CURRENT_USER, key.c_str(), 0, nullptr, 0, KEY_SET_VALUE, nullptr, &handle, nullptr));
    auto status = RegSetValueExW(handle, name, 0, REG_SZ, reinterpret_cast<BYTE const*>(value.c_str()),
        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(handle);
    Check(status);
}

void Remove(std::wstring const& key, wchar_t const* name)
{
    HKEY handle = nullptr;
    auto status = RegOpenKeyExW(HKEY_CURRENT_USER, key.c_str(), 0, KEY_SET_VALUE, &handle);
    if (status == ERROR_FILE_NOT_FOUND) return;
    Check(status);
    status = RegDeleteValueW(handle, name);
    RegCloseKey(handle);
    Check(status);
}

Json Default(wchar_t const* extension, ASSOCIATIONTYPE type)
{
    auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    IApplicationAssociationRegistration* association = nullptr;
    auto result = CoCreateInstance(CLSID_ApplicationAssociationRegistration, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&association));
    LPWSTR choice = nullptr;
    if (SUCCEEDED(result))
        result = association->QueryCurrentDefault(extension, type, AL_EFFECTIVE, &choice);
    Json value = SUCCEEDED(result) && choice ? Json(Utf8(choice)) : Json();
    CoTaskMemFree(choice);
    if (association) association->Release();
    if (SUCCEEDED(initialized)) CoUninitialize();
    return value;
}

constexpr wchar_t capabilities[] = L"Software\\TinyTorrent\\Capabilities";
constexpr wchar_t run[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
}

Json Registration::Observe() const
{
    auto executable = Executable();
    auto command = L"\"" + executable + L"\" -- \"%1\"";
    auto icon = L"\"" + executable + L"\",0";
    bool handlers = Read(L"Software\\RegisteredApplications", L"TinyTorrent") == capabilities &&
        Read(capabilities, L"ApplicationName") == L"TinyTorrent" &&
        Read(capabilities, L"ApplicationIcon") == icon &&
        Has(L"Software\\Classes\\TinyTorrent.Magnet", L"URL Protocol", REG_SZ) &&
        Read(L"Software\\Classes\\TinyTorrent.Magnet", L"URL Protocol").empty() &&
        Has(L"Software\\Classes\\.torrent\\OpenWithProgids", L"TinyTorrent.Torrent", REG_NONE) &&
        Read(std::wstring(capabilities) + L"\\FileAssociations", L".torrent") == L"TinyTorrent.Torrent" &&
        Read(std::wstring(capabilities) + L"\\URLAssociations", L"magnet") == L"TinyTorrent.Magnet";
    for (auto const* kind : {L"Torrent", L"Magnet"})
    {
        auto key = L"Software\\Classes\\TinyTorrent." + std::wstring(kind);
        handlers = handlers && Read(key + L"\\shell\\open\\command") == command && Read(key + L"\\DefaultIcon") == icon;
    }
    auto torrent = Default(L".torrent", AT_FILEEXTENSION);
    auto magnet = Default(L"magnet", AT_URLPROTOCOL);
    auto startup = Read(run, L"TinyTorrent");
    return {{"handlers_registered", handlers}, {"startup_enabled", startup == L"\"" + executable + L"\" --background"},
        {"startup_target", Utf8(startup)}, {"torrent_default", torrent.is_null() ? Json() : Json(torrent == "TinyTorrent.Torrent")},
        {"magnet_default", magnet.is_null() ? Json() : Json(magnet == "TinyTorrent.Magnet")}};
}

void Registration::Handlers(bool enabled) const
{
    for (auto const* kind : {L"Torrent", L"Magnet"})
    {
        auto key = L"Software\\Classes\\TinyTorrent." + std::wstring(kind);
        if (!enabled) { Check(RegDeleteTreeW(HKEY_CURRENT_USER, key.c_str())); continue; }
        Write(key, nullptr, L"TinyTorrent");
        if (std::wstring_view(kind) == L"Magnet") Write(key, L"URL Protocol", L"");
        Write(key + L"\\DefaultIcon", nullptr, L"\"" + Executable() + L"\",0");
        Write(key + L"\\shell\\open\\command", nullptr, L"\"" + Executable() + L"\" -- \"%1\"");
    }
    auto associations = L"Software\\Classes\\.torrent\\OpenWithProgids";
    if (enabled)
    {
        HKEY handle = nullptr;
        Check(RegCreateKeyExW(HKEY_CURRENT_USER, associations, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &handle, nullptr));
        auto status = RegSetValueExW(handle, L"TinyTorrent.Torrent", 0, REG_NONE, nullptr, 0);
        RegCloseKey(handle);
        Check(status);
        Write(capabilities, L"ApplicationName", L"TinyTorrent");
        Write(capabilities, L"ApplicationIcon", L"\"" + Executable() + L"\",0");
        Write(capabilities, L"ApplicationDescription", L"TinyTorrent");
        Write(std::wstring(capabilities) + L"\\FileAssociations", L".torrent", L"TinyTorrent.Torrent");
        Write(std::wstring(capabilities) + L"\\URLAssociations", L"magnet", L"TinyTorrent.Magnet");
        Write(L"Software\\RegisteredApplications", L"TinyTorrent", capabilities);
    }
    else
    {
        Remove(associations, L"TinyTorrent.Torrent");
        Remove(L"Software\\RegisteredApplications", L"TinyTorrent");
        Check(RegDeleteTreeW(HKEY_CURRENT_USER, capabilities));
    }
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
}

void Registration::Startup(bool enabled) const
{
    if (enabled) Write(run, L"TinyTorrent", L"\"" + Executable() + L"\" --background");
    else Remove(run, L"TinyTorrent");
}

Json Registration::Execute(std::string const& operation) const
{
    try
    {
        if (operation == "register_handlers" || operation == "open_defaults") Handlers(true);
        else if (operation == "unregister_handlers") Handlers(false);
        else if (operation == "enable_startup") Startup(true);
        else if (operation == "disable_startup") Startup(false);
        else if (operation != "observe" && operation != "open_startup")
            return {{"ok", false}, {"error", {{"code", "invalid_request"}, {"detail", "Unknown registration operation."}}}};
        auto observed = Observe();
        if (operation == "open_startup" || (operation == "open_defaults" &&
            (observed["torrent_default"] != true || observed["magnet_default"] != true)))
        {
            auto uri = operation == "open_startup" ? L"ms-settings:startupapps" : L"ms-settings:defaultapps?registeredAppUser=TinyTorrent";
            if (reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", uri, nullptr, nullptr, SW_SHOWNORMAL)) <= 32)
                throw std::runtime_error("Windows Settings could not open.");
        }
        return {{"ok", true}, {"data", std::move(observed)}};
    }
    catch (std::exception const& error)
    {
        return {{"ok", false}, {"error", {{"code", "registration_failed"}, {"detail", error.what()}}}, {"data", Observe()}};
    }
}
}
