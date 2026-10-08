#include "Registration.h"
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <algorithm>
#include <chrono>
#include <cwctype>
#include <filesystem>
#include <future>
#include <memory>
#include <stdexcept>
#include <thread>
#include <vector>

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
HKEY const roots[] = {HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE};
// A local identity check takes milliseconds; one that takes longer is waiting
// for a network share.
constexpr auto identityWait = std::chrono::milliseconds(250);

// What Windows opens with a handler: the extension or protocol, and the
// Capabilities subkey that names a registered application's class for it.
struct Kind
{
    wchar_t const* name;
    ASSOCIATIONTYPE type;
    wchar_t const* associations;
};
constexpr Kind kinds[] = {
    {L".torrent", AT_FILEEXTENSION, L"FileAssociations"},
    {L"magnet", AT_URLPROTOCOL, L"URLAssociations"}};

constexpr std::pair<std::string_view, RegistrationAction> actions[] = {
    {"observe", RegistrationAction::Observe},
    {"register_handlers", RegistrationAction::RegisterHandlers},
    {"unregister_handlers", RegistrationAction::UnregisterHandlers},
    {"enable_startup", RegistrationAction::EnableStartup},
    {"disable_startup", RegistrationAction::DisableStartup},
    {"open_defaults", RegistrationAction::OpenDefaults},
    {"open_startup", RegistrationAction::OpenStartup}};

bool Same(std::wstring const& first, std::wstring const& second)
{
    return CompareStringOrdinal(first.c_str(), -1, second.c_str(), -1, TRUE) == CSTR_EQUAL;
}

std::wstring Key(std::wstring const& progId)
{
    return L"Software\\Classes\\" + progId;
}

std::wstring OpenCommand(std::wstring const& path = Executable()) { return L"\"" + path + L"\" " + option::literal + L" \"%1\""; }
std::wstring Icon(std::wstring const& path = Executable()) { return L"\"" + path + L"\",0"; }
std::wstring Launch(std::wstring const& path = Executable()) { return L"\"" + path + L"\" " + option::background; }

std::wstring Read(std::wstring const& key, wchar_t const* name = nullptr, HKEY root = HKEY_CURRENT_USER)
{
    DWORD size = 0;
    if (RegGetValueW(root, key.c_str(), name, RRF_RT_REG_SZ,
        nullptr, nullptr, &size) != ERROR_SUCCESS)
    {
        return {};
    }
    std::wstring value(size / sizeof(wchar_t), L'\0');
    if (RegGetValueW(root, key.c_str(), name, RRF_RT_REG_SZ,
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

void Remove(std::wstring const& key, wchar_t const* name, HKEY root = HKEY_CURRENT_USER)
{
    HKEY handle = nullptr;
    auto status = RegOpenKeyExW(root, key.c_str(), 0, KEY_SET_VALUE, &handle);
    if (status == ERROR_FILE_NOT_FOUND)
    {
        return;
    }
    Check(status);
    status = RegDeleteValueW(handle, name);
    RegCloseKey(handle);
    Check(status);
}

// Each value of a key as its name and its text, which is empty for a value
// that holds no text.
std::vector<std::pair<std::wstring, std::wstring>> Values(HKEY root, std::wstring const& key)
{
    std::vector<std::pair<std::wstring, std::wstring>> values;
    HKEY handle = nullptr;
    if (RegOpenKeyExW(root, key.c_str(), 0, KEY_QUERY_VALUE, &handle) != ERROR_SUCCESS)
    {
        return values;
    }
    // A value name has at most 16,383 characters.
    std::wstring name(16'384, L'\0');
    for (DWORD index = 0;; ++index)
    {
        auto length = static_cast<DWORD>(name.size());
        auto status = RegEnumValueW(handle, index, name.data(), &length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS)
        {
            break;
        }
        if (status == ERROR_SUCCESS)
        {
            std::wstring found(name.data(), length);
            values.emplace_back(found, Read(key, found.c_str(), root));
        }
    }
    RegCloseKey(handle);
    return values;
}

// The class of the person's current default app for `extension` or protocol;
// empty when Windows does not say.
std::wstring Default(wchar_t const* extension, ASSOCIATIONTYPE type)
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
    std::wstring value = SUCCEEDED(result) && choice ? choice : L"";
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

// Another application's path can hold text that is not valid UTF-16, which
// Utf8 refuses; the replacement character shows it instead.
std::string Readable(std::wstring const& text)
{
    auto length = static_cast<int>(text.size());
    std::string value(WideCharToMultiByte(CP_UTF8, 0, text.data(), length, nullptr, 0, nullptr, nullptr), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), length, value.data(), static_cast<int>(value.size()), nullptr, nullptr);
    return value;
}

bool IsExe(std::wstring const& path)
{
    return path.size() >= 4 && Same(path.substr(path.size() - 4), L".exe");
}

// The runs of leading words of an unquoted command that end in ".exe",
// shortest first. Windows tries them in this order, so each may be the
// program.
std::vector<std::wstring> Programs(std::wstring const& command)
{
    std::vector<std::wstring> found;
    for (auto space = command.find(L' ');; space = command.find(L' ', space + 1))
    {
        auto words = command.substr(0, space);
        if (IsExe(words))
        {
            found.push_back(words);
        }
        if (space == std::wstring::npos)
        {
            return found;
        }
    }
}

// The executable a command starts; empty when a quote is not closed. Every
// TinyTorrent command quotes it; another application's command may leave a
// path with spaces unquoted, so that path ends at the first ".exe" that ends
// a word.
std::wstring Target(std::wstring const& command)
{
    if (command.starts_with(L'"'))
    {
        auto end = command.find(L'"', 1);
        return end == std::wstring::npos ? std::wstring() : command.substr(1, end - 1);
    }
    auto programs = Programs(command);
    return programs.empty() ? command.substr(0, command.find(L' ')) : programs.front();
}

// Which program the command starts; this copy also through another path to
// it, such as a junction. Missing means Windows reports that the file or its
// folder does not exist, because a missing program's entries are removed: a
// program that cannot be checked counts as another one that exists, so its
// entries are left alone. Opening a file whose path leads to an offline share
// waits for the network and would stall the engine, so a network path is
// compared only as text, and the check, which can still meet a link to a
// share, gives up after identityWait.
Copy Identify(std::wstring const& command)
{
    auto target = Target(command);
    auto executable = Executable();
    if (Same(target, executable))
    {
        return Copy::This;
    }
    // Only a quoted path, or an unquoted command with one run of words ending
    // in ".exe", certainly names the program: in C:\Program Files\app.cmd the
    // first word is not it. Only a full path on a drive names one file:
    // Windows searches for any other name and expands variables when it starts
    // the command, while this check would read it from the engine's own
    // folder. A longer path than MAX_PATH can fail as not found.
    bool exact = command.starts_with(L'"') || Programs(command).size() == 1;
    bool full = target.size() >= 3 && iswalpha(target[0]) && target[1] == L':' && target[2] == L'\\' &&
        target.find(L'%') == std::wstring::npos && target.size() < MAX_PATH;
    if (!exact || !full)
    {
        return Copy::Other;
    }
    // Only a fixed drive keeps its letter for the same volume: a removable
    // drive's letter can belong to another stick now, and a network or absent
    // drive, such as a mapped drive the elevated repair does not see, says
    // nothing about the program.
    if (GetDriveTypeW(target.substr(0, 3).c_str()) != DRIVE_FIXED)
    {
        return Copy::Other;
    }
    // Only the owner thread enters here; a timed-out lookup must finish
    // before another starts, without making shutdown wait for blocked I/O.
    static std::future<Copy> pending;
    if (pending.valid() && pending.wait_for(std::chrono::milliseconds(0)) != std::future_status::ready)
    {
        return Copy::Other;
    }
    auto found = std::make_shared<std::promise<Copy>>();
    pending = found->get_future();
    try
    {
        std::thread([found, target, executable]
        {
            std::error_code error;
            if (std::filesystem::equivalent(target, executable, error))
            {
                found->set_value(Copy::This);
                return;
            }
            bool absent = GetFileAttributesW(target.c_str()) == INVALID_FILE_ATTRIBUTES;
            auto cause = GetLastError();
            found->set_value(absent && (cause == ERROR_FILE_NOT_FOUND || cause == ERROR_PATH_NOT_FOUND)
                ? Copy::Missing
                : Copy::Other);
        }).detach();
    }
    catch (std::system_error const&)
    {
        return Copy::Other;
    }
    return pending.wait_for(identityWait) == std::future_status::ready ? pending.get() : Copy::Other;
}

// Whether the default class `choice` opens with TinyTorrent: its own class
// `ours`, or another class whose command starts this copy. Without the
// person's choice, Windows names the .torrent or magnet key itself, and the
// product before this one left its command there, which a start repoints.
bool Opens(std::wstring const& choice, wchar_t const* ours)
{
    return Same(choice, ours) ||
        Identify(Read(choice + L"\\shell\\open\\command", nullptr, HKEY_CLASSES_ROOT)) == Copy::This;
}

// True when the person's default for `extension` or protocol opens with
// TinyTorrent; null when Windows does not say.
Json IsDefault(wchar_t const* extension, ASSOCIATIONTYPE type, wchar_t const* progId)
{
    auto choice = Default(extension, type);
    return choice.empty() ? Json() : Json(Opens(choice, progId));
}

// Class names come from other applications' data and from the command line;
// one with an empty part would name a key above its class, such as
// Software\Classes itself.
bool IsClass(std::wstring const& progId)
{
    return !progId.empty() && !progId.starts_with(L'\\') && !progId.ends_with(L'\\') &&
        progId.find(L"\\\\") == std::wstring::npos;
}

bool Exists(std::wstring const& key, HKEY root = HKEY_CURRENT_USER)
{
    HKEY handle = nullptr;
    if (RegOpenKeyExW(root, key.c_str(), 0, KEY_QUERY_VALUE, &handle) != ERROR_SUCCESS)
    {
        return false;
    }
    RegCloseKey(handle);
    return true;
}

// A TinyTorrent handler's open command, preferring one that starts another
// copy, so a mixed registration reports that copy.
std::wstring HandlerCommand()
{
    std::wstring found;
    for (auto const* progId : classes)
    {
        auto command = Read(Key(progId) + L"\\shell\\open\\command");
        if (!command.empty() && (found.empty() || Identify(command) != Copy::This))
        {
            found = command;
        }
    }
    return found;
}

// Any handler entry is present, apart from the RegisteredApplications value
// that records the request.
bool HandlersPresent()
{
    for (auto const* progId : classes)
    {
        if (Exists(Key(progId)))
        {
            return true;
        }
    }
    return Exists(capabilities) || Has(openWith, torrentClass, REG_NONE);
}

// Every handler entry is present and starts this copy, through the one path
// the entries share.
bool HandlersComplete()
{
    auto command = HandlerCommand();
    if (command.empty() || Identify(command) != Copy::This)
    {
        return false;
    }
    auto path = Target(command);
    bool handlers = Read(registered, productName) == capabilities &&
        Read(capabilities, L"ApplicationName") == productName &&
        Read(capabilities, L"ApplicationIcon") == Icon(path) &&
        Has(Key(magnetClass), L"URL Protocol", REG_SZ) && Read(Key(magnetClass), L"URL Protocol").empty() &&
        Has(openWith, torrentClass, REG_NONE) &&
        Read(fileAssociations, L".torrent") == torrentClass &&
        Read(urlAssociations, L"magnet") == magnetClass;
    for (auto const* progId : classes)
    {
        handlers = handlers && Read(Key(progId) + L"\\shell\\open\\command") == OpenCommand(path) &&
            Read(Key(progId) + L"\\DefaultIcon") == Icon(path);
    }
    return handlers;
}

// Every class Windows may open `kind` with, whichever application wrote it:
// the current default, the extension's or protocol's own key and its default
// class, the Open with lists, and each registered application's class.
std::vector<std::wstring> References(Kind const& kind)
{
    std::vector<std::wstring> found{kind.name, Default(kind.name, kind.type)};
    for (auto root : roots)
    {
        found.push_back(Read(Key(kind.name), nullptr, root));
        for (auto const& [name, text] : Values(root, Key(kind.name) + L"\\OpenWithProgids"))
        {
            found.push_back(name);
        }
        for (auto const& [name, path] : Values(root, registered))
        {
            found.push_back(Read(path + L"\\" + kind.associations, kind.name, root));
        }
    }
    auto explorer = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\" + std::wstring(kind.name);
    for (auto const& [name, text] : Values(HKEY_CURRENT_USER, explorer + L"\\OpenWithProgids"))
    {
        found.push_back(name);
    }
    for (auto const& [name, program] : Values(HKEY_CURRENT_USER, explorer + L"\\OpenWithList"))
    {
        if (name != L"MRUList" && !program.empty())
        {
            found.push_back(L"Applications\\" + program);
        }
    }
    std::erase_if(found, [](std::wstring const& progId) { return !IsClass(progId); });
    std::vector<std::wstring> distinct;
    for (auto& progId : found)
    {
        if (std::ranges::none_of(distinct, [&](auto const& kept) { return Same(kept, progId); }))
        {
            distinct.push_back(std::move(progId));
        }
    }
    return distinct;
}

// A handler class in one registry root whose open command starts a missing
// program.
struct Broken
{
    Kind const* kind;
    HKEY root;
    std::wstring progId;
    std::wstring program;
};

std::vector<Broken> FindBroken()
{
    std::vector<Broken> found;
    for (auto const& kind : kinds)
    {
        for (auto const& progId : References(kind))
        {
            auto own = Read(Key(progId) + L"\\shell\\open\\command");
            for (auto root : roots)
            {
                // Windows reads a class's command from the current user
                // before all users, so a per-user command hides the other.
                auto command = root == HKEY_CURRENT_USER ? own
                    : own.empty() ? Read(Key(progId) + L"\\shell\\open\\command", nullptr, root) : std::wstring();
                if (!command.empty() && Identify(command) == Copy::Missing)
                {
                    found.push_back({&kind, root, progId, Target(command)});
                }
            }
        }
    }
    return found;
}

// Removes the open command of a class in `root` that starts the missing
// `program`. Nothing else of the class goes: a class can serve other
// extensions or hold shell extensions that still work. Only an Applications
// key, which describes that one missing program, goes whole.
void RemoveCommand(HKEY root, std::wstring const& progId, std::wstring const& program)
{
    auto name = std::filesystem::path(program).filename().wstring();
    bool application = Same(progId, L"Applications\\" + name);
    Check(RegDeleteTreeW(root, (Key(progId) + (application ? L"" : L"\\shell\\open")).c_str()));
}

// Removes a broken per-user class's open command and the references that
// offer the class for this kind, so the next open asks which app to use
// instead of failing. A registered application keeps its other associations.
void Remove(Broken const& broken)
{
    auto const& kind = *broken.kind;
    RemoveCommand(HKEY_CURRENT_USER, broken.progId, broken.program);
    // With the current user's command gone, an all-users command of the same
    // class takes over, and while it works, the references still lead to a
    // working program.
    auto remaining = Read(broken.progId + L"\\shell\\open\\command", nullptr, HKEY_CLASSES_ROOT);
    if (Same(broken.progId, kind.name) || (!remaining.empty() && Identify(remaining) != Copy::Missing))
    {
        return;
    }
    Remove(Key(kind.name) + L"\\OpenWithProgids", broken.progId.c_str());
    Remove(L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\" + std::wstring(kind.name) +
        L"\\OpenWithProgids", broken.progId.c_str());
    if (Same(Read(Key(kind.name)), broken.progId))
    {
        Remove(Key(kind.name), nullptr);
    }
    for (auto const& [name, path] : Values(HKEY_CURRENT_USER, registered))
    {
        auto associations = path + L"\\" + kind.associations;
        if (Same(Read(associations, kind.name), broken.progId))
        {
            Remove(associations, kind.name);
        }
    }
}

// Whether Windows opens `kind` with a working app other than TinyTorrent,
// which only the person can change. A default whose open command is gone or
// starts a missing program is not one: Windows asks at the next open instead.
// A command it cannot check, such as a packaged app's, counts as working.
bool OtherDefault(Kind const& kind, wchar_t const* ours)
{
    auto progId = Default(kind.name, kind.type);
    if (progId.empty() || Opens(progId, ours) || !Exists(progId + L"\\shell\\open", HKEY_CLASSES_ROOT))
    {
        return false;
    }
    auto command = Read(progId + L"\\shell\\open\\command", nullptr, HKEY_CLASSES_ROOT);
    return command.empty() || Identify(command) != Copy::Missing;
}

// Removes the broken per-user classes. One that Windows refuses to change
// stays reported and does not stop the others.
void RemoveBroken()
{
    bool removed = false;
    for (auto const& broken : FindBroken())
    {
        if (broken.root != HKEY_CURRENT_USER)
        {
            continue;
        }
        try
        {
            Remove(broken);
            removed = true;
        }
        catch (std::exception const&)
        {
        }
    }
    if (removed)
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    }
}

// Only the product before this one wrote these per-user keys: an open command
// on the .torrent or magnet key itself that starts a TinyTorrent executable,
// and a .torrent default naming TinyTorrent's class. Where no choice of the
// person's overrides them, they decide what opens torrents, so while the
// handlers are registered they start this copy, unless they start another
// existing copy in this product's form, and otherwise they go.
void RepairLegacy(bool requested)
{
    auto name = std::filesystem::path(Executable()).filename().wstring();
    bool changed = false;
    for (auto const& kind : kinds)
    {
        auto open = Key(kind.name) + L"\\shell\\open";
        auto command = Read(open + L"\\command");
        if (command.empty() || !Same(std::filesystem::path(Target(command)).filename().wstring(), name))
        {
            continue;
        }
        bool current = command == OpenCommand(Target(command));
        if (!requested)
        {
            Check(RegDeleteTreeW(HKEY_CURRENT_USER, open.c_str()));
            changed = true;
        }
        else if (command != OpenCommand() && !(current && Identify(command) == Copy::Other))
        {
            Write(open + L"\\command", nullptr, OpenCommand());
            changed = true;
        }
    }
    if (!requested && Same(Read(Key(L".torrent")), torrentClass))
    {
        Remove(Key(L".torrent"), nullptr);
        changed = true;
    }
    if (changed)
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    }
}

// Points TinyTorrent's shortcuts that start a missing copy at this one: the
// installer's Start menu entry, and the taskbar pins, which start the copy
// that was running when the person pinned it.
void RepairShortcuts()
{
    auto executable = Executable();
    auto name = std::filesystem::path(executable).filename().wstring();
    // This runs before the engine starts, and a profile redirected to a
    // network share that is unreachable would hold the start for the network
    // timeout, so only local folders are read.
    auto local = [](PWSTR folder)
    {
        std::wstring path(folder);
        return path.size() >= 3 && path[1] == L':' && GetDriveTypeW(path.substr(0, 3).c_str()) == DRIVE_FIXED;
    };
    std::vector<std::filesystem::path> files;
    PWSTR folder = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_Programs, 0, nullptr, &folder)) && local(folder))
    {
        files.push_back(std::filesystem::path(folder) / (std::wstring(productName) + L".lnk"));
    }
    CoTaskMemFree(folder);
    folder = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_UserPinned, 0, nullptr, &folder)) && local(folder))
    {
        std::error_code error;
        for (auto const& entry : std::filesystem::directory_iterator(std::filesystem::path(folder) / L"TaskBar", error))
        {
            if (Same(entry.path().extension().wstring(), L".lnk"))
            {
                files.push_back(entry.path());
            }
        }
    }
    CoTaskMemFree(folder);
    auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    for (auto const& file : files)
    {
        IShellLinkW* link = nullptr;
        IPersistFile* persist = nullptr;
        if (SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link))) &&
            SUCCEEDED(link->QueryInterface(IID_PPV_ARGS(&persist))) &&
            SUCCEEDED(persist->Load(file.c_str(), STGM_READWRITE)))
        {
            // A shortcut may store its paths with environment variables.
            auto expand = [](wchar_t const* raw)
            {
                wchar_t path[MAX_PATH]{};
                return ExpandEnvironmentStringsW(raw, path, MAX_PATH) - 1 < MAX_PATH ? std::wstring(path) : std::wstring();
            };
            wchar_t raw[MAX_PATH]{};
            link->GetPath(raw, MAX_PATH, nullptr, SLGP_RAWPATH);
            auto target = expand(raw);
            if (Same(std::filesystem::path(target).filename().wstring(), name) &&
                Identify(L"\"" + target + L"\"") == Copy::Missing)
            {
                wchar_t icon[MAX_PATH]{};
                int index = 0;
                link->GetIconLocation(icon, MAX_PATH, &index);
                // This product's shortcuts pass no arguments, and the engine
                // reads an argument it does not know as a torrent to add.
                link->SetPath(executable.c_str());
                link->SetArguments(L"");
                link->SetWorkingDirectory(std::filesystem::path(executable).parent_path().c_str());
                if (Same(expand(icon), target))
                {
                    link->SetIconLocation(executable.c_str(), index);
                }
                persist->Save(nullptr, TRUE);
            }
        }
        if (persist)
        {
            persist->Release();
        }
        if (link)
        {
            link->Release();
        }
    }
    if (SUCCEEDED(initialized))
    {
        CoUninitialize();
    }
}

// Another TinyTorrent copy's entry is still a registration, so it is "other",
// never "none".
std::string Owner(std::wstring const& command)
{
    return command.empty() ? "none" : Identify(command) == Copy::This ? "this" : "other";
}
}

Json Registration::Observe() const
{
    auto handlers = HandlerCommand();
    auto startup = Read(run, productName);
    // Each torrent or magnet handler that starts a missing program, and
    // whether removing it needs an administrator.
    auto broken = Json::array();
    for (auto const& entry : FindBroken())
    {
        broken.push_back({{"program", Readable(entry.program)}, {"machine", entry.root == HKEY_LOCAL_MACHINE},
            {"class", Readable(entry.progId)}});
    }
    return {
        {"handlers", Owner(handlers)},
        {"handlers_target", Readable(Target(handlers))},
        {"startup", Owner(startup)},
        {"startup_target", Readable(Target(startup))},
        {"torrent_default", IsDefault(L".torrent", AT_FILEEXTENSION, torrentClass)},
        {"magnet_default", IsDefault(L"magnet", AT_URLPROTOCOL, magnetClass)},
        {"broken", broken}
    };
}

std::vector<std::string> Registration::MissingPrograms() const
{
    std::vector<std::wstring> found;
    for (auto const& broken : FindBroken())
    {
        if (std::ranges::none_of(found, [&](auto const& kept) { return Same(kept, broken.program); }))
        {
            found.push_back(broken.program);
        }
    }
    std::vector<std::string> programs;
    for (auto const& program : found)
    {
        programs.push_back(Readable(program));
    }
    return programs;
}

// The window names the classes, because this runs as an administrator,
// possibly another account, whose own registry does not show which classes
// the person's Windows opens torrents with. Each command is checked again
// here, so a name can remove only a command that starts a missing program.
// One that Windows refuses to change stays reported and does not stop the
// others.
void Registration::RepairMachine(std::vector<std::wstring> const& classes) const
{
    bool removed = false;
    for (auto const& progId : classes)
    {
        if (!IsClass(progId))
        {
            continue;
        }
        auto command = Read(Key(progId) + L"\\shell\\open\\command", nullptr, HKEY_LOCAL_MACHINE);
        if (command.empty() || Identify(command) != Copy::Missing)
        {
            continue;
        }
        try
        {
            RemoveCommand(HKEY_LOCAL_MACHINE, progId, Target(command));
            removed = true;
        }
        catch (std::exception const&)
        {
        }
    }
    if (removed)
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    }
}

// Moves TinyTorrent's existing entries to this executable unless they are
// another existing copy's entries in this product's form, so running a second
// copy never takes them and deleting it never breaks them. An entry in another
// form was written by the product before this one, which uses the same names.
// An entry the person removed stays removed. The RegisteredApplications value
// records the handler request: registering writes it last and unregistering
// removes it first, so handler entries without it are the remains of a
// partial change and are removed, never brought back. Observe reports the
// actual entries, including a partially completed repair.
void Registration::Repair() const
{
    // Each step changes separate entries, so a failure in one leaves the
    // others to run; a start never fails because of them.
    auto attempt = [](auto&& step)
    {
        try
        {
            step();
        }
        catch (std::exception const&)
        {
        }
    };
    attempt([this]
    {
        auto handlers = HandlerCommand();
        bool current = !handlers.empty() && handlers == OpenCommand(Target(handlers));
        // The product before this one used the same classes in another
        // command form without recording the request, so its handlers move to
        // this copy like any copy's.
        bool legacy = !handlers.empty() && !current;
        if (Read(registered, productName) != capabilities && !legacy)
        {
            if (HandlersPresent())
            {
                UnregisterHandlers();
            }
            return;
        }
        bool kept = current && Identify(handlers) == Copy::Other;
        if (!kept && !HandlersComplete())
        {
            RegisterHandlers();
        }
    });
    attempt([]
    {
        auto startup = Read(run, productName);
        if (!startup.empty() && (Identify(startup) == Copy::Missing || startup != Launch(Target(startup))))
        {
            Write(run, productName, Launch());
        }
    });
    attempt([] { RepairLegacy(Read(registered, productName) == capabilities); });
    attempt(RepairShortcuts);
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
    Remove(registered, productName);
    for (auto const* progId : classes)
    {
        Check(RegDeleteTreeW(HKEY_CURRENT_USER, Key(progId).c_str()));
    }
    Remove(openWith, torrentClass);
    Check(RegDeleteTreeW(HKEY_CURRENT_USER, capabilities));
    RepairLegacy(false);
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
}

Json Registration::Execute(std::string const& name) const
{
    auto action = Parse(actions, name);
    if (!action)
    {
        return Failure(ErrorCode::InvalidRequest, "Unknown registration action.");
    }
    try
    {
        switch (*action)
        {
        case RegistrationAction::OpenDefaults:
            RemoveBroken();
            RegisterHandlers();
            break;
        case RegistrationAction::RegisterHandlers:
            RegisterHandlers();
            break;
        case RegistrationAction::UnregisterHandlers:
            UnregisterHandlers();
            break;
        case RegistrationAction::EnableStartup:
            Write(run, productName, Launch());
            break;
        case RegistrationAction::DisableStartup:
            Remove(run, productName);
            break;
        case RegistrationAction::Observe:
        case RegistrationAction::OpenStartup:
            break;
        }
        auto observed = Observe();
        // Windows lets only the person choose default apps, so open_defaults
        // opens Settings while another working app is the default.
        bool other = OtherDefault(kinds[0], torrentClass) || OtherDefault(kinds[1], magnetClass);
        if (action == RegistrationAction::OpenStartup || (action == RegistrationAction::OpenDefaults && other))
        {
            auto uri = action == RegistrationAction::OpenStartup
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
