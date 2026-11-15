using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DomainCopilot.Application;
using DomainCopilot.Domain.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DomainCopilot.Infrastructure.Extraction;

/// <summary>Reads the original Markdown corpus format and PDF files with sibling Markdown metadata.</summary>
public sealed class FileSystemDocumentExtractor : IDocumentExtractor
{
    private const string ManifestName = "manifest.json";
    private static readonly string[] RequiredKeys = ["tenant", "doc_id", "title", "revision", "status"];
    private readonly string _repositoryRoot;

    public FileSystemDocumentExtractor(string? repositoryRoot = null) =>
        _repositoryRoot = Path.GetFullPath(repositoryRoot ?? FindRepositoryRoot());

    public ExtractedDocument Extract(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new ExtractionError($"source file does not exist: {path}");
        return file.Extension.ToLowerInvariant() switch
        {
            ".md" => ExtractMarkdown(file.FullName),
            ".pdf" => ExtractPdf(file.FullName),
            _ => throw new ExtractionError($"unsupported source format '{file.Extension.ToLowerInvariant()}': {path}")
        };
    }

    private ExtractedDocument ExtractMarkdown(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException error) { throw new ExtractionError($"could not decode Markdown as UTF-8: {path}: {error.Message}"); }

        var lines = text.Split('\n');
        var values = FrontMatter(path, lines);
        var body = DropLeadingTables(path, lines.Skip(FrontMatterEnd(lines) + 1).ToArray());
        var sections = MarkdownSections(body, values["title"]);
        var meta = new DocumentMeta(values["tenant"], values["doc_id"], values["title"], values["revision"], values["status"],
            "markdown", SourcePath(path), Convert.ToHexStringLower(SHA256.HashData(bytes)),
            values.GetValueOrDefault("equipment") is { Length: > 0 } equipment ? equipment : null,
            values.GetValueOrDefault("document_type") is { Length: > 0 } type ? type : null);
        return new ExtractedDocument(meta, sections.ToImmutableArray());
    }

    private ExtractedDocument ExtractPdf(string path)
    {
        var sibling = Path.ChangeExtension(path, ".md");
        if (!File.Exists(sibling)) throw new ExtractionError($"PDF has no sibling Markdown file for its metadata: {path}");
        var source = ExtractMarkdown(sibling);
        var bytes = File.ReadAllBytes(path);
        var pages = ReadPdfPages(path, bytes);
        var titles = ManifestSectionTitles(path);
        var sections = PdfSections(pages, titles, source.Meta.Title);
        var meta = source.Meta with
        {
            SourceFormat = "pdf",
            SourcePath = SourcePath(path),
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
        return new ExtractedDocument(meta, sections.ToImmutableArray());
    }

    private static int FrontMatterEnd(string[] lines)
    {
        var delimiters = 0;
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Trim() == "---" && ++delimiters == 2) return i;
        throw new ExtractionError("unterminated front matter block");
    }

    private static Dictionary<string, string> FrontMatter(string path, string[] lines)
    {
        var start = Array.FindIndex(lines, line => line.Trim() == "---");
        if (start < 0) throw new ExtractionError($"no front matter delimiter found: {path}");
        var end = -1;
        for (var i = start + 1; i < lines.Length; i++)
            if (lines[i].Trim() == "---") { end = i; break; }
        if (end < 0) throw new ExtractionError("unterminated front matter block");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"').Trim();
            values[key] = value;
        }
        var missing = RequiredKeys.Where(key => !values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)).ToArray();
        if (missing.Length > 0) throw new ExtractionError($"front matter is missing required key(s) {string.Join(", ", missing)}: {path}");
        return values;
    }

    private static string[] DropLeadingTables(string path, string[] lines)
    {
        var index = 0;
        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
        if (index < lines.Length && lines[index].StartsWith("# ", StringComparison.Ordinal)) index++;
        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
        while (index < lines.Length && lines[index].StartsWith('|')) index++;
        if (index >= lines.Length) throw new ExtractionError($"no document body found below the front matter: {path}");
        return lines.Skip(index).ToArray();
    }

    private static List<Section> MarkdownSections(string[] lines, string documentTitle)
    {
        var result = new List<Section>();
        var title = documentTitle;
        var buffer = new List<string>();
        void Close()
        {
            var content = string.Join('\n', buffer);
            if (!string.IsNullOrWhiteSpace(content)) result.Add(new Section(title, content));
        }
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                title = line[3..].Trim();
                buffer.Clear();
            }
            else buffer.Add(line);
        }
        Close();
        return result;
    }

    private static List<string> ReadPdfPages(string path, byte[] bytes)
    {
        try
        {
            using var document = PdfDocument.Open(new MemoryStream(bytes));
            var pages = document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)).ToList();
            if (!pages.Any(page => !string.IsNullOrWhiteSpace(page)))
                throw new ExtractionError($"PDF has no extractable text: {path}");
            return pages;
        }
        catch (ExtractionError) { throw; }
        catch (Exception error) { throw new ExtractionError($"could not read PDF {path}: {error.Message}"); }
    }

    private List<Section> PdfSections(IReadOnlyList<string> pages, IReadOnlyList<string> titles, string documentTitle)
    {
        var known = titles.ToHashSet(StringComparer.Ordinal);
        if (known.Count == 0)
            return pages.Select((text, index) => (text, index))
                .Where(x => !string.IsNullOrWhiteSpace(x.text))
                .Select(x => new Section($"Page {x.index + 1}", x.text, x.index + 1)).ToList();

        var sections = new List<Section>();
        var title = documentTitle;
        var pageNumber = 1;
        var buffer = new List<string>();
        for (var index = 0; index < pages.Count; index++)
        {
            foreach (var line in pages[index].Split('\n'))
            {
                var stripped = line.Trim();
                if (known.Contains(stripped))
                {
                    var content = string.Join('\n', buffer);
                    if (!string.IsNullOrWhiteSpace(content)) sections.Add(new Section(title, content, pageNumber));
                    title = stripped;
                    pageNumber = index + 1;
                    buffer.Clear();
                }
                else buffer.Add(line);
            }
        }
        var trailing = string.Join('\n', buffer);
        if (!string.IsNullOrWhiteSpace(trailing)) sections.Add(new Section(title, trailing, pageNumber));
        return sections;
    }

    private IReadOnlyList<string> ManifestSectionTitles(string path)
    {
        var manifestPath = FindManifest(path);
        if (manifestPath is null) return Array.Empty<string>();
        JsonDocument manifest;
        try { manifest = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)); }
        catch (Exception error) when (error is IOException or JsonException) { return Array.Empty<string>(); }
        using (manifest)
        {
            if (!TryArray(manifest.RootElement, "documents", out var documents)) return Array.Empty<string>();
            var documentId = ManifestDocumentId(path, Path.GetDirectoryName(manifestPath)!, manifest.RootElement, documents);
            if (documentId is null) return Array.Empty<string>();
            foreach (var entry in documents.EnumerateArray())
            {
                if (String(entry, "doc_id") != documentId || !TryArray(entry, "sections", out var sections)) continue;
                return sections.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString()).ToArray();
            }
            return Array.Empty<string>();
        }
    }

    private string? ManifestDocumentId(string pdfPath, string folder, JsonElement root, JsonElement documents)
    {
        var pdfRelative = RelativeTo(pdfPath, folder);
        if (TryArray(root, "format_samples", out var samples))
            foreach (var sample in samples.EnumerateArray())
                if (String(sample, "path") == pdfRelative && String(sample, "doc_id") is { Length: > 0 } id) return id;
        var markdownRelative = RelativeTo(Path.ChangeExtension(pdfPath, ".md"), folder);
        foreach (var entry in documents.EnumerateArray())
            if (String(entry, "path") == markdownRelative && String(entry, "doc_id") is { Length: > 0 } id) return id;
        return null;
    }

    private static bool TryArray(JsonElement element, string name, out JsonElement values)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Array)
        { values = found; return true; }
        values = default;
        return false;
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? FindManifest(string path)
    {
        var folder = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (folder is not null)
        {
            var candidate = Path.Combine(folder.FullName, ManifestName);
            if (File.Exists(candidate)) return candidate;
            folder = folder.Parent;
        }
        return null;
    }

    private string SourcePath(string path) => RelativeTo(path, _repositoryRoot);

    private static string RelativeTo(string path, string folder)
    {
        var fullPath = Path.GetFullPath(path);
        var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(folder, fullPath).Replace('\\', '/')
            : fullPath.Replace('\\', '/');
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "migrations"))) return current.FullName;
            current = current.Parent;
        }
        return Environment.CurrentDirectory;
    }
}


