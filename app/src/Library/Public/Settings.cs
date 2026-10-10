using System.Globalization;

namespace Syno.TinyTorrent.Library.Public;

internal sealed record Settings(Website Website, int Seconds)
{
    internal IReadOnlyDictionary<string, string> Values => new Dictionary<string, string>
    {
        ["website"] = Website.WebsiteId,
        ["delay"] = Seconds.ToString(CultureInfo.InvariantCulture),
    };

    internal static Settings Read(IReadOnlyDictionary<string, string> values)
    {
        var website = values.TryGetValue("website", out var site)
            ? Provider.Websites.FirstOrDefault(website => website.WebsiteId == site) : null;
        var seconds = values.TryGetValue("delay", out var delay) &&
            int.TryParse(delay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 0, 3600) : 5;
        return new(website ?? Provider.Websites[1], seconds);
    }
}
