using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Helpers;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library.Public;

internal sealed class WebsiteDialog : UserControl, IDraft
{
    private readonly Strings _text;
    private readonly Settings _original;
    private readonly Func<Settings, Task> _save;
    private readonly ComboBox _website = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _seconds = new()
    {
        ValidationMode = NumberBoxValidationMode.Disabled,
    };
    private readonly InfoBar _error = new() { IsClosable = false, Severity = InfoBarSeverity.Error };
    private string _input;
    private Exception? _failure;
    private int? Seconds => int.TryParse(_input, NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds) &&
        seconds is >= 0 and <= 3600 ? seconds : null;
    public bool IsPending { get; private set; }
    public bool CanSubmit => !IsPending && _website.SelectedItem is Website && Seconds is not null;
    public bool HasDraft => (_website.SelectedItem as Website) != _original.Website || Seconds != _original.Seconds;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal WebsiteDialog(Strings text, Settings settings, Func<Settings, Task> save)
    {
        _text = text;
        _original = settings;
        _save = save;
        _website.ItemsSource = Provider.Websites;
        _website.DisplayMemberPath = nameof(Website.Name);
        _website.SelectedItem = _original.Website;
        _input = _original.Seconds.ToString(CultureInfo.CurrentCulture);
        _seconds.Text = _input;
        AutomationProperties.SetAutomationId(_website, "VideoWebsite");
        AutomationProperties.SetAutomationId(_seconds, "VideoDelay");
        _website.SelectionChanged += (_, _) => Changed();
        _seconds.Loaded += (_, _) =>
        {
            if (TextEditor.Find(_seconds) is not { } editor)
                return;
            editor.TextChanged -= OnDelayChanged;
            editor.TextChanged += OnDelayChanged;
        };
        Content = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 480, Children = { _website, _seconds, _error } };
        RefreshText();
    }

    internal void RefreshText()
    {
        _website.Header = _text.Get("websites", "website");
        _seconds.Header = _text.Get("websites", "delay");
        ToolTipService.SetToolTip(_seconds, _text.Get("websites", "delay_hint"));
        _error.Message = Seconds is null ? _text.Format("settings", "invalid_range", 0, 3600) :
            _failure is null ? string.Empty : _text.Error(_failure);
        _error.IsOpen = _error.Message.Length > 0;
        AutomationProperties.SetHelpText(_seconds, _error.IsOpen ? _error.Message : _text.Get("websites", "delay_hint"));
    }

    public async Task<bool> Submit()
    {
        if (!CanSubmit || _website.SelectedItem is not Website website || Seconds is not { } seconds)
            return false;
        IsPending = true;
        IsEnabled = false;
        Changed();
        try
        {
            await _save(new(website, seconds));
            return true;
        }
        catch (Exception error)
        {
            _failure = error;
            RefreshText();
            return false;
        }
        finally
        {
            IsPending = false;
            IsEnabled = true;
            Changed();
        }
    }

    private void Changed()
    {
        PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        PropertyChanged?.Invoke(this, new(nameof(HasDraft)));
    }

    private void OnDelayChanged(object sender, TextChangedEventArgs args)
    {
        _input = ((TextBox)sender).Text;
        _failure = null;
        RefreshText();
        Changed();
    }
}
