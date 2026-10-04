#pragma once
#include "Pipe.h"
#include "Registration.h"
#include <optional>
#include <oleacc.h>

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
    void Relaunch(DWORD process);
    void Exit();
    void Shutdown();
    void Tick();
    void Refresh();
    void Pause();
    void Startup(std::string failure = {}, std::wstring detail = {});
    void Notice(Json notice);
    void Notify();
    void Power();
    void SessionEnd();
    void Menu(POINT point);
    LRESULT MenuRow(UINT message, LPARAM parameter);
    std::wstring Format(std::string const& group, std::string const& key, std::vector<std::wstring> const& values) const;
    std::wstring Rate(std::int64_t bytes) const;
    std::wstring Rates() const;
    std::wstring Counts() const;
    std::wstring Tooltip() const;
    static LRESULT CALLBACK Surface(HWND window, UINT message, WPARAM first, LPARAM second);
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
    bool ticking_ = false;
    bool adding_ = false;
    bool reopen_ = false;
    bool ending_ = false;
    std::optional<bool> end_saved_;
    bool saving_ = false;
    bool notice_saving_ = false;
    unsigned sequence_ = 0;
    ULONGLONG opened_ = 0;
    UINT explorer_ = 0;
    std::string language_ = "en";
    HMENU menu_ = nullptr;
    HFONT menu_font_ = nullptr;
    std::wstring menu_text_[2];
    MSAAMENUINFO menu_names_[2]{};
    HFONT font_ = nullptr;
    HANDLE power_ = INVALID_HANDLE_VALUE;
    bool awake_ = false;
    HPOWERNOTIFY power_notification_ = nullptr;
    std::string startup_failure_;
    std::wstring startup_detail_;
    std::wstring tooltip_;
    Json activity_ = Json::object();
    std::deque<Json> notices_;
    Json failed_notice_;
    unsigned failed_notices_ = 0;
    Json notification_;
    ULONGLONG notice_due_ = 0;
    unsigned notice_count_ = 0;
    Json strings_;
    std::unique_ptr<Engine> engine_;
    std::unique_ptr<Pipe> pipe_;
    Registration registration_;
    Pipe::Client ui_;
    std::deque<Activation> waiting_;
    std::deque<Activation> activations_;
    std::mutex mutex_;
    std::deque<std::function<void()>> pending_;
};
std::wstring LogonSid();
Json LoadStrings(std::string const& language = "en");
}
