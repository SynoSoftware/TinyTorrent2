using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Syno.TinyTorrent.Shared.Network;

internal sealed partial class RequestPolicy
{
    private static readonly TimeSpan MaximumWait = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _admission = new(1);
    private readonly Dictionary<(string Provider, string Identity), long> _starts = [];

    internal async Task Reserve(
        OutboundRequest request,
        FreshRequest? fresh,
        CancellationToken cancellation
    )
    {
        var access = request.Access;
        var pacing = request.Allowance ?? access;
        var scope = (pacing.ProviderId, pacing.Identity);
        while (true)
        {
            TimeSpan delay;
            await _admission.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                delay = _starts.TryGetValue(scope, out var start)
                    ? TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(start)
                    : TimeSpan.Zero;
                if (delay <= TimeSpan.Zero)
                {
                    delay = await database
                        .Run(
                            connection =>
                            {
                                var now = DateTimeOffset.UtcNow;
                                var holds = Holds(connection, access);
                                if (
                                    request.Authentication is { } authentication
                                    && authentication != access
                                )
                                    holds.AddRange(Holds(connection, authentication));
                                if (request.Allowance is { } allowance && allowance != access)
                                    holds.AddRange(Holds(connection, allowance));
                                foreach (
                                    var hold in holds.Where(hold =>
                                        hold.Block.Kind != RequestBlockKind.Backoff
                                    )
                                )
                                    if (
                                        (hold.Block.RetryAt is null || hold.Block.RetryAt > now)
                                        && !(
                                            hold.Block.Kind == RequestBlockKind.Attention
                                            && fresh?.Command.Recheck == true
                                            && fresh.Admit(hold.Access, hold.Checked)
                                        )
                                    )
                                        throw new RequestBlockedException(hold.Block);
                                using var command = AccessCommand(connection, pacing);
                                command.CommandText = """
                                    DELETE FROM request_usage WHERE started<=$oldest;
                                    SELECT started FROM request_usage WHERE provider_id=$provider AND identity=$identity
                                        AND started>$since ORDER BY started;
                                    """;
                                command.Parameters.AddWithValue(
                                    "$oldest",
                                    now.AddDays(-1).UtcTicks
                                );
                                var retention =
                                    request.Windows.Count == 0
                                        ? TimeSpan.FromSeconds(1)
                                        : request.Windows.Max(window => window.Duration);
                                command.Parameters.AddWithValue(
                                    "$since",
                                    (now - retention).UtcTicks
                                );
                                var usage = new List<DateTimeOffset>();
                                using (var reader = command.ExecuteReader())
                                    while (reader.Read())
                                        usage.Add(new(reader.GetInt64(0), TimeSpan.Zero));
                                DateTimeOffset? due = null;
                                foreach (var window in request.Windows)
                                {
                                    var recent = usage
                                        .Where(time => time > now - window.Duration)
                                        .ToArray();
                                    if (recent.Length >= window.Maximum)
                                    {
                                        var next =
                                            recent[recent.Length - window.Maximum]
                                            + window.Duration;
                                        if (due is null || next > due)
                                            due = next;
                                    }
                                }
                                if (due is { } deadline)
                                    throw new RequestBlockedException(
                                        new(RequestBlockKind.Allowance, deadline)
                                    );
                                // Saved spacing also prevents reopening from admitting an immediate second start.
                                if (usage.LastOrDefault() is var last && last > now.AddSeconds(-1))
                                    return last.AddSeconds(1) - now;
                                foreach (
                                    var hold in holds.Where(hold =>
                                        hold.Block.Kind == RequestBlockKind.Backoff
                                    )
                                )
                                    if (
                                        hold.Block.RetryAt > now
                                        && fresh?.Admit(hold.Access, hold.Checked) != true
                                    )
                                        throw new RequestBlockedException(hold.Block);
                                command.CommandText = """
                                    DELETE FROM request_usage WHERE provider_id=$provider AND identity=$identity AND started<=$since;
                                    INSERT INTO request_usage(provider_id,identity,started) VALUES($provider,$identity,$now);
                                    """;
                                command.Parameters.AddWithValue("$now", now.UtcTicks);
                                command.ExecuteNonQuery();
                                return TimeSpan.Zero;
                            },
                            cancellation
                        )
                        .ConfigureAwait(false);
                    if (delay <= TimeSpan.Zero)
                    {
                        _starts[scope] = Stopwatch.GetTimestamp();
                        return;
                    }
                }
            }
            finally
            {
                _admission.Release();
            }
            await Task.Delay(delay, cancellation).ConfigureAwait(false);
        }
    }

    internal Task<RequestBlock?> Read(ProviderAccess access, CancellationToken cancellation) =>
        Read([access], cancellation);

    internal Task<RequestBlock?> Read(
        IReadOnlyList<ProviderAccess> scopes,
        CancellationToken cancellation
    ) => database.Run(connection => Read(connection, scopes), cancellation);

    internal RequestBlock? Read(SqliteConnection connection, IReadOnlyList<ProviderAccess> scopes)
    {
        var now = DateTimeOffset.UtcNow;
        RequestBlock? backoff = null;
        foreach (var scope in scopes)
        foreach (var hold in Holds(connection, scope))
        {
            var block = hold.Block;
            if (block.RetryAt <= now)
                continue;
            if (!block.CanForce)
                return block;
            backoff ??= block;
        }
        return backoff;
    }

    internal Task<RequestBlock> Fail(ProviderAccess access, RequestBlock block) =>
        database.Run(connection =>
        {
            var now = DateTimeOffset.UtcNow;
            using var command = AccessCommand(connection, access);
            command.Parameters.AddWithValue("$reason", (int)block.Kind);
            command.CommandText = """
                SELECT failures FROM request_holds WHERE provider_id=$provider AND identity=$identity
                    AND operation=$operation AND reason=$reason;
                """;
            var failures =
                block.Kind == RequestBlockKind.Backoff
                    ? Math.Min(16, Convert.ToInt32(command.ExecuteScalar()) + 1)
                    : 0;
            // A daily quota is the longest wait a provider has a reason to ask
            // for, so a later time is read as a damaged or hostile header.
            var asked = block.RetryAt > now + MaximumWait ? now + MaximumWait : block.RetryAt;
            DateTimeOffset? until = block.Kind switch
            {
                RequestBlockKind.Backoff => now.AddSeconds(
                    Math.Min(
                        3600,
                        30 * Math.Pow(2, failures - 1) * (1 + Random.Shared.NextDouble() * 0.1)
                    )
                ),
                RequestBlockKind.RemoteWait => asked > now ? asked : now.AddHours(1),
                RequestBlockKind.Attention => null,
                _ => throw new ArgumentOutOfRangeException(nameof(block)),
            };
            if (asked > until)
                until = asked;
            command.CommandText = """
                INSERT INTO request_holds(provider_id,identity,operation,reason,retry_at,failures,checked)
                VALUES($provider,$identity,$operation,$reason,$until,$failures,$checked)
                ON CONFLICT(provider_id,identity,operation,reason) DO UPDATE SET
                    retry_at=CASE WHEN excluded.reason=$remote THEN MAX(retry_at,excluded.retry_at)
                        ELSE excluded.retry_at END,
                    failures=excluded.failures,checked=excluded.checked;
                SELECT retry_at FROM request_holds WHERE provider_id=$provider AND identity=$identity
                    AND operation=$operation AND reason=$reason;
                """;
            command.Parameters.AddWithValue("$until", (object?)until?.UtcTicks ?? DBNull.Value);
            command.Parameters.AddWithValue("$failures", failures);
            command.Parameters.AddWithValue("$checked", now.UtcTicks);
            command.Parameters.AddWithValue("$remote", (int)RequestBlockKind.RemoteWait);
            var saved = command.ExecuteScalar();
            return new RequestBlock(
                block.Kind,
                saved is long ticks ? new(ticks, TimeSpan.Zero) : null
            );
        });

    internal Task Succeed(ProviderAccess access) =>
        database.Run(connection => Succeed(connection, access));

    internal int Succeed(SqliteConnection connection, ProviderAccess access)
    {
        using var command = AccessCommand(connection, access);
        command.CommandText = """
            DELETE FROM request_holds WHERE provider_id=$provider AND identity=$identity
                AND operation=$operation AND reason IN ($backoff,$attention);
            """;
        command.Parameters.AddWithValue("$backoff", (int)RequestBlockKind.Backoff);
        command.Parameters.AddWithValue("$attention", (int)RequestBlockKind.Attention);
        return command.ExecuteNonQuery();
    }

    private static List<AccessHold> Holds(SqliteConnection connection, ProviderAccess access)
    {
        using var command = AccessCommand(connection, access);
        command.CommandText = """
            SELECT reason,retry_at,checked,operation FROM request_holds
            WHERE provider_id=$provider AND identity=$identity AND operation IN ('',$operation)
            ORDER BY CASE reason WHEN $remote THEN 0 WHEN $attention THEN 1 ELSE 2 END,retry_at DESC;
            """;
        command.Parameters.AddWithValue("$remote", (int)RequestBlockKind.RemoteWait);
        command.Parameters.AddWithValue("$attention", (int)RequestBlockKind.Attention);
        var holds = new List<AccessHold>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            holds.Add(
                new(
                    new(
                        (RequestBlockKind)reader.GetInt32(0),
                        reader.IsDBNull(1) ? null : new(reader.GetInt64(1), TimeSpan.Zero)
                    ),
                    reader.GetInt64(2),
                    access with
                    {
                        Operation = reader.GetString(3),
                    }
                )
            );
        return holds;
    }

    private static SqliteCommand AccessCommand(SqliteConnection connection, ProviderAccess access)
    {
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$provider", access.ProviderId);
        command.Parameters.AddWithValue("$identity", access.Identity);
        command.Parameters.AddWithValue("$operation", access.Operation);
        return command;
    }

    private sealed record AccessHold(RequestBlock Block, long Checked, ProviderAccess Access);
}

internal sealed record ProviderAccess(string ProviderId, string Identity, string Operation);

internal sealed record RateWindow(TimeSpan Duration, int Maximum);

internal sealed record OutboundRequest(ProviderAccess Access)
{
    internal IReadOnlyList<RateWindow> Windows { get; init; } = [];
    internal ProviderAccess? Authentication { get; init; }
    internal ProviderAccess? Allowance { get; init; }
}

internal sealed class RetryCommand(long accepted)
{
    internal long Accepted { get; } = accepted;
    internal bool Recheck { get; init; }
    private readonly Dictionary<ProviderAccess, string> _probes = [];

    internal bool Admit(ProviderAccess access, string questionId, long observed)
    {
        if (observed > Accepted)
            return false;
        lock (_probes)
        {
            if (_probes.TryGetValue(access, out var previous))
                return previous == questionId;
            _probes.Add(access, questionId);
            return true;
        }
    }
}

internal sealed record FreshRequest(RetryCommand Command, RequestKey Key)
{
    /// <summary>
    /// A request the person makes now for <paramref name="access"/>, which
    /// tries once past a refusal or a backoff that an earlier request left.
    /// </summary>
    internal static FreshRequest Once(ProviderAccess access) =>
        new(
            new RetryCommand(DateTimeOffset.UtcNow.UtcTicks) { Recheck = true },
            RequestKey.Create(access.ProviderId, access.Operation, [])
        );

    internal bool Admit(ProviderAccess access, long observed) =>
        Command.Admit(access, Key.QuestionId, observed);
}
