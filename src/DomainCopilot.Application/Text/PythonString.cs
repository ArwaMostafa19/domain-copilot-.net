using System.Text;

namespace DomainCopilot.Application.Text;

public static class PythonString
{
    public static bool IsWhitespace(char value) => char.IsWhiteSpace(value) || value is >= '\u001c' and <= '\u001f';
    public static string Strip(string value)
    {
        var start = 0; var end = value.Length;
        while (start < end && IsWhitespace(value[start])) start++;
        while (end > start && IsWhitespace(value[end - 1])) end--;
        return value[start..end];
    }
    public static string TrimEnd(string value)
    {
        var end = value.Length;
        while (end > 0 && IsWhitespace(value[end - 1])) end--;
        return value[..end];
    }
    public static bool IsBlank(string value) => Strip(value).Length == 0;
    public static int CodePointLength(string value) => value.EnumerateRunes().Count();
    public static string Slice(string value, int count) => string.Concat(value.EnumerateRunes().Take(Math.Max(0, count)).Select(r => r.ToString()));
    public static string Repr(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
    public static string CaseFold(string value) => value.ToLowerInvariant()
        .Replace("ß", "ss", StringComparison.Ordinal).Replace("ς", "σ", StringComparison.Ordinal)
        .Replace("ſ", "s", StringComparison.Ordinal).Replace("K", "k", StringComparison.Ordinal)
        .Replace("µ", "μ", StringComparison.Ordinal).Replace("ﬀ", "ff", StringComparison.Ordinal)
        .Replace("ﬁ", "fi", StringComparison.Ordinal).Replace("ﬂ", "fl", StringComparison.Ordinal)
        .Replace("ﬃ", "ffi", StringComparison.Ordinal).Replace("ﬄ", "ffl", StringComparison.Ordinal)
        .Replace("ﬅ", "st", StringComparison.Ordinal).Replace("ﬆ", "st", StringComparison.Ordinal);
}
