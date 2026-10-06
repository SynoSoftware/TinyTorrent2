#include "Registration.h"
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <stdexcept>

namespace tt
{
namespace
{
constexpr wchar_t registered[] = L"Software\\RegisteredApplications";
constexpr wchar_t capabilities[] = L"Software\\TinyTorrent\\Capabilities";
constexpr wchar_t fileAssociations[] = L"Software\\TinyTorrent\\Capabilities\\FileAssociations";
constexpr wchar_t urlAssociations[] = L"Software\\TinyTorrent\\Capabilities\\URLAssociations";
constexpr wchar_t openWith[] = L"Software\\Classes\\.torrent\\OpenWithProgids";
constexpr wchar_t run[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t torrentClass[] = L"TinyTorrent.Torrent";
constexpr wchar_t magnetClass[] = L"TinyTorrent.Magnet";
constexpr wchar_t const* classes[] = {torrentClass, magnetClass};

constexpr std::pair<std::string_view, RegistrationOperation> operations[] = {
    {"observe", RegistrationOperation::Observe},
    {"register_handlers", RegistrationOperation::RegisterHandlers},
    {"unregister_handlers", RegistrationOperation::UnregisterHandlers},
    {"enable_startup", RegistrationOperation::EnableStartup},
    {"disable_startup", RegistrationOperation::DisableStartup},
    {"open_defaults", RegistrationOperation::OpenDefaults},
    {"open_startup", RegistrationOperation::OpenStartup}};

std::wstring Key(wchar_t const* progId)
{
    return L"Software\\Classes\\" + std::wstring(progId);
}

std::wstring OpenCommand() { return L"\"" + Executable() + L"\" " + option::literal + L" \"%1\""; }
std::wstring Icon() { return L"\"" + Executable() + L"\",0"; }
std::wstring Launch() { return L"\"" + Executable() + L"\" " + option::background; }

std::wstring Read(std::wstring const& key, wchar_t const* name = nullptr)
{
    DWORD size = 0;
    if (RegGetValueW(HKEY_CURRENT_USER, key.c_str(), name, RRF_RT_REG_SZ,
        nullptr, nullptr, &size) != ERROR_SUCCESS)
    {
        return {};
    }
    std::wstring value(size / sizeof(wchar_t), L'\0');
    if (RegGetValueW(HKEY_CURRENT_USER, key.c_str(), name, RRF_RT_REG_SZ,
        nullptr, value.data(), &size) != ERROR_SUCCESS)
    {
        return {};
    }
    value.resize(size / sizeof(wchar_t));
    if (!value.empty() && value.back() == L'\0')
    {
        value.pop_back();
    }
    return value;
}

bool Has(std::wstring const& key, wchar_t const* name, DWORD expected)
{
    DWORD type = 0;
    DWORD size = 0;
    return RegGetValueW(HKEY_CURRENT_USER, key.c_str(), name, RRF_RT_ANY,
        &type, nullptr, &size) == ERROR_SUCCESS &&
        type == expected;
}

void Check(LSTATUS status)
{
    if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND)
    {
        throw std::runtime_error("Windows registration error " + std::to_string(status));
    }
}

void Write(std::wstring const& key, wchar_t const* name, std::wstring const& value)
{
    HKEY handle = nullptr;
    Check(RegCreateKeyExW(HKEY_CURRENT_USER, key.c_str(), 0, nullptr, 0, KEY_SET_VALUE,
        nullptr, &handle, nullptr));
    auto status = RegSetValueExW(handle, name, 0, REG_SZ, reinterpret_cast<BYTE const*>(value.c_str()),
        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(handle);
    Check(status);
}

void Remove(std::wstring const& key, wchar_t const* name)
{
    HKEY handle = nullptr;
    auto status = RegOpenKeyExW(HKEY_CURRENT_USER, key.c_str(), 0, KEY_SET_VALUE, &handle);
    if (status == ERROR_FILE_NOT_FOUND)
    {
        return;
    }
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
    {
        result = association->QueryCurrentDefault(extension, type, AL_EFFECTIVE, &choice);
    }
    Json value = SUCCEEDED(result) && choice ? Json(Utf8(choice)) : Json();
    CoTaskMemFree(choice);
    if (association)
    {
        association->Release();
    }
    if (SUCCEEDED(initialized))
    {
        CoUninitialize();
    }
    return value;
}

// True when the person's default for `extension` or protocol is `progId`;
// null when Windows does not say.
Json IsDefault(wchar_t const* extension, ASSOCIATIONTYPE type, wchar_t const* progId)
{
    auto choice = Default(extension, type);
    return choice.is_null() ? Json() : Json(choice == Utf8(progId));
}

// Every handler entry is present and names this executable.
bool HandlersMatch()
{
    bool handlers = Read(registered, productName) == capabilities &&
        Read(capabilities, L"ApplicationName") == productName &&
        Read(capabilities, L"ApplicationIcon") == Icon() &&
        Has(Key(magnetClass), L"URL Protocol", REG_SZ) && Read(Key(magnetClass), L"URL Protocol").empty() &&
        Has(openWith, torrentClass, REG_NONE) &&
        Read(fileAssociations, L".torrent") == torrentClass &&
        Read(urlAssociations, L"magnet") == magnetClass;
    for (auto const* progId : classes)
    {
        handlers = handlers && Read(Key(progId) + L"\\shell\\open\\command") == OpenCommand() &&
            Read(Key(progId) + L"\\DefaultIcon") == Icon();
    }
    return handlers;
}

// The open command of any TinyTorrent handler, whichever copy registered it.
std::wstring HandlerCommand()
{
    auto command = Read(Key(torrentClass) + L"\\shell\\open\\command");
    return command.empty() ? Read(Key(magnetClass) + L"\\shell\\open\\command") : command;
}

// The executable a command starts, which every TinyTorrent command quotes.
std::wstring Target(std::wstring const& command)
{
    auto end = command.find(L'"', 1);
    return command.starts_with(L'"') && end != std::wstring::npos ? command.substr(1, end - 1) : command;
}

// Another TinyTorrent copy's entry is still a registration, so it is "other",
// never "none".
std::string Owner(std::wstring const& command, bool current)
{
    return command.empty() ? "none" : current ? "this" : "other";
}
}

Json Registration::Observe() const
{
    auto handlers = HandlerCommand();
    auto startup = Read(run, productName);
    return {
        {"handlers", Owner(handlers, HandlersMatch())},
        {"handlers_target", Utf8(Target(handlers))},
        {"startup", Owner(startup, startup == Launch())},
        {"startup_target", Utf8(Target(startup))},
        {"torrent_default", IsDefault(L".torrent", AT_FILEEXTENSION, torrentClass)},
        {"magnet_default", IsDefault(L"magnet", AT_URLPROTOCOL, magnetClass)}
    };
}

// Moves TinyTorrent's existing entries to this executable, so they follow the
// copy the person runs; an entry the person removed stays removed. Observe
// reports the actual entries, including a partially completed repair.
void Registration::Repair() const
{
    try
    {
        if (!HandlerCommand().empty() && !HandlersMatch())
        {
            RegisterHandlers();
        }
        auto startup = Read(run, productName);
        if (!startup.empty() && startup != Launch())
        {
            Write(run, productName, Launch());
        }
    }
    catch (std::exception const&)
    {
    }
}

void Registration::RegisterHandlers() const
{
    for (auto const* progId : classes)
    {
        Write(Key(progId), nullptr, productName);
        Write(Key(progId) + L"\\DefaultIcon", nullptr, Icon());
        Write(Key(progId) + L"\\shell\\open\\command", nullptr, OpenCommand());
    }
    Write(Key(magnetClass), L"URL Protocol", L"");
    HKEY handle = nullptr;
    Check(RegCreateKeyExW(HKEY_CURRENT_USER, openWith, 0, nullptr, 0, KEY_SET_VALUE,
        nullptr, &handle, nullptr));
    auto status = RegSetValueExW(handle, torrentClass, 0, REG_NONE, nullptr, 0);
    RegCloseKey(handle);
    Check(status);
    Write(capabilities, L"ApplicationName", productName);
    Write(capabilities, L"ApplicationIcon", Icon());
    Write(capabilities, L"ApplicationDescription", productName);
    Write(fileAssociations, L".torrent", torrentClass);
    Write(urlAssociations, L"magnet", magnetClass);
    Write(registered, productName, capabilities);
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
}

void Registration::UnregisterHandlers() const
{
    for (auto const* progId : classes)
    {
        Check(RegDeleteTreeW(HKEY_CURRENT_USER, Key(progId).c_str()));
    }
    Remove(openWith, torrentClass);
    Remove(registered, productName);
    Check(RegDeleteTreeW(HKEY_CURRENT_USER, capabilities));
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
}

Json Registration::Execute(std::string const& name) const
{
    auto operation = Parse(operations, name);
    if (!operation)
    {
        return Failure(ErrorCode::InvalidRequest, "Unknown registration operation.");
    }
    try
    {
        switch (*operation)
        {
        case RegistrationOperation::RegisterHandlers:
        case RegistrationOperation::OpenDefaults:
            RegisterHandlers();
            break;
        case RegistrationOperation::UnregisterHandlers:
            UnregisterHandlers();
            break;
        case RegistrationOperation::EnableStartup:
            Write(run, productName, Launch());
            break;
        case RegistrationOperation::DisableStartup:
            Remove(run, productName);
            break;
        case RegistrationOperation::Observe:
        case RegistrationOperation::OpenStartup:
            break;
        }
        auto observed = Observe();
        // Windows lets only the person choose default apps, so open_defaults
        // opens Settings when TinyTorrent is not the default yet.
        bool isDefault = observed["torrent_default"] == true && observed["magnet_default"] == true;
        if (operation == RegistrationOperation::OpenStartup ||
            (operation == RegistrationOperation::OpenDefaults && !isDefault))
        {
            auto uri = operation == RegistrationOperation::OpenStartup
                ? std::wstring(L"ms-settings:startupapps")
                : L"ms-settings:defaultapps?registeredAppUser=" + std::wstring(productName);
            // ShellExecuteW returns a value above 32 when it succeeds.
            auto result = ShellExecuteW(nullptr, L"open", uri.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            if (reinterpret_cast<INT_PTR>(result) <= 32)
            {
                throw std::runtime_error("Windows Settings could not open.");
            }
        }
        return Success(std::move(observed));
    }
    catch (std::exception const& error)
    {
        auto reply = Failure(ErrorCode::RegistrationFailed, error.what());
        reply["data"] = Observe();
        return reply;
    }
}
}
