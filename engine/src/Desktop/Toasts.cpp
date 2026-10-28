#include "Desktop/Toasts.h"
#include "Engine.h"
#include "Resources.h"
#include <algorithm>
#include <shellapi.h>
#include <utility>

namespace tt::desktop
{
namespace
{
// Toasts that arrive within this many milliseconds share one popup.
constexpr ULONGLONG delay = 1000;

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
bool IsProblem(ToastKind kind)
{
    return kind == ToastKind::Error || kind == ToastKind::AddFailed || kind == ToastKind::DeleteFailed;
}
}

bool UsesWindow(ToastKind kind)
{
    return IsProblem(kind) || kind == ToastKind::Completed;
}

std::optional<ToastSwitch> Switch(ToastKind kind)
{
    switch (kind)
    {
    case ToastKind::Completed:
        return ToastSwitch::Finished;
    case ToastKind::Added:
    case ToastKind::Duplicate:
        return ToastSwitch::Added;
    case ToastKind::Error:
    case ToastKind::AddFailed:
    case ToastKind::DeleteFailed:
    case ToastKind::MissingProgram:
        return ToastSwitch::Problems;
    case ToastKind::Background:
        return ToastSwitch::Background;
    case ToastKind::Aggregate:
    case ToastKind::Unfinished:
        return std::nullopt;
    }
    return std::nullopt;
}

HICON ErrorIcon()
{
    ICONINFO icon{};
    if (!GetIconInfo(LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(IDI_TINYTORRENT)), &icon))
    {
        return nullptr;
    }
    HICON result = nullptr;
    auto target = CreateCompatibleDC(nullptr);
    BITMAP color{};
    if (target && icon.hbmColor && GetObjectW(icon.hbmColor, static_cast<int>(sizeof(color)), &color))
    {
        auto previous = SelectObject(target, icon.hbmColor);
        auto width = static_cast<int>(color.bmWidth / 2);
        auto height = static_cast<int>(color.bmHeight / 2);
        auto drawn = DrawIconEx(target, width, height, LoadIconW(nullptr, IDI_ERROR), width, height, 0, nullptr, DI_NORMAL);
        SelectObject(target, previous);
        if (drawn)
        {
            result = CreateIconIndirect(&icon);
        }
    }
    if (target) DeleteDC(target);
    DeleteObject(icon.hbmColor);
    DeleteObject(icon.hbmMask);
    return result;
}

Toasts::Toasts(Strings const& strings, bool headless, HICON errorIcon, Commands commands)
    : strings_(strings), headless_(headless), errorIcon_(errorIcon), commands_(std::move(commands)),
      popup_(strings)
{
}

void Toasts::Update(Activity const& activity)
{
    activity_ = activity;
    popup_.SetTheme(activity_.theme);
}

void Toasts::Close()
{
    popup_.Close();
}

bool Toasts::IsOn(ToastKind kind) const
{
    auto toasts = Switch(kind);
    return !toasts || activity_.toasts.contains(*toasts);
}

void Toasts::Queue(Toast toast)
{
    auto kind = toast.kind;
    auto key = std::make_pair(kind, toast.condition ? toast.condition->key : std::string());
    if (toast.condition && toast.condition->change != ProblemChange::Raised)
    {
        auto pending = batches_.find(key);
        if (toast.condition->change == ProblemChange::Resolved)
        {
            batches_.erase(key);
            if (shownCondition_ == toast.condition->key)
            {
                popup_.Close();
                shownCondition_.reset();
            }
        }
        else if (pending != batches_.end())
        {
            toast.condition->change = ProblemChange::Raised;
            pending->second.first = std::move(toast);
            return;
        }
        else if (shownCondition_ == toast.condition->key)
            popup_.Update(Problems(toast, 1));
        if (commands_.window)
            commands_.window(toast);
        return;
    }
    // The window's message bar shows a condition while it lasts; the
    // Problems switch turns off only the popup.
    if (!IsOn(kind))
    {
        if (toast.condition && commands_.window)
            commands_.window(toast);
        return;
    }
    if (kind == ToastKind::Completed && commands_.window && commands_.window(toast))
    {
        return;
    }
    if (headless_)
    {
        auto content = Message(toast);
        auto message = content.title + L"\n" + content.body + L"\n";
        OutputDebugStringW(message.c_str());
        return;
    }
    if (kind == ToastKind::Background)
    {
        ShowBackground();
        return;
    }
    if (!toast.condition && !batches_.contains(key) &&
        std::count_if(batches_.begin(), batches_.end(), [](auto const& batch)
            { return !batch.second.first.condition; }) >= Toast::limit)
        return;
    auto count = toast.count;
    auto [entry, inserted] = batches_.try_emplace(std::move(key), Batch{std::move(toast), 1, GetTickCount64() + delay});
    if (!inserted && entry->second.first.condition)
    {
        entry->second.first.count = count;
    }
    else if (!inserted && kind != ToastKind::MissingProgram)
    {
        ++entry->second.count;
    }
}

void Toasts::Report(Failure failure)
{
    if (headless_)
    {
        auto message = ErrorText(failure.code);
        if (!failure.detail.empty())
        {
            message += L"\n\n" + failure.detail;
        }
        message += L"\n";
        OutputDebugStringW(message.c_str());
        return;
    }
    if (popup_.IsOpen() && shownFailure_ && shownFailure_->code == failure.code)
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

void Toasts::Notify()
{
    // Window delivery shares the burst policy but not the desktop's focus/DND gate.
    for (auto next = batches_.begin(); next != batches_.end();)
    {
        auto& batch = next->second;
        if (!IsProblem(batch.first.kind) || GetTickCount64() < batch.due || !commands_.window)
        {
            ++next;
            continue;
        }
        auto toast = batch.first;
        if (!toast.condition)
            toast.count = batch.count;
        if ((!IsOn(toast.kind) && !toast.condition) || commands_.window(toast))
            next = batches_.erase(next);
        else
            ++next;
    }
    // Let the displayed popup finish its lifetime, including hover and menu
    // interaction, before showing another.
    if (popup_.IsOpen())
    {
        return;
    }
    // Failures and toasts wait while Windows holds notifications back, such
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
    if (!IsOn(kind))
    {
        return;
    }
    if (IsProblem(kind))
    {
        Show(Problems(batch.first, batch.count), Switch(kind));
        if (batch.first.condition)
            shownCondition_ = batch.first.condition->key;
    }
    else if (batch.count == 1)
    {
        Show(Message(batch.first), Switch(kind));
    }
    else
    {
        Show(Message({.kind = ToastKind::Aggregate, .count = batch.count}), Switch(kind));
    }
}

// Selecting the message opens the window, where the person can act on it.
void Toasts::ShowFailure(Failure failure)
{
    shownCondition_.reset();
    shownFailure_ = failure;
    popup_.Show({.title = failure.title.value_or(strings_.Text("notification", "failure")),
        .body = ErrorText(failure.code), .select = failure.opens ? commands_.open : nullptr,
        .detail = std::move(failure.detail), .actions = std::move(failure.actions), .icon = errorIcon_,
        .stay = failure.interactive ? ToastStay::Interactive : ToastStay::Persistent});
}

void Toasts::Ask(Popup::Prompt prompt)
{
    // A failure stays until the person closes it, so it shows again after
    // the prompt.
    if (popup_.IsOpen() && shownFailure_)
    {
        shownFailure_->interactive = false;
        failures_.insert(failures_.begin(), std::move(*shownFailure_));
    }
    shownFailure_.reset();
    shownCondition_.reset();
    popup_.Ask(std::move(prompt));
}

Popup::Content Toasts::Message(Toast const& toast) const
{
    Popup::Content content{.select = Selection(toast)};
    // The notice of the torrents that wait for the person's Retry.
    bool stopped = toast.condition && toast.condition->key == Toast::Condition::stopped;
    if (toast.kind == ToastKind::Aggregate)
    {
        content.title = strings_.Format("notification", "aggregate", {Strings::Number(toast.count)});
    }
    else if (toast.kind == ToastKind::MissingProgram)
    {
        content.title = strings_.Text("notification", "missing_title");
        content.body = strings_.Format("notification", toast.count > 1 ? "missing_programs" : "missing_program",
            {Wide(toast.name), Strings::Number(toast.count - 1)});
    }
    else if (toast.kind == ToastKind::Error && toast.code == "download_location_unavailable")
    {
        content.title = strings_.Text("notification", "location_unavailable");
        content.body = strings_.Format("notification", toast.count == 1 ? "location_torrents_one" : "location_torrents_other",
            {Strings::Number(toast.count)});
        if (!toast.detail.empty())
            content.body = ErrorText(toast.detail) + L"\n" + content.body;
        content.location = Popup::Location{strings_.Text("notification", "download_folder"), Wide(toast.folder)};
        content.actions = {OpenFolder(strings_, Wide(toast.folder), strings_.Text("notification", "files_folder_tip"))};
    }
    // Every torrent that waits for the person shares one notice, so one Retry
    // continues them all. Selecting it opens the window, which lists them.
    else if (stopped && toast.count > 1)
    {
        content.title = strings_.Format("notification", "torrents_stopped", {Strings::Number(toast.count)});
        content.body = ErrorText(toast.code, true);
        content.select = commands_.open;
        content.actions = {Retry(strings_, strings_.Text("notification", "retry_all_tip"), commands_.retryAll)};
    }
    // The work waits until the person answers, so the notice stays.
    else if (toast.kind == ToastKind::Unfinished)
    {
        content.title = strings_.Text("notification", "unfinished_title");
        content.body = strings_.Text("notification", "unfinished");
        content.stay = ToastStay::Persistent;
    }
    else
    {
        content.title = strings_.Text("notification", ToString(toast.kind));
        if (toast.kind == ToastKind::Error && (toast.code == "torrent_error" ||
            toast.code == "download_path_missing" || toast.code == "download_path_invalid" ||
            toast.code == "download_path_unavailable" || toast.code == "disk_full" ||
            toast.code == "drive_stalled" || toast.code == "alias_conflict"))
            content.title = strings_.Text("notification", "torrent_stopped");
        content.body = Wide(toast.name);
        auto problem = toast.code == "torrent_error" && !toast.detail.empty() ?
            std::wstring() : ErrorText(toast.code, !toast.detail.empty());
        if (!toast.detail.empty())
        {
            if (!problem.empty())
            {
                problem += L"\n";
            }
            problem += Wide(toast.detail);
        }
        if (!problem.empty())
        {
            if (!content.body.empty())
            {
                content.body += L"\n\n";
            }
            content.body += problem;
        }
        if (toast.kind == ToastKind::Error && !toast.torrentId.empty())
        {
            auto folder = commands_.folder(toast.torrentId);
            if (!folder.empty())
                content.location = Popup::Location{strings_.Text("notification", "download_folder"), Wide(folder)};
            content.actions.push_back({.glyph = L'\uE106', .label = strings_.Text("notification", "show_torrent"),
                .tooltip = strings_.Text("notification", "show_torrent_tip"), .run = content.select,
                .closes = false, .automationId = L"ShowTorrent"});
            // Retry answers an error that the engine stopped retrying; the
            // toast still names the folder for a person who fixes it there.
            if (stopped)
            {
                content.actions.push_back(Retry(strings_, strings_.Text("notification", "retry_torrent_tip"),
                    commands_.retryAll));
            }
            else
            {
                content.actions.push_back({.glyph = L'\uE247', .label = strings_.Text("notification", "open_folder"),
                    .tooltip = strings_.Text("notification", "files_folder_tip"),
                    .run = [this, id = toast.torrentId]
                    {
                        auto folder = commands_.folder(id);
                        if (!folder.empty())
                            ShowFolder(Wide(folder));
                    }, .closes = false, .automationId = L"OpenFolder"});
            }
        }
        else
        {
            if (!toast.folder.empty())
                content.actions = {
                    OpenFolder(strings_, Wide(toast.folder), strings_.Text("notification", "files_folder_tip"))};
            // A removed torrent's give-back has no row to show, and its Retry
            // still continues it.
            if (stopped)
                content.actions.push_back(Retry(strings_, strings_.Text("notification", "retry_torrent_tip"),
                    commands_.retryAll));
        }
    }
    return content;
}

// Problems of one kind share a popup that shows the first of them. With
// several, it counts them and opens the window, which lists each one.
Popup::Content Toasts::Problems(Toast const& first, unsigned count) const
{
    auto toast = first;
    if (toast.kind == ToastKind::AddFailed)
    {
        toast.name = Utf8(IsMagnet(first.name) ? strings_.Text("notification", "magnet") :
            std::filesystem::path(Wide(first.name)).filename().wstring());
    }
    auto content = Message(toast);
    content.icon = errorIcon_;
    if (count > 1)
    {
        content.title = strings_.Format("notification", "failures", {Strings::Number(count)});
        content.select = commands_.open;
        content.location.reset();
        content.actions.clear();
    }
    return content;
}

std::wstring Toasts::ErrorText(std::string const& code, bool explained) const
{
    if (code.empty())
    {
        return {};
    }
    return strings_.Find("error", code).value_or(explained ? std::wstring() : strings_.Text("error", "unknown"));
}

// A completed torrent opens its folder; a missing program opens its Settings
// card; a torrent problem opens that torrent; other toasts open the window.
std::function<void()> Toasts::Selection(Toast const& toast) const
{
    if (toast.kind == ToastKind::MissingProgram)
    {
        return [this] { commands_.openSettings("windows_integration"); };
    }
    if (toast.kind == ToastKind::Completed)
    {
        return [this, id = toast.torrentId]
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
    if (toast.kind == ToastKind::Error && !toast.torrentId.empty())
    {
        return [this, id = toast.torrentId] { commands_.openTorrent(id); };
    }
    return commands_.open;
}

// Lucide's rotate-ccw glyph, as the window's Retry button shows.
Popup::Action Toasts::Retry(Strings const& strings, std::wstring tooltip, std::function<void()> run)
{
    return {.glyph = L'\uE148', .label = strings.Text("notification", "retry"), .tooltip = std::move(tooltip),
        .run = std::move(run), .automationId = L"Retry"};
}

// Lucide's folder-open glyph. Open alone would read as opening the window, so
// the label names the folder. Showing a folder leaves the popup open.
Popup::Action Toasts::OpenFolder(Strings const& strings, std::filesystem::path folder, std::wstring tooltip)
{
    return {.glyph = L'\uE247', .label = strings.Text("notification", "open_folder"), .tooltip = std::move(tooltip),
        .run = [folder = std::move(folder)] { ShowFolder(folder); }, .closes = false, .automationId = L"OpenFolder"};
}

// Lucide's power glyph, as the window's Exit command shows.
Popup::Action Toasts::ForceExit(Strings const& strings, std::function<void()> run)
{
    return {.glyph = L'\uE140', .label = strings.Text("notification", "force_exit"),
        .tooltip = strings.Text("notification", "force_exit_tip"), .run = std::move(run),
        .automationId = L"ForceExit"};
}

// Selecting the message of this popup does nothing, because opening the
// window the person just closed would undo their action.
void Toasts::ShowBackground()
{
    if (popup_.IsPersistent() || popup_.IsMenuOpen())
    {
        return;
    }
    if (AcceptsNotifications())
    {
        Show({.title = strings_.Text("notification", "background"),
            .body = strings_.Text("tray", "background")}, ToastSwitch::Background);
    }
}

void Toasts::Show(Popup::Content content, std::optional<ToastSwitch> toasts)
{
    shownFailure_.reset();
    shownCondition_.reset();
    if (toasts)
    {
        content.menuTitle = strings_.Text("notification", "fewer");
        // Lucide's bell-minus, bell-off and settings glyphs.
        content.menu = {
            {L'\uE1F0', strings_.Text("notification", "off_" + ToString(*toasts)),
                [this, shown = *toasts] { commands_.turnOff({shown}); }, L"TurnOff"},
            {L'\uE05A', strings_.Text("notification", "off_all"),
                [this] { commands_.turnOff(AllToastSwitches()); }, L"TurnOffAll"},
            {L'\uE154', strings_.Text("notification", "settings"),
                [this] { commands_.openSettings("notifications"); }, L"NotificationSettings"}};
    }
    popup_.Show(std::move(content));
}

#ifdef _DEBUG
void Toasts::ShowSample()
{
    Show(Message({.kind = ToastKind::Added, .name = "Sample torrent"}), ToastSwitch::Added);
}
#endif
}
