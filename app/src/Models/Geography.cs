using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Syno.TinyTorrent.Models;

internal readonly record struct GeoPoint(double Longitude, double Latitude);

internal static class Geography
{
    /// <summary>
    /// Reads the bundled countries. Each call reads the file again, so the
    /// caller that keeps the result decides how long its memory lives.
    /// </summary>
    internal static Task<Dictionary<string, Country>> Load() =>
        Task.Run(() =>
        {
            using var stream =
                typeof(Geography).Assembly.GetManifestResourceStream("world.bin")
                ?? throw new InvalidOperationException("The bundled world map is missing.");
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var count = reader.ReadInt32();
            Dictionary<string, Country> countries = new(count);
            for (var index = 0; index < count; index++)
            {
                var code = reader.ReadString();
                var anchor = ReadPoint(reader);
                var rings = new Vertex[reader.ReadInt32()][];
                for (var ring = 0; ring < rings.Length; ring++)
                {
                    rings[ring] = new Vertex[reader.ReadInt32()];
                    // The file stores each point as three little-endian floats
                    // in the order of Vertex's fields, so they copy straight in.
                    stream.ReadExactly(MemoryMarshal.AsBytes(rings[ring].AsSpan()));
                }
                var areas = new Area[reader.ReadInt32()];
                for (var area = 0; area < areas.Length; area++)
                    areas[area] = new Area(
                        reader.ReadString(),
                        reader.ReadInt32(),
                        ReadPoint(reader)
                    );
                countries.Add(code, new Country(code, anchor, rings, areas));
            }
            return countries;
        });

    private static GeoPoint ReadPoint(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle());

    /// <summary>
    /// A country or territory. <see cref="Code"/> is the address lookup's
    /// country code; areas the lookup never returns keep Natural Earth's code.
    /// A territory inside another country's outline, or one too small to
    /// draw, has no rings.
    /// </summary>
    internal sealed record Country(string Code, GeoPoint Anchor, Vertex[][] Rings, Area[] Areas);

    /// <summary>
    /// An outline point: longitude and latitude in degrees, and the largest
    /// error in degrees that leaving it out causes. Drawing only the points at
    /// or above one level keeps every shared border identical on both sides.
    /// Border ends and points on the 180° cut have an infinite level.
    /// </summary>
    internal readonly record struct Vertex(Vector2 Position, float Level);

    internal sealed record Area(string Name, double Population, GeoPoint Point);
}
