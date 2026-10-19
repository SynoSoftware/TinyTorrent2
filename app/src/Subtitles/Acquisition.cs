using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Shared.Storage;
using Syno.TinyTorrent.Subtitles.Suppliers;
using SupplierType = Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition(
    Database database,
    ProviderHttp http,
    Task settled,
    RequestPolicy policy
) : IAsyncDisposable
{
    internal required BackgroundWork Work { get; init; }
    internal const int MaximumExtras = 3;

    private readonly SemaphoreSlim _operation = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task _saves = Task.CompletedTask;
    private readonly object _requestSync = new();
    private CancellationTokenSource _requests = new();
    private Task? _worker;
    private string _language = "en";
    private bool _connected;
    private bool _disposed;
    private Account _account = new(SubtitleSupplier.OpenSubtitles, string.Empty, string.Empty);
    private Allowance _allowance = new();
    private Account? _checkedAccount;
    internal IReadOnlyList<Supplier> Suppliers { get; } = SupplierType.CreateAll(policy);
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
            System.Diagnostics.Debug.WriteLine($"Subtitle candidate count cancelled ({scope}).");
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
    internal int Blocked { get; private set; }
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
            await policy.Initialize(_lifetime.Token).ConfigureAwait(false);
            _language = language;
            _supplierFailure = SubtitleFailure.None;
            _saveFailed = false;
            Checked = false;
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

    // Called while acquisition is suspended; admission rechecks remote waits.
    internal void ChangeRoute(SqliteConnection connection)
    {
        Execute(
            connection,
            "UPDATE subtitle_jobs SET retry_at=0 WHERE outcome=$pending;",
            ("$pending", (int)SubtitleOutcome.Pending)
        );
        Checked = false;
        _checkedAccount = null;
        if (_supplierFailure is SubtitleFailure.Route or SubtitleFailure.Network)
            _supplierFailure = SubtitleFailure.None;
    }

    internal async Task Suspend(Func<Task> apply)
    {
        Invalidate();
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await apply().ConfigureAwait(false);
            await RefreshAllowance().ConfigureAwait(false);
            if (_supplierFailure == SubtitleFailure.Network)
                _supplierFailure = SubtitleFailure.None;
            await RefreshCounts().ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
            Notify();
            Wake();
        }
    }

    internal void ResetProtection(SqliteConnection connection, string? providerId)
    {
        Execute(
            connection,
            """
            UPDATE subtitle_searches SET reusable=0
            WHERE ($provider IS NULL OR provider_id=$provider)
                AND (rejected=1 OR EXISTS(SELECT 1 FROM request_blocks b
                    WHERE b.provider_id=subtitle_searches.provider_id AND b.question_id=subtitle_searches.question_id));
            """,
            ("$provider", providerId)
        );
        if (providerId is not null && providerId != RequireSupplier(SupplierId).ProviderId)
            return;
        Unblock(connection);
        Execute(
            connection,
            """
            UPDATE subtitle_jobs SET retry_at=0 WHERE outcome=$pending;
            """,
            ("$pending", (int)SubtitleOutcome.Pending)
        );
    }

    // Completes when every settings write queued so far has been applied or
    // has failed. Until then a view keeps the person's choices instead of
    // showing the saved values.
    internal Task Saving => _saves;

    // Applies settings writes one at a time in the order the person made them,
    // whichever view made them. Callers are on the UI thread, and
    // DisposeAsync applies every queued write before the lifetime ends.
    private Task Queue(Func<Task> write)
    {
        var previous = _saves;
        // Completes before the returned task, so a caller that resumes after
        // its write sees Saving finished when nothing else is queued.
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _saves = written.Task;
        return Write();

        async Task Write()
        {
            try
            {
                await previous.ConfigureAwait(false);
                if (_disposed)
                    throw new OperationCanceledException();
                await write().ConfigureAwait(false);
            }
            finally
            {
                written.SetResult();
            }
        }
    }

    internal Task SetEnabled(bool enabled) => Queue(() => WriteEnabled(enabled));

    private async Task WriteEnabled(bool enabled)
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

    internal Task SetLanguages(IEnumerable<string> languages) =>
        Queue(() => WriteLanguages(languages));

    // Follows the interface language, or keeps the languages in effect when
    // the write runs.
    internal Task FollowLanguage(bool follows) =>
        Queue(() => WriteLanguages(follows ? [] : EffectiveLanguages));

    // Adds and removes languages from the ones in effect when the write runs.
    // An empty result would follow the interface language, which only
    // FollowLanguage chooses.
    internal Task ChangeLanguages(string[] added, string[] removed) =>
        Queue(async () =>
        {
            var wanted = EffectiveLanguages
                .Where(tag => !removed.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Concat(added)
                .ToArray();
            if (wanted.Length > 0)
                await WriteLanguages(wanted).ConfigureAwait(false);
        });

    private async Task WriteLanguages(IEnumerable<string> languages)
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

    internal Task SetExtras(int extras) => Queue(() => WriteExtras(extras));

    private async Task WriteExtras(int extras)
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

    // Find's counts depend on the shared search limit.
    internal void RefreshLookupLimit() => Notify();

    // Another supplier starts with an empty account, so the saved supplier's
    // credentials are never sent to it.
    internal Draft Edit(SubtitleSupplier supplierId) =>
        supplierId == SupplierId
            ? new(SupplierId, Username, string.Empty, HasSecret)
            : new(supplierId, string.Empty, string.Empty, false);

    private Account Interpret(Draft draft)
    {
        var secret =
            draft.Secret.Length > 0 ? draft.Secret
            : draft.KeepSecret && draft.SupplierId == SupplierId ? _account.Secret
            : string.Empty;
        return RequireSupplier(draft.SupplierId).Interpret(draft, secret);
    }

    // Choosing the saved supplier again keeps its account.
    internal Task SelectSupplier(SubtitleSupplier supplierId) =>
        Queue(() =>
            supplierId == SupplierId
                ? Task.CompletedTask
                : SaveAccount(new Account(supplierId, string.Empty, string.Empty))
        );

    internal Task Save(Draft draft) => Queue(() => SaveAccount(Interpret(draft)));

    private async Task SaveAccount(Account account)
    {
        var changed = account != _account;
        if (changed)
            Invalidate();
        var secret = account.Secret.Length > 0 ? Credentials.Protect(account.Secret) : null;
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            // Saving the values Check just accepted corrects refused access even
            // when they equal the saved ones.
            var verified = _checkedAccount == account;
            var allowance = changed
                ? await RequireSupplier(account.SupplierId).Allowance(account).ConfigureAwait(false)
                : _allowance;
            await Run(
                    connection =>
                    {
                        Execute(
                            connection,
                            """
                            UPDATE subtitle_settings SET supplier=$supplier, username=$username, secret=$secret WHERE singleton=1;
                            """,
                            ("$supplier", (int)account.SupplierId),
                            ("$username", account.Username),
                            ("$secret", secret)
                        );
                        if (changed)
                            policy.Succeed(
                                connection,
                                RequireSupplier(account.SupplierId).Authentication(account)
                            );
                        if (changed || verified)
                            Resume(connection);
                        if (changed)
                            Execute(
                                connection,
                                "UPDATE subtitle_jobs SET fresh=NULL WHERE fresh IS NOT NULL;"
                            );
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
                        if (changed)
                            Unblock(connection);
                        return true;
                    },
                    _lifetime.Token
                )
                .ConfigureAwait(false);
            _account = account;
            _allowance = allowance;
            if (changed)
            {
                _retryCommands.Clear();
                Checked = false;
                _supplierFailure = SubtitleFailure.None;
            }
            if (verified)
                Answered(SubtitleFailure.None);
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
                    _checkedAccount = account;
                else if (_account == account)
                    Answered(SubtitleFailure.None);
            }
        }
        catch (SubtitleException error)
        {
            if (!pending.IsCancellationRequested)
            {
                if (error.Reason == SubtitleFailure.Authentication && _checkedAccount == account)
                    _checkedAccount = null;
                if (draft is null && _account == account)
                    Answered(error.Reason);
            }
            throw;
        }
        finally
        {
            try
            {
                if (account == _account && GetSupplier(account.SupplierId) is { } supplier)
                    _allowance = await supplier.Allowance(account).ConfigureAwait(false);
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
            foreach (var run in new[] { _manualWork, _automaticWork })
                if (run is not null)
                    run.Report(run.Observation.Progress, new WorkExecution.Waiting());
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
        await _saves.ConfigureAwait(false);
        _disposed = true;
        _lifetime.Cancel();
        Task? reconciling;
        lock (_requestSync)
        {
            _requests.Cancel();
            reconciling = _reconciling;
        }
        if (_worker is not null)
            await _worker.ConfigureAwait(false);
        if (reconciling is not null)
        {
            try
            {
                await reconciling.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
        foreach (var run in new[] { _manualWork, _automaticWork })
            if (run is not null)
            {
                await run.Stopped.ConfigureAwait(false);
                await run.Finish().ConfigureAwait(false);
            }
        await _operation.WaitAsync().ConfigureAwait(false);
        _operation.Release();
        _requests.Dispose();
        _lifetime.Dispose();
    }
}

internal sealed record Draft(
    SubtitleSupplier SupplierId,
    string Username,
    string Secret,
    bool KeepSecret
);

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
