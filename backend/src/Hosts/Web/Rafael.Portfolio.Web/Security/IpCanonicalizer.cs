using System.Globalization;
using System.Text;

namespace Rafael.Portfolio.Web.Security;

/// <summary>
/// Strict exact-IP canonicalization, the same algorithm as the frontend
/// (<c>src/modules/security/ip.ts</c>); both are tested against
/// <c>docs/contracts/visitor-identity-v2.json</c>. Only exact IPv4 and IPv6 addresses are
/// accepted: no brackets, ports, zone ids, CIDR suffixes, wildcards, whitespace, leading
/// zeros or non-decimal IPv4 forms. An IPv4-mapped IPv6 address canonicalizes to its IPv4
/// form so one host keeps one identity.
/// </summary>
internal static class IpCanonicalizer
{
    private const int MaxLength = 45;

    public static bool TryCanonicalize(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        if (TryParseIPv4(value, out var octets))
        {
            canonical = string.Join('.', octets);
            return true;
        }

        if (!TryParseIPv6(value, out var groups))
        {
            return false;
        }

        if (groups[..5].All(group => group == 0) && groups[5] == 0xffff)
        {
            canonical = $"{groups[6] >> 8}.{groups[6] & 0xff}.{groups[7] >> 8}.{groups[7] & 0xff}";
            return true;
        }

        canonical = FormatIPv6(groups);
        return true;
    }

    private static bool TryParseIPv4(string value, out int[] octets)
    {
        octets = [];
        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var parsed = new int[4];
        for (var index = 0; index < 4; index++)
        {
            var part = parts[index];
            if (part.Length is < 1 or > 3 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            parsed[index] = int.Parse(part, CultureInfo.InvariantCulture);
            if (parsed[index] > 255)
            {
                return false;
            }
        }

        octets = parsed;
        return true;
    }

    private static bool TryParseIPv6(string value, out int[] groups)
    {
        groups = [];
        if (!value.Contains(':') || !value.All(character => char.IsAsciiHexDigit(character) || character is ':' or '.'))
        {
            return false;
        }

        var halves = value.Split("::");
        if (halves.Length > 2)
        {
            return false;
        }

        var compressed = halves.Length == 2;
        if (!TryExpand(halves[0], allowIPv4Tail: !compressed, out var head) ||
            !TryExpand(compressed ? halves[1] : string.Empty, allowIPv4Tail: true, out var tail))
        {
            return false;
        }

        if (!compressed)
        {
            if (head.Count != 8)
            {
                return false;
            }

            groups = [.. head];
            return true;
        }

        if (head.Count + tail.Count > 7)
        {
            return false;
        }

        var all = new List<int>(8);
        all.AddRange(head);
        all.AddRange(Enumerable.Repeat(0, 8 - head.Count - tail.Count));
        all.AddRange(tail);
        groups = [.. all];
        return true;
    }

    private static bool TryExpand(string part, bool allowIPv4Tail, out List<int> groups)
    {
        groups = [];
        if (part.Length == 0)
        {
            return true;
        }

        var tokens = part.Split(':');
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Contains('.'))
            {
                if (!allowIPv4Tail || index != tokens.Length - 1 || !TryParseIPv4(token, out var octets))
                {
                    return false;
                }

                groups.Add((octets[0] << 8) | octets[1]);
                groups.Add((octets[2] << 8) | octets[3]);
            }
            else
            {
                if (token.Length is < 1 or > 4 || !token.All(char.IsAsciiHexDigit))
                {
                    return false;
                }

                groups.Add(int.Parse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            }
        }

        return true;
    }

    private static string FormatIPv6(int[] groups)
    {
        var bestStart = -1;
        var bestLength = 0;
        for (var index = 0; index < 8;)
        {
            if (groups[index] != 0)
            {
                index++;
                continue;
            }

            var end = index;
            while (end < 8 && groups[end] == 0)
            {
                end++;
            }

            if (end - index > bestLength)
            {
                bestStart = index;
                bestLength = end - index;
            }

            index = end;
        }

        static string Hex(int group) => group.ToString("x", CultureInfo.InvariantCulture);

        if (bestLength < 2)
        {
            return string.Join(':', groups.Select(Hex));
        }

        var builder = new StringBuilder();
        builder.Append(string.Join(':', groups.Take(bestStart).Select(Hex)));
        builder.Append("::");
        builder.Append(string.Join(':', groups.Skip(bestStart + bestLength).Select(Hex)));
        return builder.ToString();
    }
}
