using System.Text;

namespace DomainCopilot.Infrastructure.Configuration;

/// <summary>Loads a workspace .env file without replacing explicitly supplied process variables.</summary>
public static class DotEnvLoader
{
    public static void LoadWorkspaceFile()
    {
        var path = FindWorkspaceFile();
        if (path is not null) LoadFile(path);
    }

    public static string? FindWorkspaceFile()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                var dotenv = Path.Combine(directory.FullName, ".env");
                if (File.Exists(dotenv) && File.Exists(Path.Combine(directory.FullName, "DomainCopilot.sln")))
                    return dotenv;
                directory = directory.Parent;
            }
        }

        foreach (var direct in new[] { Path.Combine(Environment.CurrentDirectory, ".env"), Path.Combine(AppContext.BaseDirectory, ".env") })
            if (File.Exists(direct)) return direct;
        return null;
    }

    public static IReadOnlyList<string> LoadFile(string path) =>
        LoadContent(File.ReadAllText(path, Encoding.UTF8));

    public static IReadOnlyList<string> LoadContent(string contents)
    {
        var loaded = new List<string>();
        foreach (var pair in Parse(contents))
        {
            // An explicitly present process variable, even an empty one, has priority.
            if (Environment.GetEnvironmentVariable(pair.Key) is not null) continue;
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            loaded.Add(pair.Key);
        }
        return loaded;
    }

    public static IReadOnlyDictionary<string, string> Parse(string contents)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(contents);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith("export ", StringComparison.Ordinal)) trimmed = trimmed[7..].TrimStart();

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
                throw new FormatException($".env line {lineNumber} must contain KEY=VALUE.");
            var key = trimmed[..separator].Trim();
            if (!IsValidKey(key))
                throw new FormatException($".env line {lineNumber} has an invalid variable name.");

            var raw = trimmed[(separator + 1)..].Trim();
            values[key] = ParseValue(raw, lineNumber);
        }
        return values;
    }

    private static string ParseValue(string raw, int lineNumber)
    {
        if (raw.Length == 0) return string.Empty;
        if (raw[0] is '"' or '\'')
        {
            var quote = raw[0];
            var closing = FindClosingQuote(raw, quote);
            if (closing < 0)
                throw new FormatException($".env line {lineNumber} has an unterminated quoted value.");
            var suffix = raw[(closing + 1)..].Trim();
            if (suffix.Length > 0 && !suffix.StartsWith('#'))
                throw new FormatException($".env line {lineNumber} has unexpected text after a quoted value.");
            var value = raw[1..closing];
            return quote == '"' ? UnescapeDoubleQuoted(value) : value;
        }

        for (var index = 0; index < raw.Length; index++)
            if (raw[index] == '#' && (index == 0 || char.IsWhiteSpace(raw[index - 1])))
                return raw[..index].TrimEnd();
        return raw.Trim();
    }

    private static int FindClosingQuote(string value, char quote)
    {
        for (var index = 1; index < value.Length; index++)
        {
            if (quote == '"' && value[index] == '\\') { index++; continue; }
            if (value[index] == quote) return index;
        }
        return -1;
    }

    private static string UnescapeDoubleQuoted(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length) { result.Append(value[index]); continue; }
            var next = value[++index];
            result.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => '\\' });
            if (next is not ('n' or 'r' or 't' or '"' or '\\')) result.Append(next);
        }
        return result.ToString();
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0 || !(char.IsAsciiLetter(key[0]) || key[0] == '_')) return false;
        return key.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
}
