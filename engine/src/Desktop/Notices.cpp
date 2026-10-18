#include "Desktop/Notices.h"
#include "Engine.h"
#include <algorithm>
#include <shellapi.h>

namespace tt::desktop
{
namespace
{
// Notices that arrive within this many milliseconds share one popup.
constexpr ULONGLONG noticeDelay = 1000;

bool AcceptsNotifications()
{
    QUERY_USER_NOTIFICATION_STATE state{};
    return SUCCEEDED(SHQueryUserNotificationState(&state)) && state == QUNS_ACCEPTS_NOTIFICATIONS;
}

void ShowFolder(std::filesystem::path const& folder)
{
    ShellExecuteW(nullptr, L"open", folder.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

// Torrent problems, which show the error icon and share a popup by kind.
bool IsProblem(NoticeKind kind)
{
    return kind == NoticeKind::Error || kind == NoticeKind::AddFailed || kind == NoticeKind::DeleteFailed;
}
}

bool UsesWindow(NoticeKind kind)
{
    return IsProblem(kind) || kind == NoticeKind::Completed;
}

std::optional<NoticeSwitch> Switch(NoticeKind kind)
{
    switch (kind)
    {
    case NoticeKind::Completed:
        return NoticeSwitch::Finished;
    case NoticeKind::Added:
    case NoticeKind::Duplicate:
        return NoticeSwitch::Added;
    case NoticeKind::Error:
    case NoticeKind::AddFailed:
    case NoticeKind::DeleteFailed:
    case NoticeKind::MissingProgram:
        return NoticeSwitch::Problems;
    case NoticeKind::Background:
        return NoticeSwitch::Background;
    case NoticeKind::Aggregate:
        return std::nullopt;
    }
    return std::nullopt;
}

Notices::Notices(Strings const& strings, bool headless, HICON errorIcon, Commands commands)
    : strings_(strings), headless_(headless), errorIcon_(errorIcon), commands_(std::move(commands)),
      popup_(strings)
{
}

void Notices::Update(Activity const& activity)
{
    activity_ = activity;
    popup_.SetTheme(activity_.theme);
}

void Notices::Close()
{
    popup_.Close();
}

bool Notices::Accepts(NoticeKind kind) const
{
    return !headless_ && IsOn(kind);
}

bool Notices::IsOn(NoticeKind kind) const
{
    auto notices = Switch(kind);
    if (!notices)
    {
        return true;
    }
    switch (*notices)
    {
    case NoticeSwitch::Finished:
        return activity_.notificationsEnabled;
    case NoticeSwitch::Added:
        return activity_.notifiesAdded;
    case NoticeSwitch::Problems:
        return activity_.notifiesProblems;
    case NoticeSwitch::Background:
        return activity_.notifiesBackground;
    }
    return false;
}

void Notices::Queue(Notice notice)
{
    auto kind = notice.kind;
    if (!Accepts(kind))
    {
        return;
    }
    if (kind == NoticeKind::Background)
    {
        ShowBackground();
        return;
    }
    // One batch per kind bounds pending context while an open menu keeps its
    // popup unchanged. Different notification switches never share a batch.
    auto [entry, inserted] = batches_.try_emplace(kind, Batch{std::move(notice), 1, GetTickCount64() + noticeDelay});
    if (!inserted && kind != NoticeKind::MissingProgram)
    {
        ++entry->second.count;
    }
}

void Notices::Report(Failure failure)
{
    if (headless_ || (popup_.IsOpen() && shownFailure_ == failure.code))
    {
        return;
    }
    // Notify shows it within a tick only when nothing holds it back.
    if (popup_.IsOpen() || !failures_.empty() || !AcceptsNotifications())
    {
        failure.interactive = false;
    }
    auto waiting = std::find_if(failures_.begin(), failures_.end(),
        [&failure](Failure const& other) { return other.code == failure.code; });
    if (waiting == failures_.end())
    {
        failures_.push_back(std::move(failure));
    }
    else
    {
        *waiting = std::move(failure);
    }
}

void Notices::Notify()
{
    // Let the displayed popup finish its lifetime, including hover and menu
    // interaction, before showing another.
    if (popup_.IsOpen())
    {
        return;
    }
    // Failures and notices wait while Windows holds notifications back, such
    // as over a full-screen app, so a stopped torrent is still reported later.
    if (!AcceptsNotifications())
    {
        return;
    }
    if (!failures_.empty())
    {
        auto failure = std::move(failures_.front());
        failures_.erase(failures_.begin());
        ShowFailure(std::move(failure));
        return;
    }
    if (batches_.empty())
    {
        return;
    }
    auto next = std::min_element(batches_.begin(), batches_.end(), [](auto const& left, auto const& right)
    {
        return left.second.due < right.second.due;
    });
    if (GetTickCount64() < next->second.due)
    {
        return;
    }
    auto batch = std::move(next->second);
    batches_.erase(next);
    auto kind = batch.first.kind;
    if (!Accepts(kind))
    {
        return;
    }
    if (IsProblem(kind))
    {
        Show(Problems(batch.first, batch.count), Switch(kind));
    }
    else if (batch.count == 1)
    {
        Show(Message(batch.first), Switch(kind));
    }
    else
    {
        Show(Message({.kind = NoticeKind::Aggregate, .count = batch.count}), Switch(kind));
    }
}

// Selecting the message opens the window, where the person can act on it.
void Notices::ShowFailure(Failure failure)
{
    shownFailure_ = failure.code;
    popup_.Show({.title = failure.title.value_or(strings_.Text("notification", "failure")),
        .body = ErrorText(failure.code), .select = failure.opens ? commands_.open : nullptr,
        .detail = std::move(failure.detail), .actions = std::move(failure.actions), .icon = errorIcon_,
        .stay = failure.interactive ? NoticeStay::Interactive : NoticeStay::Persistent});
}

Popup::Content Notices::Message(Notice const& notice) const
{
    Popup::Content content{.select = Selection(notice)};
    if (notice.kind == NoticeKind::Aggregate)
    {
        content.title = strings_.Format("notification", "aggregate", {Strings::Number(notice.count)});
    }
    else if (notice.kind == NoticeKind::MissingProgram)
    {
        content.title = strings_.Text("notification", "missing_title");
        content.body = strings_.Format("notification", notice.count > 1 ? "missing_programs" : "missing_program",
            {Wide(notice.name), Strings::Number(notice.count - 1)});
    }
    else
    {
        content.title = strings_.Text("notification", ToString(notice.kind));
        content.body = Wide(notice.name);
        auto problem = ErrorText(notice.code);
        if (!notice.detail.empty())
        {
            if (!problem.empty())
            {
                problem += L"\n";
            }
            problem += Wide(notice.detail);
        }
        if (!problem.empty())
        {
            if (!content.body.empty())
            {
                content.body += L"\n\n";
            }
            content.body += problem;
        }
        if (!notice.folder.empty())
        {
            content.actions = {
                OpenFolder(strings_, Wide(notice.folder), strings_.Text("notification", "files_folder_tip"))};
        }
    }
    return content;
}

// Problems of one kind share a popup that shows the first of them. With
// several, it counts them and opens the window, which lists each one.
Popup::Content Notices::Problems(Notice const& first, unsigned count) const
{
    auto notice = first;
    if (notice.kind == NoticeKind::AddFailed)
    {
        notice.name = Utf8(IsMagnet(first.name) ? strings_.Text("notification", "magnet") :
            std::filesystem::path(Wide(first.name)).filename().wstring());
    }
    auto content = Message(notice);
    content.icon = errorIcon_;
    if (count > 1)
    {
        content.title = strings_.Format("notification", "failures", {Strings::Number(count)});
        content.select = commands_.open;
        content.actions.clear();
    }
    return content;
}

std::wstring Notices::ErrorText(std::string const& code) const
{
    if (code.empty())
    {
        return {};
    }
    auto message = strings_.Text("error", code);
    if (message == Wide("error." + code))
    {
        message = strings_.Text("error", "unknown");
    }
    return message;
}

// A completed torrent opens its folder; a missing program opens its Settings
// card; any other notice opens the window.
std::function<void()> Notices::Selection(Notice const& notice) const
{
    if (notice.kind == NoticeKind::MissingProgram)
    {
        return [this] { commands_.openSettings("windows_integration"); };
    }
    if (notice.kind == NoticeKind::Completed)
    {
        return [this, id = notice.torrentId]
        {
            auto folder = commands_.folder(id);
            if (folder.empty())
            {
                commands_.open();
            }
            else
            {
                ShowFolder(Wide(folder));
            }
        };
    }
    return commands_.open;
}

// Lucide's rotate-ccw glyph, as the window's Retry button shows.
Popup::Action Notices::Retry(Strings const& strings, std::wstring tooltip, std::function<void()> run)
{
    return {.glyph = L'\uE148', .label = strings.Text("notification", "retry"), .tooltip = std::move(tooltip),
        .run = std::move(run), .automationId = L"Retry"};
}

// Lucide's folder-open glyph. Open alone would read as opening the window, so
// the label names the folder. Showing a folder leaves the popup open.
Popup::Action Notices::OpenFolder(Strings const& strings, std::filesystem::path folder, std::wstring tooltip)
{
    return {.glyph = L'\uE247', .label = strings.Text("notification", "open_folder"), .tooltip = std::move(tooltip),
        .run = [folder = std::move(folder)] { ShowFolder(folder); }, .closes = false, .automationId = L"OpenFolder"};
}

// Lucide's power glyph, as the window's Exit command shows.
Popup::Action Notices::ForceExit(Strings const& strings, std::function<void()> run)
{
    return {.glyph = L'\uE140', .label = strings.Text("notification", "force_exit"),
        .tooltip = strings.Text("notification", "force_exit_tip"), .run = std::move(run),
        .automationId = L"ForceExit"};
}

// Selecting the message of this popup does nothing, because opening the
// window the person just closed would undo their action.
void Notices::ShowBackground()
{
    if (popup_.IsPersistent() || popup_.IsMenuOpen())
    {
        return;
    }
    if (AcceptsNotifications())
    {
        Show({.title = strings_.Text("notification", "background"),
            .body = strings_.Text("tray", "background")}, NoticeSwitch::Background);
    }
}

void Notices::Show(Popup::Content content, std::optional<NoticeSwitch> notices)
{
    shownFailure_.reset();
    if (notices)
    {
        content.menuTitle = strings_.Text("notification", "fewer");
        // Lucide's bell-minus, bell-off and settings glyphs.
        content.menu = {
            {L'\uE1F0', strings_.Text("notification", "off_" + ToString(*notices)),
                [this, shown = *notices] { commands_.turnOff({shown}); }, L"TurnOff"},
            {L'\uE05A', strings_.Text("notification", "off_all"),
                [this] { commands_.turnOff(AllNoticeSwitches()); }, L"TurnOffAll"},
            {L'\uE154', strings_.Text("notification", "settings"),
                [this] { commands_.openSettings("notifications"); }, L"NotificationSettings"}};
    }
    popup_.Show(std::move(content));
}

#ifdef _DEBUG
void Notices::ShowSample()
{
    Show(Message({.kind = NoticeKind::Added, .name = "Sample torrent"}), NoticeSwitch::Added);
}
#endif
}
