using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Services;

internal sealed class BackgroundWork : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly List<Operation> _active = [];
    private readonly ObservableCollection<Operation> _items = [];
    private readonly Timer _timer;
    private bool _queued;
    private bool _scheduled;
    private bool _disposed;

    internal ReadOnlyObservableCollection<Operation> Items { get; }

    internal BackgroundWork(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Items = new(_items);
        _timer = new(_ =>
        {
            lock (_gate)
            {
                _scheduled = false;
                Queue();
            }
        });
    }

    internal Operation Begin(WorkObservation initial, Func<Task>? cancel = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var operation = new Operation(this, initial, cancel);
            _active.Add(operation);
            Queue();
            return operation;
        }
    }

    private void Queue()
    {
        if (_disposed || _queued)
            return;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _scheduled = false;
        _queued = _dispatcher.TryEnqueue(Flush);
    }

    private void Flush()
    {
        Operation[] active;
        lock (_gate)
        {
            _queued = false;
            if (_disposed)
                return;
            active = [.. _active];
        }
        foreach (var removed in _items.Except(active).ToArray())
            _items.Remove(removed);
        foreach (var operation in active)
        {
            WorkObservation latest;
            lock (_gate)
            {
                if (operation._ended)
                    continue;
                latest = operation._latest;
            }
            operation.Publish(latest);
            if (!_items.Contains(operation))
                _items.Add(operation);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
            _active.Clear();
        }
        _items.Clear();
    }

    internal sealed class Operation : INotifyPropertyChanged, ICommand, IDisposable
    {
        private readonly BackgroundWork _owner;
        private readonly Func<Task>? _cancel;
        internal WorkObservation _latest;
        internal bool _ended;
        private bool _requesting;

        internal Operation(BackgroundWork owner, WorkObservation initial, Func<Task>? cancel)
        {
            _owner = owner;
            _latest = Current = initial;
            _cancel = cancel;
        }

        internal WorkObservation Current { get; private set; }
        internal ICommand Cancel => this;
        internal bool HasCancel => _cancel is not null;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? CanExecuteChanged;

        internal void Report(WorkObservation observation)
        {
            lock (_owner._gate)
            {
                if (_ended || _owner._disposed || observation == _latest)
                    return;
                var immediate = observation.Description != _latest.Description
                    || observation.Execution != _latest.Execution;
                _latest = observation;
                if (immediate)
                    _owner.Queue();
                else if (!_owner._queued && !_owner._scheduled)
                {
                    _owner._scheduled = true;
                    _owner._timer.Change(100, Timeout.Infinite);
                }
            }
        }

        internal void Publish(WorkObservation observation)
        {
            if (Current != observation)
            {
                Current = observation;
                PropertyChanged?.Invoke(this, new(nameof(Current)));
            }
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool CanExecute(object? parameter)
        {
            lock (_owner._gate)
                return !_owner._disposed && !_ended && !_requesting && _cancel is not null
                    && _latest.Execution.CanCancel;
        }

        public async void Execute(object? parameter)
        {
            lock (_owner._gate)
            {
                if (!CanExecute(parameter))
                    return;
                _requesting = true;
            }
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await _cancel!();
            }
            finally
            {
                lock (_owner._gate)
                {
                    _requesting = false;
                    _owner.Queue();
                }
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_ended)
                    return;
                _ended = true;
                _owner._active.Remove(this);
                _owner.Queue();
            }
        }
    }
}
