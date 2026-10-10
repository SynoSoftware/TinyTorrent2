using Microsoft.Data.Sqlite;

namespace Syno.TinyTorrent.Library;

internal sealed partial class Store
{
    internal Task<bool> Enabled(CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM library_settings WHERE setting='enabled';";
        return command.ExecuteScalar() as string == "1";
    }, cancellation);

    internal Task SetEnabled(bool enabled, CancellationToken cancellation) => SaveSetting("enabled", enabled ? "1" : "0", cancellation);

    internal Task<string?> Provider(CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM library_settings WHERE setting='source';";
        return command.ExecuteScalar() as string;
    }, cancellation);

    internal Task SetProvider(string providerId, CancellationToken cancellation) => SaveSetting("source", providerId, cancellation);

    private Task SaveSetting(string setting, string value, CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO library_settings VALUES ($setting,$value) " +
            "ON CONFLICT(setting) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$setting", setting);
        command.Parameters.AddWithValue("$value", value);
        return command.ExecuteNonQuery();
    }, cancellation);

    internal Task<IReadOnlyDictionary<string, string>> Settings(string providerId, CancellationToken cancellation) =>
        database.Run<IReadOnlyDictionary<string, string>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT setting,value FROM provider_settings WHERE section=$section;";
            command.Parameters.AddWithValue("$section", providerId);
            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                settings.Add(reader.GetString(0), reader.GetString(1));
            return settings;
        }, cancellation);

    internal Task SaveSettings(string providerId, IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellation) => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM provider_settings WHERE section=$section;";
        command.Parameters.AddWithValue("$section", providerId);
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO provider_settings VALUES ($section,$setting,$value);";
        command.Parameters.Add("$setting", SqliteType.Text);
        command.Parameters.Add("$value", SqliteType.Text);
        foreach (var (setting, value) in settings)
        {
            cancellation.ThrowIfCancellationRequested();
            command.Parameters["$setting"].Value = setting;
            command.Parameters["$value"].Value = value;
            command.ExecuteNonQuery();
        }
        return true;
    }, cancellation);
}
