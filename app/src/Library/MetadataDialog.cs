using System.ComponentModel;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Syno.TinyTorrent.Views;

namespace Syno.TinyTorrent.Library;

internal sealed class MetadataDialog : UserControl, IDraft
{
    private readonly Browser _library;
    private readonly Entry _entry;
    private readonly AutoSuggestBox _input;
    private readonly ListView _results = new() { DisplayMemberPath = "Label", MaxHeight = 300, SelectionMode = ListViewSelectionMode.Single };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private CancellationTokenSource? _request;
    private Exception? _failure;
    public bool IsPending { get; private set; }
    public bool CanSubmit => !IsPending && HasDraft;
    public bool HasDraft => _results.SelectedItem is VideoChoice;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal MetadataDialog(Browser library, Entry entry)
    {
        _library = library;
        _entry = entry;
        _input = new AutoSuggestBox { Text = FileName.Interpret(entry.Name).Title, QueryIcon = new SymbolIcon(Symbol.Find) };
        AutomationProperties.SetAutomationId(_input, "MetadataSearch");
        AutomationProperties.SetAutomationId(_results, "MetadataMatches");
        Content = new StackPanel { Spacing = 12, MinWidth = 380, Children = { _input, _results, _error } };
        _input.QuerySubmitted += async (_, _) => await Find();
        _input.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            Cancel();
            _results.ItemsSource = null;
        };
        _results.SelectionChanged += (_, _) => PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
    }

    internal void RefreshText()
    {
        _input.PlaceholderText = _library.Text.Get("library", "search_title");
        AutomationProperties.SetName(_input, _input.PlaceholderText);
        AutomationProperties.SetName(_results, _library.Text.Get("library", "matches"));
        _error.Message = _failure is null ? string.Empty : _library.Error(_failure);
        _error.IsOpen = _failure is not null;
    }

    internal async Task Find()
    {
        Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        _results.ItemsSource = null;
        try
        {
            var choices = await _library.FindVideo(_input.Text, request.Token);
            if (!request.IsCancellationRequested)
            {
                _results.ItemsSource = choices;
                Report(null);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
                Report(error);
        }
        finally
        {
            if (ReferenceEquals(_request, request))
                _request = null;
        }
    }

    public async Task<bool> Submit()
    {
        if (_results.SelectedItem is not VideoChoice choice || IsPending)
            return false;
        Cancel();
        using var request = new CancellationTokenSource();
        _request = request;
        IsPending = true;
        _input.IsEnabled = _results.IsEnabled = false;
        PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        try
        {
            if (await _library.SaveMetadata(_entry, choice, request.Token))
                return true;
            Report(new VideoException("library", "no_video_match"));
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested)
                Report(error);
            return false;
        }
        finally
        {
            IsPending = false;
            _input.IsEnabled = _results.IsEnabled = true;
            if (ReferenceEquals(_request, request))
                _request = null;
            PropertyChanged?.Invoke(this, new(nameof(CanSubmit)));
        }
    }

    private void Report(Exception? error)
    {
        _failure = error;
        RefreshText();
    }

    internal void Cancel() => _request?.Cancel();
}
