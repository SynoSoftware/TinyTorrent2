namespace Syno.TinyTorrent.Library;

internal sealed partial class Store
{
    internal Task<bool> Enabled(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT enabled FROM library_settings WHERE singleton=1;";
                var value = command.ExecuteScalar();
                if (value is long enabled && enabled is 0 or 1)
                    return enabled == 1;
                command.CommandText = "UPDATE library_settings SET enabled=0 WHERE singleton=1;";
                command.ExecuteNonQuery();
                return false;
            },
            cancellation
        );

    internal Task<string?> Provider(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT source FROM library_settings WHERE singleton=1;";
                return command.ExecuteScalar() as string;
            },
            cancellation
        );

    internal Task EnableProvider(string providerId, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE library_settings SET source=$provider,enabled=1 WHERE singleton=1;";
                command.Parameters.AddWithValue("$provider", providerId);
                return command.ExecuteNonQuery();
            },
            cancellation
        );

    internal const int DefaultLookupLimit = 5;

    // How many searches automatic and batch work may make for one movie at one
    // provider or language; Library and subtitles share it.
    internal Task<int> LookupLimit(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT lookups FROM library_settings WHERE singleton=1;";
                if (command.ExecuteScalar() is long limit && limit is >= 1 and <= int.MaxValue)
                    return (int)limit;
                command.CommandText = "UPDATE library_settings SET lookups=$limit WHERE singleton=1;";
                command.Parameters.AddWithValue("$limit", DefaultLookupLimit);
                command.ExecuteNonQuery();
                return DefaultLookupLimit;
            },
            cancellation
        );

    internal Task SaveLookupLimit(int limit, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE library_settings SET lookups=$limit WHERE singleton=1;";
                command.Parameters.AddWithValue("$limit", limit);
                return command.ExecuteNonQuery();
            },
            cancellation
        );

    internal Task DisableProvider(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE library_settings SET enabled=0 WHERE singleton=1;";
                return command.ExecuteNonQuery();
            },
            cancellation
        );
}
