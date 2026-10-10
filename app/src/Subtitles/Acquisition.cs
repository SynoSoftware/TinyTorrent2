using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using SupplierType = Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition(Database database, ProviderHttp http) : IAsyncDisposable
{
    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS subtitle_settings (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1), enabled INTEGER NOT NULL DEFAULT 0,
            supplier INTEGER NOT NULL DEFAULT 0, username TEXT NOT NULL DEFAULT '', secret BLOB,
            languages TEXT NOT NULL DEFAULT '[]', checked_at TEXT, request_reset INTEGER, download_reset INTEGER
        );
        INSERT OR IGNORE INTO subtitle_settings(singleton) VALUES(1);
        CREATE TABLE IF NOT EXISTS subtitle_outputs (
            path TEXT COLLATE PATH NOT NULL, language TEXT NOT NULL, created INTEGER NOT NULL,
            PRIMARY KEY(path,language)
        );
        CREATE TABLE IF NOT EXISTS subtitle_jobs (
            path TEXT COLLATE PATH NOT NULL, language TEXT NOT NULL, stage INTEGER NOT NULL,
            outcome INTEGER NOT NULL, retry_at INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(path,language)
        );
        CREATE TABLE IF NOT EXISTS subtitle_sources (
            path TEXT COLLATE PATH NOT NULL, language TEXT NOT NULL,
            origin TEXT NOT NULL, file_index INTEGER NOT NULL,
            PRIMARY KEY(path,language,origin,file_index)
        );
        CREATE TABLE IF NOT EXISTS subtitle_failures (
            path TEXT COLLATE PATH PRIMARY KEY, reason INTEGER NOT NULL
        );
        """;

    private readonly SemaphoreSlim _operation = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _requestSync = new();
    private CancellationTokenSource _requests = new();
    private Task? _worker;
    private string _language = "en";
    private long _revision;
    private bool _connected;
    private bool _disposed;
    private Account _account = new(SubtitleSupplier.OpenSubtitles, string.Empty, string.Empty);
    private Allowance _allowance = new();
    private Draft? _checkedDraft;
    private Account? _checkedAccount;
    internal IReadOnlyList<Supplier> Suppliers { get; } = SupplierType.CreateAll();
    internal Supplier? Supplier => GetSupplier(SupplierId);
    internal Supplier? GetSupplier(SubtitleSupplier supplierId) => Suppliers.FirstOrDefault(supplier => supplier.SupplierId == supplierId);
    private Supplier RequireSupplier(SubtitleSupplier supplierId) =>
        GetSupplier(supplierId) ?? throw new SubtitleException(SubtitleFailure.Unavailable);

    internal bool Enabled { get; private set; }
    internal SubtitleSupplier SupplierId => _account.SupplierId;
    internal string Username => _account.Username;
    internal bool HasSecret => _account.Secret.Length > 0;
    internal string[] Languages { get; private set; } = [];
    internal IReadOnlyList<string> EffectiveLanguages => Languages.Length == 0 ? [_language] : Languages;
    internal bool Ready => Supplier is { } supplier && supplier.Configured(_account) &&
        EffectiveLanguages.Any(supplier.Supports);
    internal SubtitleFailure Failure { get; private set; }
    internal IReadOnlyDictionary<string, SubtitleFailure> FileFailures { get; private set; } =
        new Dictionary<string, SubtitleFailure>();
    internal bool Checked { get; private set; }
    internal DateTimeOffset? RetryAt => _allowance.RetryAt;
    internal DateTimeOffset? LastRecheck { get; private set; }
    internal bool Rechecking { get; private set; }
    internal int Rechecked { get; private set; }
    internal int RecheckTotal { get; private set; }
    internal IReadOnlyDictionary<string, int> Missing { get; private set; } = new Dictionary<string, int>();
    internal int Pending { get; private set; }
    internal int? Found { get; private set; }
    internal int FindTotal { get; private set; }
    internal bool Finding => _finding.Count > 0;
    internal event EventHandler? Changed;

    internal async Task Initialize(string language)
    {
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_worker is not null)
                return;
            _language = language;
            await Load().ConfigureAwait(false);
            await RefreshCounts().ConfigureAwait(false);
            if (Failure == SubtitleFailure.Database)
                Failure = SubtitleFailure.None;
            _worker = Task.Run(Pump);
        }
        catch (Exception error) when (error is SqliteException or System.Security.Cryptography.CryptographicException)
        {
            Failure = SubtitleFailure.Database;
            throw;
        }
        finally
        {
            _operation.Release();
            Notify();
        }
    }

    internal void SetConnected(bool connected)
    {
        if (_disposed || _connected == connected)
            return;
        _connected = connected;
        Invalidate();
        if (connected)
            Wake();
    }

    internal async Task SetLanguage(string language)
    {
        if (_language == language)
            return;
        _language = language;
        if (Languages.Length == 0)
            await SetLanguages([]).ConfigureAwait(false);
        else
            Notify();
    }

    internal void RouteChanged()
    {
        lock (_requestSync)
        {
            Invalidate();
            Checked = false;
            _checkedDraft = null;
            _checkedAccount = null;
        }
        Wake();
        Notify();
    }

    internal async Task SetEnabled(bool enabled)
    {
        if (enabled && !Ready)
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        Invalidate();
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await database.Run(connection =>
            {
                Execute(connection, "UPDATE subtitle_settings SET enabled=$value WHERE singleton=1;", ("$value", enabled));
                if (!enabled)
                    Execute(connection, "DELETE FROM subtitle_jobs;");
                return true;
            }, _lifetime.Token).ConfigureAwait(false);
            Enabled = enabled;
            Found = null;
            _finding.Clear();
            await RefreshCounts().ConfigureAwait(false);
        }
        finally { _operation.Release(); }
        Notify();
        await Reconcile().ConfigureAwait(false);
    }

    internal async Task SetLanguages(IEnumerable<string> languages)
    {
        var values = languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Any(value => !EffectiveLanguages.Contains(value, StringComparer.OrdinalIgnoreCase) &&
            Supplier?.Supports(value) != true))
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        Invalidate();
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await database.Run(connection =>
            {
                Execute(connection, "UPDATE subtitle_settings SET languages=$value WHERE singleton=1;",
                    ("$value", System.Text.Json.JsonSerializer.Serialize(values)));
                var wanted = values.Length == 0 ? new[] { _language } : values;
                using var jobs = connection.CreateCommand();
                jobs.CommandText = "SELECT DISTINCT language FROM subtitle_jobs;";
                var obsolete = new List<string>();
                using (var reader = jobs.ExecuteReader())
                    while (reader.Read())
                        if (!wanted.Contains(reader.GetString(0), StringComparer.OrdinalIgnoreCase))
                            obsolete.Add(reader.GetString(0));
                foreach (var language in obsolete)
                    Execute(connection, "DELETE FROM subtitle_jobs WHERE language=$language;", ("$language", language));
                return true;
            }, _lifetime.Token).ConfigureAwait(false);
            Languages = values;
            await RefreshCounts().ConfigureAwait(false);
        }
        finally { _operation.Release(); }
        await Reconcile().ConfigureAwait(false);
    }

    internal Draft Edit() => new(SupplierId, Username, HasSecret);

    internal void EditChanged(Draft draft)
    {
        if (_checkedDraft != draft)
            return;
        _checkedDraft = null;
        _checkedAccount = null;
    }

    private Account Interpret(Draft draft)
    {
        var secret = draft.Secret.Length > 0 ? draft.Secret :
            draft.KeepSecret && draft.SupplierId == SupplierId ? _account.Secret : string.Empty;
        return RequireSupplier(draft.SupplierId).Interpret(draft, secret);
    }

    internal async Task Save(Draft draft)
    {
        var account = Interpret(draft);
        var changed = account != _account;
        var supplierChanged = account.SupplierId != SupplierId;
        if (changed)
            Invalidate();
        var secret = account.Secret.Length > 0 ? Protect(account.Secret) : null;
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var allowance = changed ? RequireSupplier(account.SupplierId).Allowance(account) : _allowance;
            await database.Run(connection =>
            {
                Execute(connection, """
                    UPDATE subtitle_settings SET supplier=$supplier, username=$username, secret=$secret,
                        enabled=CASE WHEN $changed THEN 0 ELSE enabled END,
                        request_reset=$request, download_reset=$download WHERE singleton=1;
                    """, ("$supplier", (int)account.SupplierId), ("$username", account.Username),
                    ("$secret", secret), ("$changed", supplierChanged),
                    ("$request", allowance.RequestReset?.ToUnixTimeMilliseconds()),
                    ("$download", allowance.DownloadReset?.ToUnixTimeMilliseconds()));
                if (supplierChanged)
                    Execute(connection, "DELETE FROM subtitle_jobs;");
                else if (changed)
                    Resume(connection);
                return true;
            }, _lifetime.Token).ConfigureAwait(false);
            _account = account;
            _allowance = allowance;
            if (supplierChanged)
            {
                Enabled = false;
                _finding.Clear();
                Found = null;
            }
            if (changed)
            {
                Checked = false;
                Failure = allowance.RetryAt > DateTimeOffset.UtcNow ? SubtitleFailure.Quota : SubtitleFailure.None;
            }
            if (_checkedDraft == draft && _checkedAccount == account)
                Checked = true;
            _checkedDraft = null;
            _checkedAccount = null;
            await RefreshCounts().ConfigureAwait(false);
        }
        finally { _operation.Release(); }
        Notify();
        await Reconcile().ConfigureAwait(false);
    }

    internal async Task Check(Draft? draft = null, CancellationToken cancellation = default)
    {
        var account = draft is null ? _account : Interpret(draft);
        CancellationToken requests;
        lock (_requestSync)
            requests = _requests.Token;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(requests, _lifetime.Token, cancellation);
        await _operation.WaitAsync(pending.Token).ConfigureAwait(false);
        try
        {
            await RequireSupplier(account.SupplierId).Check(http, account, pending.Token).ConfigureAwait(false);
            if (draft is null && Failure == SubtitleFailure.Authentication)
                await database.Run(connection =>
                {
                    Resume(connection);
                    return true;
                }, pending.Token).ConfigureAwait(false);
            lock (_requestSync)
            {
                pending.Token.ThrowIfCancellationRequested();
                if (draft is not null)
                {
                    _checkedDraft = draft;
                    _checkedAccount = account;
                }
                else if (_account == account)
                {
                    Checked = true;
                    if (Failure is not SubtitleFailure.Save and not SubtitleFailure.Database and not SubtitleFailure.Quota)
                        Failure = SubtitleFailure.None;
                }
            }
        }
        catch (SubtitleException error)
        {
            if (!pending.IsCancellationRequested && draft is null && _account == account)
                Failure = error.Reason;
            throw;
        }
        finally
        {
            try
            {
                if (account == _account && GetSupplier(account.SupplierId) is { } supplier)
                    await SaveAllowance(supplier.Allowance(account)).ConfigureAwait(false);
            }
            finally
            {
                _operation.Release();
                Notify();
                Wake();
            }
        }
    }

    private void Invalidate()
    {
        lock (_requestSync)
        {
            Interlocked.Increment(ref _revision);
            _requests.Cancel();
            _requests.Dispose();
            _requests = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        lock (_requestSync)
            _requests.Cancel();
        if (_worker is not null)
            await _worker.ConfigureAwait(false);
        await _operation.WaitAsync().ConfigureAwait(false);
        _operation.Release();
        _requests.Dispose();
        _lifetime.Dispose();
    }
}

internal sealed class Draft(SubtitleSupplier supplierId, string username, bool keepSecret)
{
    internal SubtitleSupplier SupplierId { get; set; } = supplierId;
    internal string Username { get; set; } = username;
    internal string Secret { get; set; } = string.Empty;
    internal bool KeepSecret { get; set; } = keepSecret;
}

internal sealed record Account(SubtitleSupplier SupplierId, string Username, string Secret)
{
    public override string ToString() => SupplierId.ToString();
}

internal sealed class SubtitleException(SubtitleFailure reason, DateTimeOffset? retryAt = null) : Exception(reason.ToString())
{
    internal SubtitleFailure Reason { get; } = reason;
    internal DateTimeOffset? RetryAt { get; } = retryAt;
}
