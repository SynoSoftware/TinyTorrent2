using System.Text.Json;

namespace Syno.TinyTorrent.Shared.Engine;

internal sealed partial class PipeClient
{
    // A torrent's payload files with their paths.
    internal IAsyncEnumerable<DetailFile> TorrentFiles(string torrentId) =>
        Files(
            async offset =>
                await Read("files", new { torrent_id = torrentId, offset }).ConfigureAwait(false),
            DetailFile.Read
        );

    // The pieces of a torrent's payload files, read without their paths, so
    // the names of a large torrent are never sent.
    internal IAsyncEnumerable<PieceRange> TorrentPieces(string torrentId) =>
        Files(
            async offset =>
                await Read(
                        "files",
                        new { torrent_id = torrentId, offset, paths = false }
                    )
                    .ConfigureAwait(false),
            PieceRange.Read
        );

    // A torrent's file count, padding files included, which a page past its
    // last file carries with no files.
    internal async Task<int> FileCount(string torrentId) =>
        (
            await Read(
                    "files",
                    new { torrent_id = torrentId, offset = int.MaxValue, paths = false }
                )
                .ConfigureAwait(false)
        )
            .GetProperty("file_count")
            .GetInt32();

    // A torrent's payload files, page by page. `read` gives the page at an
    // offset, or null to end the files early. The next page is requested only
    // after the caller has taken every file of the current one.
    private static async IAsyncEnumerable<T> Files<T>(
        Func<int, Task<JsonElement?>> read,
        Func<JsonElement, T> parse
    )
    {
        var offset = 0;
        while (await read(offset).ConfigureAwait(false) is { } page)
        {
            var files = page.GetProperty("files");
            foreach (var file in files.EnumerateArray())
                if (!file.GetProperty("padding").GetBoolean())
                    yield return parse(file);
            offset += files.GetArrayLength();
            if (files.GetArrayLength() == 0 || offset >= page.GetProperty("file_count").GetInt32())
                yield break;
        }
    }

    // Null when the pipe could not carry a read: the connection closed, or too
    // many commands waited. The caller asks again with the next snapshot. The
    // pieces are read only when the torrent has wanted
    // bytes present (`completed`), because only then can a file be verified.
    // A torrent without metadata gives an empty list, which is complete and
    // does not hold back cleanup that waits for unread lists. The engine
    // answers these reads beside the inspector's, so they wait for no view.
    internal async Task<FileDetail?> ReadFiles(
        string torrentId,
        bool completed,
        CancellationToken cancellation
    )
    {
        try
        {
            List<DetailFile> files = [];
            var payload = Files(
                async offset =>
                    await Read("files", new { torrent_id = torrentId, offset })
                        .WaitAsync(cancellation)
                        .ConfigureAwait(false),
                DetailFile.Read
            );
            await foreach (var file in payload.ConfigureAwait(false))
                files.Add(file);
            bool[]? verified = null;
            if (completed && files.Count > 0)
            {
                // A read without a context is answered once its facts are complete.
                var pieces = await Read("torrent", new { torrent_id = torrentId, view = "pieces" })
                    .WaitAsync(cancellation)
                    .ConfigureAwait(false);
                verified =
                [
                    .. pieces.GetProperty("verified").EnumerateArray().Select(bit => bit.GetBoolean()),
                ];
            }
            return new FileDetail(files, verified);
        }
        // The connection's own reads are cancelled before it reports itself
        // closed, so a cancellation the caller did not ask for is a closed pipe.
        catch (Exception error)
            when (error is IOException or OperationCanceledException
                && !cancellation.IsCancellationRequested
            )
        {
            return null;
        }
    }
}

// The payload files of a torrent as the engine's file pages describe them.
// Verified holds the torrent's verified pieces; it is null when the torrent had
// no wanted bytes present, so the pieces were not read.
internal sealed record FileDetail(IReadOnlyList<DetailFile> Files, bool[]? Verified);

// DiskPath is the file's current name on disk, or null until the engine has
// read the names.
internal sealed record DetailFile(
    int Index,
    string Path,
    string? DiskPath,
    long Size,
    PieceRange Pieces,
    int Priority
)
{
    internal static DetailFile Read(JsonElement file) =>
        new(
            file.GetProperty("index").GetInt32(),
            file.GetProperty("path").GetString()!,
            file.GetProperty("disk_path").GetString(),
            file.GetProperty("size").GetInt64(),
            PieceRange.Read(file),
            file.GetProperty("priority").GetInt32()
        );
}

// A file's pieces, End exclusive.
internal readonly record struct PieceRange(int First, int End)
{
    internal static PieceRange Read(JsonElement file) =>
        new(file.GetProperty("first_piece").GetInt32(), file.GetProperty("end_piece").GetInt32());
}
