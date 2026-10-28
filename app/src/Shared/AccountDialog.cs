using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Shared;

/// <summary>A typed password or API key, or the saved one, which the dialog never shows.</summary>
internal abstract record Secret
{
    private Secret() { }

    internal sealed record Kept : Secret;

    internal sealed record Typed(string Value) : Secret;
}

/// <summary>
/// The values in an <see cref="AccountDialog"/>: the built-in key or no
/// sign-in, the person's own API key, or a user name and password.
/// </summary>
internal abstract record AccountInput
{
    private AccountInput() { }

    internal sealed record BuiltIn : AccountInput;

    internal sealed record OwnKey(Secret Key) : AccountInput;

    /// <summary>
    /// A user name and its password. A kept password belongs to the saved
    /// user name only. The password is null when none is typed or saved, as
    /// after a saved password could not be read.
    /// </summary>
    internal sealed record SignedIn(string Username, Secret? Password) : AccountInput;
}

/// <summary>
/// Asks for what a Library provider or subtitle supplier declares in its
/// <see cref="Access"/>: an API key, or a user name and password. The opener
/// saves and checks the values, so every plugin shares this one dialog.
/// </summary>
internal sealed class AccountDialog : UserControl, IDraft
{
    private readonly Access _access;

    // A key built into the release lets the person choose between it and
    // their own key; without one, their own key is required.
    private readonly bool _builtIn;
    private readonly AccountInput _saved;
    private readonly Func<AccountInput, Task> _save;
    private readonly Func<AccountInput, CancellationToken, Task>? _check;
    private readonly CheckBox _own = new();
    private readonly TextBox _username = new();
    private readonly PasswordBox _secret = new();
    private readonly FieldHeader _usernameHeader = new();
    private readonly FieldHeader _secretHeader = new();
    private readonly InfoBar _saveError = new()
    {
        IsClosable = false,
        Severity = InfoBarSeverity.Error,
    };
    private readonly CheckStatus _status = new() { Width = 280 };
    private CancellationTokenSource? _checking;
    private Exception? _saveFailure;
    private bool _incomplete;
    private Strings Text { get; }
    internal string Title { get; }

    /// <summary>The check button and its progress, or null when the opener cannot check.</summary>
    internal CheckStatus? Status => _check is null ? null : _status;
    public bool IsPending { get; private set; }
    public bool CanSubmit => !IsPending;
    public bool HasDraft => Input != _saved;

    // Account details are stored only by the person's own Save.
    public ClosePolicy ClosePolicy => new ClosePolicy.Discard();
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="title">The provider or supplier name, which the key hint also names.</param>
    /// <param name="saved">The saved values. A saved password or key is <see cref="Secret.Kept"/>.</param>
    /// <param name="check">
    /// Checks access without saving and reports the result itself, or null
    /// when the opener has no check.
    /// </param>
    internal AccountDialog(
        Strings text,
        string title,
        Access access,
        bool builtIn,
        AccountInput saved,
        Func<AccountInput, Task> save,
        Func<AccountInput, CancellationToken, Task>? check = null
    )
    {
        Text = text;
        Title = title;
        _access = access;
        _builtIn = builtIn;
        _saved = saved;
        _save = save;
        _check = check;
        _own.IsChecked = saved is AccountInput.OwnKey;
        _username.Text = saved is AccountInput.SignedIn signedIn ? signedIn.Username : string.Empty;
        _username.Header = _usernameHeader;
        _secret.Header = _secretHeader;
        AutomationProperties.SetAutomationId(_own, "AccountOwnKey");
        AutomationProperties.SetAutomationId(_username, "AccountUsername");
        AutomationProperties.SetAutomationId(_secret, "AccountSecret");
        AutomationProperties.SetAutomationId(_status.Check, "AccountCheck");
        Content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 320,
            Children = { _own, _username, _secret, _saveError },
        };
        _own.Checked += (_, _) => ChooseKey();
        _own.Unchecked += (_, _) => ChooseKey();
        _username.TextChanged += (_, _) => Changed();
        _secret.PasswordChanged += (_, _) => Changed();
        _status.Check.Click += async (_, _) => await Check();
        _secret.Loaded += (_, _) => RefreshText();
        RefreshText();
    }

    private bool UsesPassword => _access == Access.Password;

    // Whether the secret field holds the person's own password or key, rather
    // than leaving the built-in key in use.
    private bool Own => UsesPassword || !_builtIn || _own.IsChecked == true;

    // Whether an empty secret field keeps the saved password or key. A saved
    // password belongs to the saved user name, so another one needs a typed
    // password.
    private bool CanKeep =>
        _saved switch
        {
            AccountInput.OwnKey { Key: Secret.Kept } => true,
            AccountInput.SignedIn { Password: Secret.Kept } signedIn =>
                signedIn.Username == _username.Text.Trim(),
            _ => false,
        };

    // The values, or null while they are incomplete. A user name needs a
    // password and a password needs a user name; with neither, the supplier
    // is used without signing in. The person's own key needs a typed or saved
    // key.
    private AccountInput? Input
    {
        get
        {
            var typed = UsesPassword ? _secret.Password : _secret.Password.Trim();
            Secret? secret =
                typed.Length > 0 ? new Secret.Typed(typed)
                : CanKeep ? new Secret.Kept()
                : null;
            if (!UsesPassword)
                return !Own ? new AccountInput.BuiltIn()
                    : secret is null ? null
                    : new AccountInput.OwnKey(secret);
            var username = _username.Text.Trim();
            if (username.Length == 0)
                return typed.Length == 0 ? new AccountInput.BuiltIn() : null;
            return secret is null ? null : new AccountInput.SignedIn(username, secret);
        }
    }

    private bool IsComplete => Input is not null;

    internal void RefreshText()
    {
        var placeholder = UsesPassword ? "saved_password" : "saved_key";
        var help = UsesPassword
            ? T("account_hint")
            : Text.Format("account", _builtIn ? "shared_key_hint" : "key_hint", Title);
        _own.Content = T("own_key");
        _own.Visibility = !UsesPassword && _builtIn ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_own, help);
        _username.Visibility = UsesPassword ? Visibility.Visible : Visibility.Collapsed;
        _usernameHeader.Label = T("username");
        _secretHeader.Label = T(UsesPassword ? "password" : "api_key");
        _secretHeader.Error = _incomplete
            ? T(UsesPassword ? "account_hint" : "key_missing")
            : string.Empty;
        _usernameHeader.Error = UsesPassword ? _secretHeader.Error : string.Empty;
        _secret.IsEnabled = Own;
        _secret.PlaceholderText = CanKeep && Own ? T(placeholder) : string.Empty;
        TextEditor.RevealTip(
            _secret,
            Text.Get("dialog", UsesPassword ? "reveal_password" : "reveal_key")
        );
        Describe(_username, _usernameHeader);
        Describe(_secret, _secretHeader);
        _saveError.Message = _saveFailure is { } failure ? Text.Error(failure) : string.Empty;
        _saveError.IsOpen = _saveFailure is not null;
        _saveError.Visibility = _saveFailure is null ? Visibility.Collapsed : Visibility.Visible;
        _status.Check.Text = Text.Get("settings", "check");
        ToolTipService.SetToolTip(_status.Check, T("check_hint"));
        var checking = _checking is { IsCancellationRequested: false };
        _status.Check.IsEnabled = !IsPending && IsComplete;
        var feedback = checking ? Text.Get("settings", "proxy_checking") : string.Empty;
        _status.Refresh(checking, null, feedback, feedback);

        void Describe(Control field, FieldHeader header)
        {
            AutomationProperties.SetName(field, header.Label);
            ToolTipService.SetToolTip(field, help);
            AutomationProperties.SetHelpText(field, _incomplete ? header.Error : help);
        }
    }

    private string T(string key) => Text.Get("account", key);

    // Choosing the built-in key discards a typed key, so it is never saved.
    private void ChooseKey()
    {
        if (_own.IsChecked != true)
            _secret.Password = string.Empty;
        Changed();
    }

    private void Changed()
    {
        Stop();
        _saveFailure = null;
        _incomplete = false;
        RefreshText();
    }

    private async Task Check()
    {
        if (
            _check is null
            || !_status.Check.IsEnabled
            || _checking is { IsCancellationRequested: false }
            || Input is not { } input
        )
            return;
        using var cancellation = new CancellationTokenSource();
        _checking = cancellation;
        _saveFailure = null;
        RefreshText();
        try
        {
            await _check(input, cancellation.Token);
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine($"Account check cancelled ({Title}).");
        }
        finally
        {
            if (_checking == cancellation)
                _checking = null;
            RefreshText();
        }
    }

    public async Task<bool> Submit()
    {
        if (IsPending)
            return false;
        Stop();
        if (Input is not { } input)
        {
            _incomplete = true;
            RefreshText();
            return false;
        }
        // The check button sits in the dialog's footer, outside this control,
        // so RefreshText disables it separately.
        IsEnabled = false;
        IsPending = true;
        _saveFailure = null;
        PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        RefreshText();
        try
        {
            await _save(input);
            return true;
        }
        catch (Exception error)
        {
            _saveFailure = error;
            return false;
        }
        finally
        {
            IsEnabled = true;
            IsPending = false;
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
            RefreshText();
        }
    }

    public void Stop() => _checking?.Cancel();

    // Drops the typed secret.
    public Task Discard()
    {
        Stop();
        _own.IsChecked = _saved is AccountInput.OwnKey;
        _secret.Password = string.Empty;
        return Task.CompletedTask;
    }
}
