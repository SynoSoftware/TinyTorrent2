using System.Reflection;

namespace Syno.TinyTorrent.Shared.Network;

/// <summary>The API keys that the build writes into the release.</summary>
internal static class ApiKey
{
    /// <summary>
    /// The key the build wrote under <paramref name="name"/>, or null when
    /// this build has none.
    /// </summary>
    internal static string? BuiltIn(string name) =>
        typeof(ApiKey)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(value => value.Key == name)
            ?.Value
            is { } key
        && !string.IsNullOrWhiteSpace(key)
            ? key
            : null;
}
