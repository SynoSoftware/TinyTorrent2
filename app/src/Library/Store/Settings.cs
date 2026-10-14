using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Store
{
    internal Task<bool> AutomaticDownload(CancellationToken cancellation) =>
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

    internal Task SelectProvider(string providerId, CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE library_settings SET source=$provider WHERE singleton=1;";
                command.Parameters.AddWithValue("$provider", providerId);
                return command.ExecuteNonQuery();
            },
            cancellation
        );

    internal Task EnableAutomaticDownload(string providerId, CancellationToken cancellation) =>
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

    /// <summary>
    /// The person's own API keys by provider. A key another Windows user or
    /// computer saved, or a damaged one, cannot be read, so it is removed and
    /// the provider uses its built-in key again.
    /// </summary>
    internal Task<Dictionary<string, string>> Keys(CancellationToken cancellation) =>
        database.Run(
            connection =>
            {
                var keys = new Dictionary<string, string>();
                var unreadable = new List<string>();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT provider_id,secret FROM provider_keys;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        try
                        {
                            keys[reader.GetString(0)] = reader[1] is byte[] secret
                                ? Credentials.Unprotect(secret)
                                : throw new CryptographicException();
                        }
                        catch (CryptographicException)
                        {
                            unreadable.Add(reader.GetString(0));
                        }
                    }
                }
                command.CommandText = "DELETE FROM provider_keys WHERE provider_id=$provider;";
                var provider = command.Parameters.Add("$provider", SqliteType.Text);
                foreach (var providerId in unreadable)
                {
                    provider.Value = providerId;
                    command.ExecuteNonQuery();
                }
                return keys;
            },
            cancellation
        );

    /// <summary>Saves the person's own API key, or removes it when <paramref name="key"/> is null.</summary>
    internal Task SaveKey(string providerId, string? key, CancellationToken cancellation)
    {
        var secret = key is null ? null : Credentials.Protect(key);
        return database.Run(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = secret is null
                    ? "DELETE FROM provider_keys WHERE provider_id=$provider;"
                    : "INSERT INTO provider_keys VALUES($provider,$secret) "
                        + "ON CONFLICT(provider_id) DO UPDATE SET secret=excluded.secret;";
                command.Parameters.AddWithValue("$provider", providerId);
                if (secret is not null)
                    command.Parameters.AddWithValue("$secret", secret);
                return command.ExecuteNonQuery();
            },
            cancellation
        );
    }

    internal Task DisableAutomaticDownload(CancellationToken cancellation) =>
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
