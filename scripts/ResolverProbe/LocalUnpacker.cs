using System.Text;
using System.Text.RegularExpressions;

internal static class LocalUnpacker
{
    private static readonly Regex Rx = new(
        @"eval\(function\(p,a,c,k,e,d?\)\{[^}]+\}\('(.+?)',(\d+),(\d+),'(.*?)'\.split\('\|'\)",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Unpack(string input)
    {
        var matches = Rx.Matches(input);
        if (matches.Count == 0) return input;
        var sb = new StringBuilder(input.Length * 2);
        sb.Append(input);
        foreach (Match m in matches)
        {
            try
            {
                var payload = m.Groups[1].Value;
                var radix = int.Parse(m.Groups[2].Value);
                var dict = m.Groups[4].Value.Split('|');
                var unpacked = Regex.Replace(payload, @"\b\w+\b", mm =>
                {
                    var token = mm.Value;
                    int value = 0;
                    foreach (var c in token)
                    {
                        var d = Alphabet.IndexOf(c);
                        if (d < 0 || d >= radix) return token;
                        value = value * radix + d;
                    }
                    return (value >= 0 && value < dict.Length && dict[value].Length > 0)
                        ? dict[value] : token;
                });
                sb.Append('\n').Append(unpacked);
            }
            catch { }
        }
        return sb.ToString();
    }
}
