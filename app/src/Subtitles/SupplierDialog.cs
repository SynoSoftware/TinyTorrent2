using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Subtitles;

internal sealed class SupplierDialog : UserControl, IDraft
{
    private readonly Acquisition _model;
    private readonly TextBox _username;
    private readonly PasswordBox _password = new();
    private readonly FieldHeader _usernameHeader = new();
    private readonly FieldHeader _passwordHeader = new();
    private readonly InfoBar _saveError = new()
    {
        IsClosable = false,
        Severity = InfoBarSeverity.Error,
    };
    private readonly CheckStatus _status = new() { Width = 280 };
    private readonly ActionButton _check;
    private readonly Draft _draft;

    // Reports a check's result to the window's notifications: null when
    // access was confirmed, otherwise the failure.
    private readonly Action<SubtitleSupplier, Exception?> _reportCheck;
    private CancellationTokenSource? _checking;
    private Exception? _saveFailure;
    private bool _invalidAccount;
    internal FrameworkElement Status => _status;
    internal ActionButton CheckAction => _check;
    internal SubtitleSupplier SupplierId => _draft.SupplierId;
    public bool IsPending { get; private set; }
    public bool CanSubmit => !IsPending;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool HasDraft =>
        _draft.SupplierId != _model.SupplierId
        || _username.Text != _model.Username
        || _password.Password.Length > 0
        || !_draft.KeepSecret && _model.HasSecret;
    private Strings Text { get; }

    private string T(string key) => Text.Get("subtitles", key);

    // Edits the account of supplierId; Save makes it the saved supplier, so a
    // supplier and its account always take effect together.
    internal SupplierDialog(
        Strings text,
        Acquisition model,
        SubtitleSupplier supplierId,
        Action<SubtitleSupplier, Exception?> reportCheck
    )
    {
        Text = text;
        _check = _status.Check;
        _model = model;
        _reportCheck = reportCheck;
        _draft = model.Edit(supplierId);
        _username = new TextBox { Text = _draft.Username, Header = _usernameHeader };
        _password.Header = _passwordHeader;
        AutomationProperties.SetAutomationId(_username, "SupplierUsername");
        AutomationProperties.SetAutomationId(_password, "SupplierSecret");
        AutomationProperties.SetAutomationId(_check, "SupplierCheck");
        var body = new StackPanel { Spacing = 12, MinWidth = 320 };
        body.Children.Add(_username);
        body.Children.Add(_password);
        body.Children.Add(_saveError);
        Content = body;
        _username.TextChanged += (_, _) => Changed();
        _password.PasswordChanged += (_, _) => Changed();
        _check.Click += async (_, _) => await Check();
        _password.Loaded += (_, _) => RefreshText();
        RefreshText();
    }

    internal void RefreshText()
    {
        var password = _model.GetSupplier(_draft.SupplierId)?.UsesPassword == true;
        _usernameHeader.Label = T("username");
        _passwordHeader.Label = T(password ? "password" : "api_key");
        _usernameHeader.Error = _passwordHeader.Error = _invalidAccount
            ? T("failure_unconfigured")
            : string.Empty;
        AutomationProperties.SetName(_username, _usernameHeader.Label);
        AutomationProperties.SetName(_password, _passwordHeader.Label);
        _saveError.Message = _saveFailure is { } saveFailure
            ? Text.Error(saveFailure)
            : string.Empty;
        _saveError.IsOpen = _saveFailure is not null;
        _saveError.Visibility = _saveFailure is null ? Visibility.Collapsed : Visibility.Visible;
        _password.PlaceholderText = _draft.KeepSecret
            ? T(password ? "saved_password" : "saved_key")
            : string.Empty;
        _username.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        var help = password
            ? T("account_hint")
            : Text.Format("subtitles", "key_hint", _draft.SupplierId);
        ToolTipService.SetToolTip(_username, help);
        AutomationProperties.SetHelpText(_username, _invalidAccount ? _usernameHeader.Error : help);
        ToolTipService.SetToolTip(_password, help);
        AutomationProperties.SetHelpText(_password, _invalidAccount ? _passwordHeader.Error : help);
        TextEditor.RevealTip(
            _password,
            Text.Get("dialog", password ? "reveal_password" : "reveal_key")
        );
        _check.Text = Text.Get("settings", "check");
        ToolTipService.SetToolTip(_check, T("check_hint"));
        var checking = _checking is { IsCancellationRequested: false };
        ReadDraft();
        _check.IsEnabled = !IsPending && _model.CanCheckDraft(_draft);
        var feedback = checking ? Text.Get("settings", "proxy_checking") : string.Empty;
        _status.Refresh(checking, null, feedback, feedback);
    }

    private void ReadDraft()
    {
        _draft.Username = _username.Text;
        _draft.Secret = _password.Password;
    }

    private void Changed()
    {
        CancelCheck();
        _model.EditChanged(_draft);
        _saveFailure = null;
        _invalidAccount = false;
        RefreshText();
    }

    private async Task Check()
    {
        if (!_check.IsEnabled || _checking is { IsCancellationRequested: false })
            return;
        ReadDraft();
        var draft = _draft;
        using var cancellation = new CancellationTokenSource();
        _checking = cancellation;
        _saveFailure = null;
        _invalidAccount = false;
        RefreshText();
        try
        {
            await _model.Check(draft, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                _reportCheck(draft.SupplierId, null);
        }
        catch (OperationCanceledException) { }
        // An incomplete account is a field error, shown beside the fields.
        catch (SubtitleException error) when (error.Reason == SubtitleFailure.Unconfigured)
        {
            _invalidAccount = true;
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
                _reportCheck(draft.SupplierId, error);
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
        ReadDraft();
        IsEnabled = _check.IsEnabled = false;
        IsPending = true;
        PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        _saveFailure = null;
        _invalidAccount = false;
        try
        {
            await _model.Save(_draft);
            return true;
        }
        catch (SubtitleException error) when (error.Reason == SubtitleFailure.Unconfigured)
        {
            _invalidAccount = true;
        }
        catch (Exception error)
        {
            _saveFailure = error;
        }
        finally
        {
            IsEnabled = _check.IsEnabled = true;
            IsPending = false;
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        }
        RefreshText();
        return false;
    }

    internal void CancelCheck() => _checking?.Cancel();

    internal void Clear()
    {
        CancelCheck();
        _model.EditChanged(_draft);
        _draft.Secret = _password.Password = string.Empty;
    }

    internal static Flyout Information(Strings text, Supplier supplier) =>
        Disclosure.Create(
            text,
            supplier.SupplierId.ToString(),
            text.Format("subtitles", "disclosure", supplier.SupplierId),
            supplier.Privacy,
            supplier.Terms
        );
}
