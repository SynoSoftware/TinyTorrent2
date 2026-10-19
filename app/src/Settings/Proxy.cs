using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Engine;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Shell;

namespace Syno.TinyTorrent.Settings;

// The proxy server setting, and the Edit proxy server dialog's draft of it.
// The dialog saves all five values together, because a proxy works only with
// its type, address and port. A check's result and a failed save are toasts,
// as the supplier dialog's are, so they show above the open dialog.
public sealed class Proxy : IDraft
{
    private readonly Editor _owner;
    private readonly MainViewModel _main;
    private readonly PipeClient _client;
    private Server _saved = new(ProxyType.None, string.Empty, 0, string.Empty, string.Empty);
    private ProxyOutcome? _status;
    private ProxyType _type;
    private string _host = string.Empty;
    private string _port = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;

    // Field errors show once Save or Check has read the fields.
    private bool _validated;

    // Counts checks and edits, so an answer that an edit made out of date is
    // dropped.
    private int _checks;

    // The check that the engine runs for the dialog. The snapshot reports its
    // result, because a check can take longer than a request may wait.
    private (string CheckId, Server Server)? _requested;
    private Strings Text => _owner.Text;
    private string SavedPort =>
        _saved.Port > 0 ? _saved.Port.ToString(CultureInfo.CurrentCulture) : string.Empty;
    public bool IsInUse => _saved.Type != ProxyType.None;

    internal HttpRoute RouteSettings(string adapter) =>
        new()
        {
            Type = _saved.Type,
            Host = _saved.Host,
            Port = _saved.Port,
            Username = _saved.Username,
            Password = _saved.Password,
            Adapter = adapter,
        };

    public string Summary => IsInUse ? Label(_saved) : Text.Get("settings", "off");
    public string Route =>
        Text.Format("window", "proxy", Name(_saved.Type), _saved.Host, _saved.Port);

    // The engine's check of the proxy in use; nothing until it ends.
    public bool IsWorking => IsInUse && _status == ProxyOutcome.Connected;
    public bool IsFailing => IsInUse && _status is { } status && status != ProxyOutcome.Connected;
    public string StatusText =>
        _status is { } status && status != ProxyOutcome.Connected
            ? Reason(status, _saved)
            : Text.Get("settings", "proxy_working");
    public int TypeIndex
    {
        get => (int)_type;
        set
        {
            if ((int)_type == value || value < 0)
                return;
            _type = (ProxyType)value;
            Edit();
        }
    }
    public string Host
    {
        get => _host;
        set
        {
            if (_host == value)
                return;
            _host = value;
            Edit();
        }
    }
    public string Port
    {
        get => _port;
        set
        {
            if (_port == value)
                return;
            _port = value;
            Edit();
        }
    }
    public string Username
    {
        get => _username;
        set
        {
            if (_username == value)
                return;
            _username = value;
            Edit();
        }
    }
    public string Password
    {
        get => _password;
        set
        {
            if (_password == value)
                return;
            _password = value;
            Edit();
        }
    }
    public bool HasDraft =>
        _type != _saved.Type
        || _host != _saved.Host
        || _port != SavedPort
        || _username != _saved.Username
        || _password != _saved.Password;
    public bool IsPending { get; private set; }
    public bool IsChecking { get; private set; }
    public bool CanSubmit => _owner.CanSave && !IsPending;
    public ClosePolicy ClosePolicy =>
        _owner.CanSave ? new ClosePolicy.Save() : new ClosePolicy.Discard();

    // Check stays enabled while it runs, so focus stays on it; Check ignores
    // a second press until the first ends.
    public bool CanCheck => CanSubmit && _type != ProxyType.None;
    public bool CanEditAddress => CanSubmit && _type != ProxyType.None;

    // SOCKS4 has no sign-in.
    public bool CanSignIn => CanSubmit && _type is ProxyType.Socks5 or ProxyType.Http;
    public string? SignInTip =>
        _type == ProxyType.Socks4 ? Text.Get("settings", "socks4_tip") : null;
    public string HostError =>
        _validated
        && _type != ProxyType.None
        && Uri.CheckHostName(_host.Trim()) == UriHostNameType.Unknown
            ? Text.Get("settings", "invalid_host")
            : string.Empty;
    public string PortError =>
        _validated && _type != ProxyType.None && ParsePort() is null
            ? Text.Get("settings", "invalid_port")
            : string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal Proxy(Editor owner, MainViewModel main, PipeClient client)
    {
        _owner = owner;
        _main = main;
        _client = client;
    }

    // Whether the saved proxy or its status changed.
    internal bool Apply(JsonElement settings, JsonElement status, JsonElement check)
    {
        Resolve(check);
        if (!settings.TryGetProperty("proxy_type", out var type))
            return false;
        var saved = new Server(
            Parse(type.GetString()),
            settings.GetProperty("proxy_host").GetString() ?? string.Empty,
            settings.GetProperty("proxy_port").GetInt32(),
            settings.GetProperty("proxy_username").GetString() ?? string.Empty,
            settings.GetProperty("proxy_password").GetString() ?? string.Empty
        );
        var outcome =
            status.ValueKind == JsonValueKind.String ? ParseOutcome(status.GetString()) : null;
        if (saved == _saved && outcome == _status)
            return false;
        (_saved, _status) = (saved, outcome);
        Refresh();
        return true;
    }

    internal void Begin()
    {
        _type = _saved.Type;
        _host = _saved.Host;
        _port = SavedPort;
        _username = _saved.Username;
        _password = _saved.Password;
        _validated = false;
        _requested = null;
        _checks++;
        IsChecking = false;
        Refresh();
    }

    public async Task<bool> Submit()
    {
        if (!CanSubmit)
            return false;
        if (Read() is not { } server)
            return false;
        IsPending = true;
        Refresh();
        try
        {
            await _owner.Save(Values(server));
            _saved = server;
            _status = null;
            _owner.Changed();
            return true;
        }
        catch (Exception error)
        {
            _owner.Report(error);
            return false;
        }
        finally
        {
            IsPending = false;
            Refresh();
        }
    }

    // Begin reads the saved proxy each time the dialog opens, so closing it
    // leaves nothing to drop. A check keeps running and reports its outcome.
    public Task Discard() => Task.CompletedTask;

    public void Stop() { }

    public async Task Check()
    {
        if (!CanCheck || IsChecking)
            return;
        if (Read() is not { } server)
            return;
        var current = ++_checks;
        IsChecking = true;
        Refresh();
        try
        {
            var reply = await _client.Send(
                "check_proxy",
                new { proxy = Values(server) },
                Requester.Person
            );
            if (current == _checks)
                _requested = (reply.GetProperty("check_id").GetString() ?? string.Empty, server);
        }
        catch (Exception error)
        {
            if (current != _checks)
                return;
            IsChecking = false;
            Refresh();
            _main.Notifications.Report(error, "failed_proxy_check", () => Label(server), null);
        }
    }

    internal void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    // The proxy that the fields describe, or nothing when they are not
    // complete. Disabled fields keep their values, so choosing None or SOCKS4
    // and back does not lose them.
    private Server? Read()
    {
        _validated = true;
        if (HostError.Length > 0 || PortError.Length > 0)
        {
            Refresh();
            return null;
        }
        return new(_type, _host.Trim(), ParsePort() ?? 0, _username, _password);
    }

    // The settings command's names for the proxy.
    private static Dictionary<string, object> Values(Server server) =>
        new()
        {
            ["proxy_type"] = Word(server.Type),
            ["proxy_host"] = server.Host,
            ["proxy_port"] = server.Port,
            ["proxy_username"] = server.Username,
            ["proxy_password"] = server.Password,
        };

    private int? ParsePort() =>
        Editor.TryInteger(_port, out var port) && port is >= 1 and <= 65535 ? port : null;

    // Ends the dialog's check when the snapshot reports its result. A snapshot
    // without the check means that the engine could not run it or restarted.
    private void Resolve(JsonElement check)
    {
        if (_requested is not { } requested)
            return;
        var reported =
            check.ValueKind == JsonValueKind.Object
            && check.GetProperty("check_id").GetString() == requested.CheckId;
        var outcome = reported ? check.GetProperty("outcome") : default;
        if (reported && outcome.ValueKind == JsonValueKind.Null)
            return;
        _requested = null;
        IsChecking = false;
        Refresh();
        var server = requested.Server;
        if (!reported)
        {
            _main.Notifications.Report(
                new CommandException("unknown", null, Text),
                "failed_proxy_check",
                () => Label(server),
                null
            );
            return;
        }
        var result = ParseOutcome(outcome.GetString()) ?? ProxyOutcome.WrongType;
        if (result == ProxyOutcome.Connected)
        {
            _main.Notifications.ShowOutcome("settings", "proxy_connected", Label(server));
            return;
        }
        // The engine reports an outcome rather than an error, so the outcome's
        // reason is the toast's text.
        _main.Notifications.Report(
            new CommandException(Word(result), null, Text, "check_proxy"),
            "failed_proxy_check",
            () => Label(server),
            () => Reason(result, server)
        );
    }

    private void Edit()
    {
        _requested = null;
        _checks++;
        IsChecking = false;
        Refresh();
    }

    private string Label(Server server) =>
        Text.Format("settings", "proxy_summary", Name(server.Type), server.Host, server.Port);

    // Why a check of server failed.
    private string Reason(ProxyOutcome outcome, Server server) =>
        outcome switch
        {
            ProxyOutcome.SignInFailed => Text.Get("settings", "proxy_sign_in_failed_tip"),
            ProxyOutcome.Unreachable => Text.Format(
                "settings",
                "proxy_unreachable_tip",
                server.Host,
                server.Port
            ),
            ProxyOutcome.NotFound => Text.Format("settings", "proxy_not_found_tip", server.Host),
            ProxyOutcome.WrongType => Text.Format(
                "settings",
                "proxy_wrong_type_tip",
                server.Host,
                server.Port,
                Name(server.Type)
            ),
            _ => Text.Get("settings", "proxy_timed_out_tip"),
        };

    // Protocol names, which are not translated.
    private static string Name(ProxyType type) =>
        type switch
        {
            ProxyType.Socks5 => "SOCKS5",
            ProxyType.Socks4 => "SOCKS4",
            ProxyType.Http => "HTTP",
            _ => string.Empty,
        };

    private static string Word(ProxyType type) =>
        type switch
        {
            ProxyType.Socks5 => "socks5",
            ProxyType.Socks4 => "socks4",
            ProxyType.Http => "http",
            _ => "none",
        };

    private static ProxyType Parse(string? word) =>
        word switch
        {
            "socks5" => ProxyType.Socks5,
            "socks4" => ProxyType.Socks4,
            "http" => ProxyType.Http,
            _ => ProxyType.None,
        };

    private static string Word(ProxyOutcome outcome) =>
        outcome switch
        {
            ProxyOutcome.Connected => "connected",
            ProxyOutcome.SignInFailed => "sign_in_failed",
            ProxyOutcome.Unreachable => "unreachable",
            ProxyOutcome.NotFound => "not_found",
            ProxyOutcome.WrongType => "wrong_type",
            _ => "timed_out",
        };

    private static ProxyOutcome? ParseOutcome(string? word) =>
        word switch
        {
            "connected" => ProxyOutcome.Connected,
            "sign_in_failed" => ProxyOutcome.SignInFailed,
            "unreachable" => ProxyOutcome.Unreachable,
            "not_found" => ProxyOutcome.NotFound,
            "wrong_type" => ProxyOutcome.WrongType,
            "timed_out" => ProxyOutcome.TimedOut,
            _ => null,
        };

    private sealed record Server(
        ProxyType Type,
        string Host,
        int Port,
        string Username,
        string Password
    );
}
