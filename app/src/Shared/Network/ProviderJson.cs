using System.Text.Json;

namespace Syno.TinyTorrent.Shared.Network;

/// <summary>
/// Reads values from a provider's JSON answer. A property that is missing or
/// holds another kind of value is no value, so a reader needs no shape checks.
/// </summary>
internal static class ProviderJson
{
    /// <summary>
    /// The property's text without surrounding white space, or null when the
    /// element has no such text or the text is blank.
    /// </summary>
    internal static string? Text(JsonElement element, string property) =>
        Child(element, property) is { ValueKind: JsonValueKind.String } value
        && value.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : null;

    internal static long? Number(JsonElement element, string property) =>
        Child(element, property) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>
    /// The property's value, or an undefined element when the element has
    /// none, so nested reads need no checks.
    /// </summary>
    internal static JsonElement Child(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var child)
            ? child
            : default;
}
