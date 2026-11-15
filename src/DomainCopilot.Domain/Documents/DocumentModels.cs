using System.Collections.Immutable;

namespace DomainCopilot.Domain.Documents;

/// <summary>Ownership, identity, version, and source details for a document.</summary>
public sealed record DocumentMeta(
    string TenantId,
    string DocId,
    string Title,
    string Revision,
    string Status,
    string SourceFormat,
    string SourcePath,
    string ContentSha256,
    string? Equipment = null,
    string? DocumentType = null);

/// <summary>One titled portion of an extracted document.</summary>
public sealed record Section(string Title, string Text, int? Page = null);

/// <summary>The metadata and ordered sections extracted from a source document.</summary>
public sealed class ExtractedDocument : IEquatable<ExtractedDocument>
{
    public ExtractedDocument(DocumentMeta Meta, ImmutableArray<Section> Sections)
    {
        this.Meta = Meta;
        this.Sections = Sections.IsDefault ? ImmutableArray<Section>.Empty : Sections;
    }

    public DocumentMeta Meta { get; }

    public ImmutableArray<Section> Sections { get; }

    public bool Equals(ExtractedDocument? other) =>
        other is not null &&
        EqualityComparer<DocumentMeta>.Default.Equals(Meta, other.Meta) &&
        SectionsEqual(Sections, other.Sections);

    public override bool Equals(object? obj) => obj is ExtractedDocument other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Meta);
        hash.Add(Sections.Length);
        foreach (var section in Sections)
            hash.Add(section);
        return hash.ToHashCode();
    }

    public static bool operator ==(ExtractedDocument? left, ExtractedDocument? right) =>
        EqualityComparer<ExtractedDocument?>.Default.Equals(left, right);

    public static bool operator !=(ExtractedDocument? left, ExtractedDocument? right) => !(left == right);

    private static bool SectionsEqual(ImmutableArray<Section> left, ImmutableArray<Section> right)
    {
        if (left.Length != right.Length) return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!EqualityComparer<Section>.Default.Equals(left[index], right[index]))
                return false;
        }

        return true;
    }
}

/// <summary>A piece of section text stored and cited independently.</summary>
public sealed record Chunk(
    int Ordinal,
    string Section,
    string Context,
    string Text,
    string Revision,
    int? Page = null);

/// <summary>One numbered safety prerequisite extracted from a procedure.</summary>
public sealed record SafetyStep(int StepNo, string Text);
