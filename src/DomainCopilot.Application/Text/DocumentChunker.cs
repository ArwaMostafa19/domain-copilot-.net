using DomainCopilot.Domain.Documents;

namespace DomainCopilot.Application.Text;

public static class DocumentChunker
{
    public const int DefaultMaxChars = 1800;
    public const string SafetySectionTitle = "Safety prerequisites";
    public const int DEFAULT_MAX_CHARS = DefaultMaxChars;
    public const string SAFETY_SECTION_TITLE = SafetySectionTitle;
    public const string BLOCK_SEPARATOR = "\n\n";
    public static IReadOnlyList<Chunk> Chunk(ExtractedDocument doc, int maxChars = DefaultMaxChars)
    {
        var chunks = new List<Chunk>();
        foreach (var section in doc.Sections)
        {
            var text = CleanText.Clean(section.Text);
            if (text.Length == 0) continue;
            foreach (var piece in Pack(section.Title, text, maxChars))
                chunks.Add(new Chunk(chunks.Count, section.Title, $"{doc.Meta.Title} | {section.Title} | {doc.Meta.Revision}", piece, doc.Meta.Revision, section.Page));
        }
        return chunks;
    }
    private static IReadOnlyList<string> Pack(string title, string text, int maxChars)
    {
        if (title == SafetySectionTitle) return [text];
        var packed = new List<string>(); var current = new List<string>(); var length = 0;
        foreach (var block in text.Split("\n\n", StringSplitOptions.None))
        {
            var addition = PythonString.CodePointLength(block) + (current.Count == 0 ? 0 : 2);
            if (current.Count > 0 && length + addition > maxChars)
            { packed.Add(string.Join("\n\n", current)); current.Clear(); length = 0; addition = PythonString.CodePointLength(block); }
            current.Add(block); length += addition;
        }
        if (current.Count > 0) packed.Add(string.Join("\n\n", current));
        return packed;
    }
}
