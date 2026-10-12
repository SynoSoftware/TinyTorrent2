using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using SupplierType = Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition(
    Database database,
    ProviderHttp http,
    Task settled,
    Func<int> lookupLimit
) : IAsyncDisposable
{
    internal const int MaximumExtras = 3;

    private readonly SemaphoreSlim _operation = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _requestSync = new();
    private CancellationTokenSource _requests = new();
    private Task? _worker;
    private string _language = "en";
    private bool _connected;
    private bool _disposed;
    private Account _account = new(SubtitleSupplier.OpenSubtitles, string.Empty, string.Empty);
    private Allowance _allowance = new();
    private Draft? _checkedDraft;
    private Account? _checkedAccount;
    internal IReadOnlyList<Supplier> Suppliers { get; } = SupplierType.CreateAll();
    internal Supplier? Supplier => GetSupplier(SupplierId);

    internal Supplier? GetSupplier(SubtitleSupplier supplierId) =>
        Suppliers.FirstOrDefault(supplier => supplier.SupplierId == supplierId);

    private Supplier RequireSupplier(SubtitleSupplier supplierId) =>
        GetSupplier(supplierId) ?? throw new SubtitleException(SubtitleFailure.Unavailable);

    internal bool IsDisposed => _disposed;
    internal bool Enabled { get; private set; }
    internal SubtitleSupplier SupplierId => _account.SupplierId;
    internal string Username => _account.Username;
    internal bool HasSecret => _account.Secret.Length > 0;
    internal string[] Languages { get; private set; } = [];

    // Automatic searches keep the saved number of alternatives independently of Find.
    internal int Extras { get; private set; } = 1;
    internal IReadOnlyList<string> EffectiveLanguages =>
        Languages.Length == 0 ? [_language] : Languages;
    internal bool Ready =>
        Supplier is { } supplier
        && supplier.Configured(_account)
        && EffectiveLanguages.Any(supplier.Supports);
    internal bool CanCheck => Supplier?.Configured(_account) == true;

    // Failure derives from the database health, the saved configuration, the
    // last supplier answer, the allowance and the file health, so each fact
    // has one writer.
    private SubtitleFailure _supplierFailure;
    private SqliteException? _databaseFailure;
    private bool _saveFailed;
    internal SqliteException? DatabaseFailure => _databaseFailure;
    internal SubtitleFailure Failure =>
        _databaseFailure is not null ? SubtitleFailure.Database
        : Supplier is not { Available: true } ? SubtitleFailure.Unavailable
        : !Ready ? SubtitleFailure.Unconfigured
        : _supplierFailure != SubtitleFailure.None ? _supplierFailure
        : RetryAt > DateTimeOffset.UtcNow ? SubtitleFailure.Quota
        : _saveFailed ? SubtitleFailure.Save
        : SubtitleFailure.None;
    internal bool Checked { get; private set; }
    internal DateTimeOffset? RetryAt => _allowance.RetryAt;

    // Read current membership from SQLite so Library selections and counts agree.
    internal async Task<int> Count(SubtitleScope scope, IReadOnlySet<long>? entries = null)
    {
        if (_disposed)
            return 0;
        try
        {
            var targets = (await Targets(_lifetime.Token).ConfigureAwait(false))
                .Where(Eligible)
                .ToArray();
            return await Run(
                    connection =>
                        Searchable(connection, targets, scope, entries)
                            .Select(target => target.Videos[0].EntryId)
                            .Distinct()
                            .Count(),
                    _lifetime.Token
                )
                .ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    internal int WithSubtitles { get; private set; }
    internal int? Found { get; private set; }

    // The movies Find accepted, counted when it starts, because the worker
    // changes the batch while the page reads the total.
    internal int FindTotal { get; private set; }
    internal long BatchId { get; private set; }

    // Manual work is unfinished: a batch, or manual jobs kept from an earlier
    // session.
    internal bool Finding { get; private set; }
    internal event EventHandler? Changed;
    internal event EventHandler? LanguagesChanged;

    // A subtitle that could not be downloaded, saved or deleted. Raised once
    // for each failure, on a worker thread, so the window reports it as an
    // event.
    internal event EventHandler<FileFailure>? FileFailed;

    internal async Task Initialize(string language)
    {
        Invalidate();
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            _language = language;
            _supplierFailure = SubtitleFailure.None;
            _saveFailed = false;
            Checked = false;
            _checkedDraft = null;
            _checkedAccount = null;
            _batch.Clear();
            Found = null;
            await Load().ConfigureAwait(false);
            await RefreshCounts().ConfigureAwait(false);
            _databaseFailure = null;
            if (_worker is null)
            {
                _worker = Task.Run(Pump);
                _ = settled.ContinueWith(_ => Wake(), TaskScheduler.Default);
            }
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

    // Failures and retry waits seen on the old route say nothing about the
    // new one, so waiting work tries the new route at once.
    internal async Task RouteChanged()
    {
        lock (_requestSync)
        {
            Invalidate();
            Checked = false;
            _checkedDraft = null;
            _checkedAccount = null;
            if (_supplierFailure is SubtitleFailure.Route or SubtitleFailure.Network)
                _supplierFailure = SubtitleFailure.None;
        }
        Notify();
        try
        {
            await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                await Run(
                        connection =>
                        {
                            Execute(
                                connection,
                                "UPDATE subtitle_jobs SET retry_at=0 WHERE outcome=$pending;",
                                ("$pending", (int)SubtitleOutcome.Pending)
                            );
                            return true;
                        },
                        _lifetime.Token
                    )
                    .ConfigureAwait(false);
            }
            finally
            {
                _operation.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (SqliteException)
        {
            Notify();
        }
        Wake();
    }

    internal async Task SetEnabled(bool enabled)
    {
        Invalidate();
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await Run(
                    connection =>
                    {
                        Execute(
                            connection,
                            "UPDATE subtitle_settings SET enabled=$value WHERE singleton=1;",
                            ("$value", enabled)
                        );
                        if (!enabled)
                            Cancel(connection, manual: false);
                        else
                            Execute(
                                connection,
                                """
                                UPDATE subtitle_jobs SET outcome=$pending,retry_at=0
                                WHERE manual=0 AND outcome=$cancelled
                                    AND (reference IS NOT NULL OR lower(path) LIKE '%.'||lower(language)||'.srt')
                                    AND EXISTS (
                                    SELECT 1 FROM subtitle_job_sources s JOIN collected_files f
                                        ON f.origin=s.origin AND f.file_index=s.file_index
                                    JOIN new_origins n ON n.origin=f.origin
                                    WHERE s.path=subtitle_jobs.path AND s.language=subtitle_jobs.language
                                        AND f.wanted=1 AND f.complete=0);
                                """,
                                ("$pending", (int)SubtitleOutcome.Pending),
                                ("$cancelled", (int)SubtitleOutcome.Cancelled)
                            );
                        return true;
                    },
                    _lifetime.Token
                )
                .ConfigureAwait(false);
            Enabled = enabled;
            await RefreshCounts().ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
        Notify();
        await Reconcile().ConfigureAwait(false);
    }

    internal async Task SetLanguages(IEnumerable<string> languages)
    {
        var values = languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (
            values.Any(value =>
                !EffectiveLanguages.Contains(value, StringComparer.OrdinalIgnoreCase)
                && Supplier?.Supports(value) != true
            )
        )
            throw new SubtitleException(SubtitleFailure.Unconfigured);
        Invalidate();
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await Run(
                    connection =>
                    {
                        Execute(
                            connection,
                            "UPDATE subtitle_settings SET languages=$value WHERE singleton=1;",
                            ("$value", System.Text.Json.JsonSerializer.Serialize(values))
                        );
                        var wanted = values.Length == 0 ? new[] { _language } : values;
                        using var jobs = connection.CreateCommand();
                        jobs.CommandText = "SELECT DISTINCT language FROM subtitle_jobs;";
                        var obsolete = new List<string>();
                        using (var reader = jobs.ExecuteReader())
                            while (reader.Read())
                                if (
                                    !wanted.Contains(
                                        reader.GetString(0),
                                        StringComparer.OrdinalIgnoreCase
                                    )
                                )
                                    obsolete.Add(reader.GetString(0));
                        foreach (var language in obsolete)
                            Cancel(connection, language: language);
                        return true;
                    },
                    _lifetime.Token
                )
                .ConfigureAwait(false);
            Languages = values;
            await RefreshCounts().ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
        await Reconcile().ConfigureAwait(false);
    }

    internal async Task SetExtras(int extras)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(extras);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(extras, MaximumExtras);
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await Run(
                    connection =>
                    {
                        Execute(
                            connection,
                            "UPDATE subtitle_settings SET extras=$value WHERE singleton=1;",
                            ("$value", extras)
                        );
                        return true;
                    },
                    _lifetime.Token
                )
                .ConfigureAwait(false);
            Extras = extras;
        }
        finally
        {
            _operation.Release();
            Notify();
        }
    }

    // The shared search limit changed in Library; Find's counts depend on it.
    internal void RefreshLookupLimit() => Notify();

    // Another supplier starts with an empty account, so the saved supplier's
    // credentials are never sent to it.
    internal Draft Edit(SubtitleSupplier supplierId) =>
        supplierId == SupplierId
            ? new(SupplierId, Username, HasSecret)
            : new(supplierId, string.Empty, false);

    internal void EditChanged(Draft draft)
    {
        if (_checkedDraft != draft)
            return;
        _checkedDraft = null;
        _checkedAccount = null;
    }

    private Account Interpret(Draft draft)
    {
        var secret =
            draft.Secret.Length > 0 ? draft.Secret
            : draft.KeepSecret && draft.SupplierId == SupplierId ? _account.Secret
            : string.Empty;
        return RequireSupplier(draft.SupplierId).Interpret(draft, secret);
    }

    internal bool CanCheckDraft(Draft draft)
    {
        try
        {
            return RequireSupplier(draft.SupplierId).Configured(Interpret(draft));
        }
        catch (SubtitleException)
        {
            return false;
        }
    }

    internal Task SelectSupplier(SubtitleSupplier supplierId) =>
        SaveAccount(new Account(supplierId, string.Empty, string.Empty));

    internal Task Save(Draft draft) => SaveAccount(Interpret(draft));

    private async Task SaveAccount(Account account)
    {
        var changed = account != _account;
        // Saving the values Check just accepted corrects refused access even
        // when they equal the saved ones.
        var verified = _checkedAccount == account;
        if (changed)
            Invalidate();
        var secret = account.Secret.Length > 0 ? Protect(account.Secret) : null;
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var allowance = changed
                ? RequireSupplier(account.SupplierId).Allowance(account)
                : _allowance;
            await Run(
                    connection =>
                    {
                        Execute(
                            connection,
                            """
                            UPDATE subtitle_settings SET supplier=$supplier, username=$username, secret=$secret,
                                request_reset=$request, download_reset=$download WHERE singleton=1;
                            """,
                            ("$supplier", (int)account.SupplierId),
                            ("$username", account.Username),
                            ("$secret", secret),
                            ("$request", allowance.RequestReset?.ToUnixTimeMilliseconds()),
                            ("$download", allowance.DownloadReset?.ToUnixTimeMilliseconds())
                        );
                        if (changed || verified)
                            Resume(connection);
                        // References cannot cross suppliers; extra jobs only
                        // download their reference, while the first job can search again.
                        if (account.SupplierId != SupplierId)
                        {
                            using var references = connection.CreateCommand();
                            references.CommandText = """
                                SELECT path,language FROM subtitle_jobs WHERE reference IS NOT NULL
                                    AND lower(path) GLOB '*.'||lower(language)||'.[2-9]*.srt';
                                """;
                            var extras = new List<(string Path, string Language)>();
                            using (var reader = references.ExecuteReader())
                                while (reader.Read())
                                    extras.Add((reader.GetString(0), reader.GetString(1)));
                            foreach (var extra in extras)
                                Cancel(connection, language: extra.Language, path: extra.Path);
                            Execute(
                                connection,
                                "UPDATE subtitle_jobs SET reference=NULL WHERE reference IS NOT NULL;"
                            );
                        }
                        return true;
                    },
                    _lifetime.Token
                )
                .ConfigureAwait(false);
            _account = account;
            _allowance = allowance;
            if (changed)
            {
                Checked = false;
                _supplierFailure = SubtitleFailure.None;
            }
            if (verified)
                Answered(SubtitleFailure.None);
            _checkedDraft = null;
            _checkedAccount = null;
            await RefreshCounts().ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
        Notify();
        await Reconcile().ConfigureAwait(false);
    }

    internal async Task Check(Draft? draft = null, CancellationToken cancellation = default)
    {
        var account = draft is null ? _account : Interpret(draft);
        CancellationToken requests;
        lock (_requestSync)
            requests = _requests.Token;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(
            requests,
            _lifetime.Token,
            cancellation
        );
        await _operation.WaitAsync(pending.Token).ConfigureAwait(false);
        try
        {
            await RequireSupplier(account.SupplierId)
                .Check(http, account, pending.Token)
                .ConfigureAwait(false);
            if (draft is null && _supplierFailure == SubtitleFailure.Authentication)
                await Run(
                        connection =>
                        {
                            Resume(connection);
                            return true;
                        },
                        pending.Token
                    )
                    .ConfigureAwait(false);
            lock (_requestSync)
            {
                pending.Token.ThrowIfCancellationRequested();
                if (draft is not null)
                {
                    _checkedDraft = draft;
                    _checkedAccount = account;
                }
                else if (_account == account)
                    Answered(SubtitleFailure.None);
            }
        }
        catch (SubtitleException error)
        {
            if (!pending.IsCancellationRequested && draft is null && _account == account)
                Answered(error.Reason);
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
            _requests.Cancel();
            _requests.Dispose();
            _requests = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        }
    }

    // Records the supplier's answer to the saved account. A format failure
    // belongs to one movie and a used-up allowance to Allowance, so neither
    // is an account problem; refused access means access is no longer checked.
    private void Answered(SubtitleFailure answer)
    {
        _supplierFailure = answer is SubtitleFailure.Format or SubtitleFailure.Quota
            ? SubtitleFailure.None
            : answer;
        if (answer == SubtitleFailure.None)
            Checked = true;
        else if (answer == SubtitleFailure.Authentication)
            Checked = false;
    }

    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    // Every database call passes here, so a SQLite failure is recorded once
    // and stays until Initialize or Reconcile completes.
    private async Task<T> Run<T>(Func<SqliteConnection, T> action, CancellationToken cancellation)
    {
        try
        {
            return await database.Run(action, cancellation).ConfigureAwait(false);
        }
        catch (SqliteException error)
        {
            _databaseFailure = error;
            throw;
        }
    }

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

internal sealed record FileFailure(string Path, SubtitleFailure Reason);

internal sealed record Account(SubtitleSupplier SupplierId, string Username, string Secret)
{
    public override string ToString() => SupplierId.ToString();
}

internal sealed class SubtitleException(SubtitleFailure reason, DateTimeOffset? retryAt = null)
    : Exception(reason.ToString())
{
    internal SubtitleFailure Reason { get; } = reason;
    internal DateTimeOffset? RetryAt { get; } = retryAt;
}
