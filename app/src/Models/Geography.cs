using System.Globalization;
using System.Text.Json;

namespace Syno.TinyTorrent.Models;

internal readonly record struct GeoPoint(double Longitude, double Latitude);

internal static class Geography
{
    internal static Task<Dictionary<string, Country>> Countries { get; } =
        Task.Run(() =>
        {
            using var stream =
                typeof(Geography).Assembly.GetManifestResourceStream("world.json")
                ?? throw new InvalidOperationException("The bundled world map is missing.");
            var countries =
                JsonSerializer.Deserialize<Country[]>(stream)
                ?? throw new InvalidDataException("The bundled world map is empty.");
            return countries.ToDictionary(country => country.Code);
        });

    internal static string Code(string code)
    {
        if (code == "XK")
            return "KOS";
        try
        {
            return new RegionInfo(code).ThreeLetterISORegionName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    internal sealed record Country(string Code, GeoPoint Anchor, GeoPoint[][] Rings, Area[] Areas);

    internal sealed record Area(string Name, double Population, GeoPoint Point);
}
