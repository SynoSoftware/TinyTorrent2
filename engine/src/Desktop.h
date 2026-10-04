#pragma once
#include "Pipe.h"

namespace tiny
{
class Desktop
{
public:
    Desktop(std::filesystem::path directory, std::wstring sid, bool headless);
    ~Desktop();
    int Run(bool background, Json sources = Json::array());
    static bool ValidSources(Json const& sources);
private:
    struct Activation
    {
        std::string activation_id;
        std::vector<std::string> sources;
    };
    static LRESULT CALLBACK Window(HWND window, UINT message, WPARAM first, LPARAM second);
    LRESULT Handle(UINT message, WPARAM first, LPARAM second);
    void Dispatch(Pipe::Client client, Json request);
    void Receive(Pipe::Client const& client, Json const& request);
    void Open();
    void Exit();
    void Shutdown();
    void Tick();
    void Refresh();
    Json Activate(Json const& sources);
    void Sources();
    void Finish(Json const& response);
    void Tray(DWORD action);
    void Feedback(std::string const& key, std::wstring detail = {});
    std::wstring Text(std::string const& group, std::string const& key) const;
    HWND window_ = nullptr;
    HWND broadcast_ = nullptr;
    HWND splash_ = nullptr;
    HANDLE ownership_ = INVALID_HANDLE_VALUE;
    HANDLE process_ = nullptr;
    bool headless_;
    bool exiting_ = false;
    bool notified_ = false;
    bool ticking_ = false;
    bool adding_ = false;
    bool reopen_ = false;
    unsigned sequence_ = 0;
    ULONGLONG opened_ = 0;
    UINT explorer_ = 0;
    std::string language_ = "en";
    HMENU menu_ = nullptr;
    Json strings_;
    std::unique_ptr<Engine> engine_;
    std::unique_ptr<Pipe> pipe_;
    Pipe::Client ui_;
    std::deque<Activation> waiting_;
    std::deque<Activation> activations_;
    std::mutex mutex_;
    std::deque<std::function<void()>> pending_;
};
std::wstring LogonSid();
Json LoadStrings(std::string const& language = "en");
}
