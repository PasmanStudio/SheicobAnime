using System.Text;
using System.Text.RegularExpressions;

namespace AnimeIndex.Api.Infrastructure.Resolvers;

/// <summary>
/// Unpacks Dean Edwards "p,a,c,k,e,d" packed JavaScript commonly used by free video hosters
/// (Streamwish, Filemoon, Mp4Upload, etc.) to obfuscate the player.src(...) call.
/// </summary>
internal static class PackedJsUnpacker
{
    private static readonly Regex PackedRegex = new(
        @"eval\(function\(p,a,c,k,e,d?\)\{[^}]+\}\('(.+?)',(\d+),(\d+),'(.*?)'\.split\('\|'\)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Attempts to unpack one or more packed eval() blocks in the input. Returns the
    /// concatenated unpacked source, or the original input if no packed block was found.
    /// </summary>
    public static string Unpack(string input)
    {
        var matches = PackedRegex.Matches(input);
        if (matches.Count == 0) return input;

        var sb = new StringBuilder(input.Length * 2);
        sb.Append(input);
        foreach (Match m in matches)
        {
            try
            {
                var payload = m.Groups[1].Value;
                var radix = int.Parse(m.Groups[2].Value);
                // count = m.Groups[3] not strictly required for unpack, dictionary length is authoritative
                var dict = m.Groups[4].Value.Split('|');

                var unpacked = UnpackPayload(payload, radix, dict);
                sb.Append('\n').Append(unpacked);
            }
            catch
            {
                // ignore individual unpack failures — caller can still regex the original
            }
        }
        return sb.ToString();
    }

    private static string UnpackPayload(string payload, int radix, string[] dict)
    {
        // Replace each \w+ token with dict[base{radix}(token)] when valid.
        return Regex.Replace(payload, @"\b\w+\b", m =>
        {
            var token = m.Value;
            try
            {
                var idx = ParseRadix(token, radix);
                if (idx >= 0 && idx < dict.Length && dict[idx].Length > 0)
                    return dict[idx];
            }
            catch { }
            return token;
        });
    }

    private static int ParseRadix(string s, int radix)
    {
        // Dean Edwards packer uses a custom base where digits 0-9 then a-z then A-Z then aa, ab, ...
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        int value = 0;
        foreach (var c in s)
        {
            var d = alphabet.IndexOf(c);
            if (d < 0 || d >= radix) return -1;
            value = value * radix + d;
        }
        return value;
    }
}
