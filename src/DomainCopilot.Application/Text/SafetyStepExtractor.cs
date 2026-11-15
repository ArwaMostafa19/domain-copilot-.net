using System.Text.RegularExpressions;
using DomainCopilot.Domain.Documents;

namespace DomainCopilot.Application.Text;

public static class SafetyStepExtractor
{
    public const string SAFETY_SECTION_TITLE = "Safety prerequisites";
    public const string BLOCK_SEPARATOR = "\n\n";
    private static readonly Regex Numbered = new("^(\\d+)\\.[ \\t]*(.*)$", RegexOptions.CultureInvariant);
    public static Regex NUMBERED_ITEM => Numbered;
    public static IReadOnlyList<SafetyStep> Extract(ExtractedDocument document)
    {
        var steps = new List<SafetyStep>();
        foreach (var section in document.Sections)
        {
            if (section.Title != DocumentChunker.SafetySectionTitle) continue;
            steps.AddRange(Parse(CleanText.Clean(section.Text)));
        }
        var seen = new HashSet<int>();
        foreach (var step in steps) if (!seen.Add(step.StepNo)) throw new ExtractionError($"duplicate safety step number {step.StepNo}");
        return steps;
    }
    private static IReadOnlyList<SafetyStep> Parse(string text)
    {
        var steps = new List<SafetyStep>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.None))
        {
            var number = 0; var body = new List<string>();
            foreach (var line in block.Split('\n'))
            {
                var match = Numbered.Match(line);
                if (match.Success)
                {
                    if (body.Count > 0) steps.Add(new SafetyStep(number, string.Join(' ', body)));
                    number = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture); body = [match.Groups[2].Value];
                }
                else if (body.Count > 0 && !PythonString.IsBlank(line)) body.Add(PythonString.Strip(line));
            }
            if (body.Count > 0) steps.Add(new SafetyStep(number, string.Join(' ', body)));
        }
        return steps;
    }
}
