using System.Diagnostics;
using System.Text.Json;

namespace Syno.TinyTorrent.Shared.Storage;

/// <summary>
/// A small JSON file in the data folder that keeps state between runs, such
/// as the window layout. A missing, unreadable or damaged file reads as no
/// saved state, so the defaults apply. A save writes a temporary file and
/// renames it over the old one, so a crash never leaves half a file; two
/// saves of one file must not overlap, because they share that temporary
/// file. A failure is written to the debug output only, because the product
/// works without the state.
/// </summary>
internal sealed class StateFile<T>(string path)
    where T : class
{
    /// <summary>The saved state, or null when the file is missing or unreadable.</summary>
    internal async Task<T?> Read()
    {
        try
        {
            return JsonSerializer.Deserialize<T>(
                await File.ReadAllTextAsync(path).ConfigureAwait(false)
            );
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"{Path.GetFileName(path)} could not be read: {error.Message}");
        }
        return null;
    }

    /// <summary>Replaces the file with <paramref name="value"/>; returns whether it did.</summary>
    internal async Task<bool> Save(T value)
    {
        var text = JsonSerializer.Serialize(value);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text).ConfigureAwait(false);
            File.Move(temporary, path, true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"{Path.GetFileName(path)} could not be saved: {error.Message}");
            return false;
        }
    }

    internal void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"{Path.GetFileName(path)} could not be deleted: {error.Message}");
        }
    }
}
