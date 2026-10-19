#pragma once
#include "Pipe.h"
#include <filesystem>
#include <optional>
#include <string>
#include <variant>

namespace tt::desktop
{
// Why a launch cannot start, as the failure popup shows it.
struct Refusal
{
    // The explanation's key in the error group.
    std::string key;
    // One diagnostic line, such as the folder the failure concerns.
    std::wstring detail;
    // Adds Open folder for the folder that holds what the person must close.
    std::optional<std::filesystem::path> folder;
    // Adds Retry, which starts this launch again, for a failure the person
    // can resolve without leaving the popup.
    bool retry = false;
};

// What a launch of the engine asks for, read from its command line.
struct Arguments
{
    // Starts the engine or opens the running one, with the torrents to add.
    struct Open
    {
        // Starts in the tray without opening the window.
        bool background = false;
        std::vector<std::string> sources;
#ifdef _DEBUG
        // Shows the popup of an Added notice for a sample torrent.
        bool sample = false;
#endif
    };
    // Ends the running engine.
    struct Exit
    {
    };
    struct Register
    {
        RegistrationAction action;
    };
    // Removes the all-users open command of each named class that starts a
    // missing program. The window starts it as an administrator.
    struct Repair
    {
        std::vector<std::wstring> classes;
    };

    // Reads a command line. Options that are incomplete or cannot be combined
    // become a Refusal, so a headless launch still reports it without a popup.
    static Arguments Read(wchar_t const* line);
    // Reports failures on the error output instead of in a popup.
    bool headless = false;
    // The data folder; empty for the person's own store.
    std::filesystem::path directory;
    std::variant<Open, Exit, Register, Repair, Refusal> request;
};

// The engine instance of this logon session after a launch claimed it.
struct Ownership
{
    // The engine lock, held while this process is the engine. Empty when the
    // launch has finished, because the running engine took its request or
    // the person kept that engine.
    OwnedHandle lock;
    // The launch's exit code once it has finished.
    int code = 0;
};

// Takes the engine lock of the logon session `sid` for an Open, Exit or
// Register request, or hands it to the engine that holds the lock.
// `arguments.directory` names the store that a running engine must serve.
// Exit waits for the running engine to end. Throws Refusal when another
// engine keeps this launch out.
Ownership Claim(std::wstring const& sid, Arguments const& arguments);
}
