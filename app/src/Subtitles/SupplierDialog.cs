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
    private readonly ComboBox _supplier;
    private readonly TextBox _username;
    private readonly PasswordBox _password = new();
    private readonly FieldHeader _usernameHeader = new();
    private readonly FieldHeader _passwordHeader = new();
    private readonly InfoBar _saveError = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private readonly Button _information = new() { Content = new FontIcon { FontFamily = Lucide.Font, Glyph = Lucide.Info },
        Style = (Style)Application.Current.Resources["TinyTorrentSubtleButtonStyle"] };
    private readonly TextBlock _heading = new();
    private readonly CheckStatus _status = new();
    private readonly ActionButton _check;
    private readonly Draft _draft;
    private CancellationTokenSource? _checking;
    private Exception? _failure;
    private Exception? _saveFailure;
    private bool _invalidAccount;
    private bool _checked;
    internal FrameworkElement Status => _status;
    internal ActionButton CheckAction => _check;
    public bool IsPending { get; private set; }
    public bool CanSubmit => !IsPending;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool HasDraft => _draft.SupplierId != _model.SupplierId || _username.Text != _model.Username ||
        _password.Password.Length > 0 || !_draft.KeepSecret && _model.HasSecret;
    private Strings Text { get; }
    private string T(string key) => Text.Get("subtitles", key);

    internal SupplierDialog(Strings text, Acquisition model)
    {
        Text = text;
        _check = _status.Check;
        _model = model;
        _draft = model.Edit();
        _supplier = new ComboBox
        {
            ItemsSource = model.Suppliers.Select(supplier => supplier.SupplierId).ToArray(), SelectedItem = _draft.SupplierId,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _username = new TextBox { Text = _draft.Username, Header = _usernameHeader };
        _password.Header = _passwordHeader;
        AutomationProperties.SetAutomationId(_supplier, "SupplierChoice");
        AutomationProperties.SetAutomationId(_username, "SupplierUsername");
        AutomationProperties.SetAutomationId(_password, "SupplierSecret");
        AutomationProperties.SetAutomationId(_information, "SupplierInformation");
        AutomationProperties.SetAutomationId(_check, "SupplierCheck");
        var body = new StackPanel { Spacing = 12, MinWidth = 320 };
        body.Children.Add(Settings.Pair(_heading, _information));
        body.Children.Add(_supplier);
        body.Children.Add(_username);
        body.Children.Add(_password);
        body.Children.Add(_saveError);
        Content = body;
        _supplier.SelectionChanged += (_, _) =>
        {
            if (_supplier.SelectedItem is not SubtitleSupplier selected || selected == _draft.SupplierId)
                return;
            _draft.SupplierId = selected;
            _draft.KeepSecret = false;
            _username.Text = _password.Password = string.Empty;
            Changed();
        };
        _username.TextChanged += (_, _) => Changed();
        _password.PasswordChanged += (_, _) => Changed();
        _information.Click += (_, _) =>
        {
            if (_model.GetSupplier(_draft.SupplierId) is { } supplier)
                Information(Text, supplier).ShowAt(_information);
        };
        _check.Click += async (_, _) => await Check();
        _password.Loaded += (_, _) => RefreshText();
        RefreshText();
    }

    internal void RefreshText()
    {
        var password = _model.GetSupplier(_draft.SupplierId)?.UsesPassword == true;
        _heading.Text = T("supplier");
        _usernameHeader.Label = T("username");
        _passwordHeader.Label = T(password ? "password" : "api_key");
        _usernameHeader.Error = _passwordHeader.Error = _invalidAccount ? T("failure_unconfigured") : string.Empty;
        AutomationProperties.SetName(_username, _usernameHeader.Label);
        AutomationProperties.SetName(_password, _passwordHeader.Label);
        _saveError.Message = _saveFailure is { } saveFailure ? Text.Error(saveFailure) : string.Empty;
        _saveError.IsOpen = _saveFailure is not null;
        _saveError.Visibility = _saveFailure is null ? Visibility.Collapsed : Visibility.Visible;
        _password.PlaceholderText = _draft.KeepSecret ? T(password ? "saved_password" : "saved_key") : string.Empty;
        _username.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        var help = password ? T("account_hint") :
            Text.Format("subtitles", "key_hint", _draft.SupplierId);
        ToolTipService.SetToolTip(_supplier, T("supplier_hint"));
        AutomationProperties.SetHelpText(_supplier, T("supplier_hint"));
        ToolTipService.SetToolTip(_username, help);
        AutomationProperties.SetHelpText(_username, _invalidAccount ? _usernameHeader.Error : help);
        ToolTipService.SetToolTip(_password, help);
        AutomationProperties.SetHelpText(_password, _invalidAccount ? _passwordHeader.Error : help);
        TextEditor.RevealTip(_password, Text.Get("dialog", password ? "reveal_password" : "reveal_key"));
        _check.Text = Text.Get("settings", "check");
        ToolTipService.SetToolTip(_check, T("check_hint"));
        AutomationProperties.SetName(_information, T("supplier_information"));
        ToolTipService.SetToolTip(_information, T("supplier_information"));
        AutomationProperties.SetName(_supplier, T("supplier"));
        var checking = _checking is { IsCancellationRequested: false };
        var feedback = checking ? Text.Get("settings", "proxy_checking") : _failure is { } failure
            ? Text.Error(failure) : _checked ? T("connected") : string.Empty;
        bool? succeeded = _failure is not null ? false : _checked ? true : null;
        _status.Refresh(checking, succeeded, feedback, feedback);
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
        _failure = null;
        _saveFailure = null;
        _invalidAccount = false;
        _checked = false;
        RefreshText();
    }

    private async Task Check()
    {
        ReadDraft();
        var draft = _draft;
        using var cancellation = new CancellationTokenSource();
        _checking = cancellation;
        _check.IsEnabled = false;
        _failure = null;
        _saveFailure = null;
        _invalidAccount = false;
        _checked = false;
        RefreshText();
        try
        {
            await _model.Check(draft, cancellation.Token);
            _checked = !cancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) _failure = error; }
        finally
        {
            _checking = null;
            _check.IsEnabled = !IsPending;
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
        catch (SubtitleException error) when (error.Reason == SubtitleFailure.Unconfigured) { _invalidAccount = true; }
        catch (Exception error) { _saveFailure = error; }
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

    internal static Flyout Information(Strings text, Supplier supplier)
    {
        string T(string key) => text.Get("subtitles", key);
        var panel = new StackPanel { Spacing = 12, MaxWidth = 400 };
        panel.Children.Add(new TextBlock { Text = supplier.SupplierId.ToString(), Style = (Style)Application.Current.Resources["TinyTorrentGroupTitleTextStyle"] });
        panel.Children.Add(new TextBlock { Text = text.Format("subtitles", "disclosure", supplier.SupplierId), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new HyperlinkButton { Content = T("privacy"), NavigateUri = supplier.Privacy });
        panel.Children.Add(new HyperlinkButton { Content = T("terms"), NavigateUri = supplier.Terms });
        return new Flyout { Content = panel };
    }
}
