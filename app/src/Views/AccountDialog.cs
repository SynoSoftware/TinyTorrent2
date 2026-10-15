using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Views;

/// <summary>The values in an <see cref="AccountDialog"/>.</summary>
/// <param name="Secret">The typed password or API key, or empty when none was typed.</param>
/// <param name="KeepSecret">
/// Whether an empty <paramref name="Secret"/> keeps the saved one. With
/// neither, there is no password, or the provider uses its built-in key.
/// </param>
internal sealed record AccountInput(string Username, string Secret, bool KeepSecret);

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
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="title">The provider or supplier name, which the key hint also names.</param>
    /// <param name="saved">The saved values; its secret is always empty.</param>
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
        _own.IsChecked = saved.KeepSecret;
        _username.Text = saved.Username;
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

    private AccountInput Input =>
        UsesPassword ? new(_username.Text.Trim(), _secret.Password, _saved.KeepSecret)
        : Own ? new(string.Empty, _secret.Password.Trim(), _saved.KeepSecret)
        : new(string.Empty, string.Empty, false);

    // A user name needs a password and a password needs a user name; with
    // neither, the supplier is used without signing in. The person's own key
    // needs a typed or saved key.
    private bool IsComplete
    {
        get
        {
            var input = Input;
            if (UsesPassword)
                return input.Username.Length > 0
                    ? input.Secret.Length > 0 || input.KeepSecret
                    : input.Secret.Length == 0;
            return !Own || input.Secret.Length > 0 || input.KeepSecret;
        }
    }

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
        _secret.PlaceholderText = _saved.KeepSecret && Own ? T(placeholder) : string.Empty;
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
        CancelCheck();
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
        )
            return;
        using var cancellation = new CancellationTokenSource();
        _checking = cancellation;
        _saveFailure = null;
        RefreshText();
        try
        {
            await _check(Input, cancellation.Token);
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
        CancelCheck();
        if (!IsComplete)
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
            await _save(Input);
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

    internal void CancelCheck() => _checking?.Cancel();

    // Discards the typed secret, which ResolveDraft does when the window closes.
    internal void Clear()
    {
        CancelCheck();
        _own.IsChecked = _saved.KeepSecret;
        _secret.Password = string.Empty;
    }
}
