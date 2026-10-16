using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Shared.Storage;

namespace Syno.TinyTorrent.Shared.Network;

internal sealed partial class RequestPolicy(Database database)
{
    internal const int DefaultLimit = 5;
    private readonly SemaphoreSlim _settings = new(1);
    private bool _initialized;
    internal int Limit { get; private set; } = DefaultLimit;

    internal async Task Initialize(CancellationToken cancellation)
    {
        await _settings.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;
            Limit = await database
                .Run(
                    connection =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText =
                            "SELECT lookups FROM request_settings WHERE singleton=1;";
                        if (
                            command.ExecuteScalar() is long value
                            && value is >= 1 and <= int.MaxValue
                        )
                            return (int)value;
                        command.CommandText =
                            "UPDATE request_settings SET lookups=$limit WHERE singleton=1;";
                        command.Parameters.AddWithValue("$limit", DefaultLimit);
                        command.ExecuteNonQuery();
                        return DefaultLimit;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _settings.Release();
        }
    }

    internal async Task SaveLimit(int limit, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await _settings.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await database
                .Run(
                    connection =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText =
                            "UPDATE request_settings SET lookups=$limit WHERE singleton=1;";
                        command.Parameters.AddWithValue("$limit", limit);
                        return command.ExecuteNonQuery();
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            Limit = limit;
            _initialized = true;
        }
        finally
        {
            _settings.Release();
        }
    }

    internal Task<RequestBlock?> Read(
        LookupRequest request,
        CancellationToken cancellation,
        long since = 0
    ) => database.Run(connection => Read(connection, request, since), cancellation);

    internal Task Record(LookupRequest request, RequestOutcome outcome) =>
        database.Run(connection =>
        {
            Record(connection, request, outcome);
            return true;
        });

    internal RequestBlock? Read(SqliteConnection connection, LookupRequest request, long since = 0)
    {
        using var command = Command(connection, request.Key);
        command.CommandText =
            "SELECT reason FROM request_blocks WHERE provider_id=$provider AND question_id=$question AND checked>=$since;";
        command.Parameters.AddWithValue("$since", since);
        if (command.ExecuteScalar() is long reason)
            return new((RequestBlockKind)reason);
        if (since > 0 || request.Counter is not { } counter)
            return null;
        command.Parameters["$provider"].Value = counter.ProviderId;
        command.Parameters["$question"].Value = counter.QuestionId;
        command.CommandText =
            "SELECT total FROM request_counts WHERE provider_id=$provider AND question_id=$question;";
        return command.ExecuteScalar() is long total && total >= Limit
            ? new(RequestBlockKind.Limit)
            : null;
    }

    // The feature commits its reusable answer and this outcome together.
    internal static void Record(
        SqliteConnection connection,
        LookupRequest request,
        RequestOutcome outcome
    )
    {
        using var command = Command(connection, request.Key);
        command.CommandText =
            outcome == RequestOutcome.Answered
                ? "DELETE FROM request_blocks WHERE provider_id=$provider AND question_id=$question;"
                : """
                    INSERT INTO request_blocks(provider_id,question_id,reason,checked) VALUES($provider,$question,$reason,$checked)
                    ON CONFLICT(provider_id,question_id) DO UPDATE SET reason=excluded.reason,checked=excluded.checked;
                    """;
        command.Parameters.AddWithValue(
            "$reason",
            (int)(
                outcome switch
                {
                    RequestOutcome.Missing => RequestBlockKind.Missing,
                    RequestOutcome.NoMatch => RequestBlockKind.NoMatch,
                    _ => RequestBlockKind.Unusable,
                }
            )
        );
        command.Parameters.AddWithValue("$checked", DateTimeOffset.UtcNow.UtcTicks);
        command.ExecuteNonQuery();
        if (outcome == RequestOutcome.Unusable || request.Counter is not { } counter)
            return;
        command.Parameters["$provider"].Value = counter.ProviderId;
        command.Parameters["$question"].Value = counter.QuestionId;
        command.CommandText = """
            INSERT INTO request_counts(provider_id,question_id,total) VALUES($provider,$question,1)
            ON CONFLICT(provider_id,question_id) DO UPDATE SET total=total+1;
            """;
        command.ExecuteNonQuery();
    }

    internal static void Reset(SqliteConnection connection, string? providerId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM request_blocks WHERE $provider IS NULL OR provider_id=$provider;
            DELETE FROM request_counts WHERE $provider IS NULL OR provider_id=$provider;
            """;
        command.Parameters.AddWithValue("$provider", (object?)providerId ?? DBNull.Value);
        command.ExecuteNonQuery();
        ClearBackoff(connection, providerId);
    }

    internal static void ClearBackoff(SqliteConnection connection, string? providerId = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM request_holds WHERE reason=$backoff AND ($provider IS NULL OR provider_id=$provider);";
        command.Parameters.AddWithValue("$provider", (object?)providerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$backoff", (int)RequestBlockKind.Backoff);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, RequestKey key)
    {
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$provider", key.ProviderId);
        command.Parameters.AddWithValue("$question", key.QuestionId);
        return command;
    }
}

internal sealed record RequestKey(string ProviderId, string QuestionId)
{
    internal static RequestKey Create(
        string providerId,
        string operation,
        IReadOnlyList<string> parameters
    ) => new(providerId, Digest(JsonSerializer.Serialize(new { operation, parameters })));

    internal static string Digest(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

internal sealed record LookupRequest(RequestKey Key, RequestKey? Counter = null);

internal sealed record RequestBlock(RequestBlockKind Kind, DateTimeOffset? RetryAt = null)
{
    internal bool CanForce =>
        Kind
            is RequestBlockKind.Missing
                or RequestBlockKind.Unusable
                or RequestBlockKind.Limit
                or RequestBlockKind.Backoff
                or RequestBlockKind.NoMatch;
}

internal sealed class RequestBlockedException(RequestBlock block) : Exception(block.Kind.ToString())
{
    internal RequestBlock Block { get; } = block;
}
