using System.Globalization;

namespace Syno.TinyTorrent.Torrents;

/// <summary>
/// A peer's address as the Address column orders it: IP addresses by their
/// numeric value, IPv4 before IPv6, then any other address by its ordinal
/// text.
/// </summary>
internal readonly struct PeerAddress : IComparable<PeerAddress>
{
    // The address when it is not an IP address, or null when the numbers hold it.
    private readonly string? _text;

    // The IPv6 address, high half first. IPv4 is held as its IPv4-mapped IPv6
    // address (::ffff:a.b.c.d), which sorts it before every IPv6 address a
    // remote peer can have: below that range lie only ::, the loopback ::1
    // and the deprecated IPv4-compatible form.
    private readonly ulong _high;
    private readonly ulong _low;

    /// <param name="address">A <see cref="Peer.Address"/>.</param>
    internal PeerAddress(string address)
    {
        if (!TryRead(address, out _high, out _low))
        {
            _text = address;
        }
    }

    public int CompareTo(PeerAddress other)
    {
        if (_text is not null && other._text is not null)
        {
            return string.CompareOrdinal(_text, other._text);
        }
        if (_text is not null)
        {
            return 1;
        }
        if (other._text is not null)
        {
            return -1;
        }
        var high = _high.CompareTo(other._high);
        if (high != 0)
        {
            return high;
        }
        return _low.CompareTo(other._low);
    }

    // Reads the text IPAddress.ToString writes. IPAddress.TryParse would
    // allocate an IPAddress for every row of every sort. A scope id (%5) is
    // left out, so link-local addresses that differ only by scope keep their
    // current order.
    private static bool TryRead(ReadOnlySpan<char> text, out ulong high, out ulong low)
    {
        high = 0;
        low = 0;
        if (!text.Contains(':'))
        {
            if (!TryReadIPv4(text, out var address))
            {
                return false;
            }
            low = 0xFFFF_0000_0000UL | address;
            return true;
        }
        var scope = text.IndexOf('%');
        if (scope >= 0)
        {
            text = text[..scope];
        }
        Span<ushort> groups = stackalloc ushort[8];
        var gap = text.IndexOf("::", StringComparison.Ordinal);
        if (gap < 0)
        {
            if (ReadGroups(text, groups) != groups.Length)
            {
                return false;
            }
        }
        else
        {
            Span<ushort> tail = stackalloc ushort[8];
            var head = ReadGroups(text[..gap], groups);
            var count = ReadGroups(text[(gap + 2)..], tail);
            // "::" stands for at least one zero group.
            if (head < 0 || count < 0 || head + count > groups.Length - 1)
            {
                return false;
            }
            tail[..count].CopyTo(groups[(groups.Length - count)..]);
        }
        foreach (var group in groups[..4])
        {
            high = high << 16 | group;
        }
        foreach (var group in groups[4..])
        {
            low = low << 16 | group;
        }
        return true;
    }

    // Reads colon-separated hex groups and returns how many it read, or -1
    // when the text is not groups. A dotted IPv4 address may end them, as two
    // groups.
    private static int ReadGroups(ReadOnlySpan<char> text, Span<ushort> groups)
    {
        if (text.IsEmpty)
        {
            return 0;
        }
        var count = 0;
        foreach (var range in text.Split(':'))
        {
            var part = text[range];
            if (part.Contains('.'))
            {
                if (
                    range.End.Value != text.Length
                    || count > groups.Length - 2
                    || !TryReadIPv4(part, out var address)
                )
                {
                    return -1;
                }
                groups[count] = (ushort)(address >> 16);
                groups[count + 1] = (ushort)address;
                count += 2;
                continue;
            }
            if (
                count == groups.Length
                || part.Length is 0 or > 4
                || !ushort.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            )
            {
                return -1;
            }
            groups[count] = value;
            count++;
        }
        return count;
    }

    // Reads four decimal bytes. Legacy short forms such as "1.2", which
    // IPAddress.ToString never writes, stay text.
    private static bool TryReadIPv4(ReadOnlySpan<char> text, out uint address)
    {
        address = 0;
        var count = 0;
        foreach (var range in text.Split('.'))
        {
            if (
                count == 4
                || !byte.TryParse(text[range], NumberStyles.None, CultureInfo.InvariantCulture, out var part)
            )
            {
                return false;
            }
            address = address << 8 | part;
            count++;
        }
        return count == 4;
    }
}
