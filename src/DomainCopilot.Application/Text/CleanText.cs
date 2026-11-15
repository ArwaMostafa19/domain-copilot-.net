using System.Text.RegularExpressions;

namespace DomainCopilot.Application.Text;

public static class CleanText
{
    public const string NON_BREAKING_SPACE = "\u00a0";
    private static readonly Regex ExtraBlankLines = new("\\n{3,}", RegexOptions.CultureInvariant);
    public static string Clean(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00a0', ' ');
        var lines = normalized.Split('\n').Select(PythonString.TrimEnd).ToArray();
        return ExtraBlankLines.Replace(string.Join('\n', lines), "\n\n").Trim('\n');
    }
}
