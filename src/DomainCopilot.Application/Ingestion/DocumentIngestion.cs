using DomainCopilot.Application.Text;
using DomainCopilot.Domain.Documents;
using DomainCopilot.Domain.Llm;

namespace DomainCopilot.Application.Ingestion;

public static class DocumentIngestion
{
    public static IngestResult IngestFile(string path, IEmbedder embedder, IDocumentRepository repository,
        IDocumentExtractor extractor, int maxChars = DocumentChunker.DefaultMaxChars)
    {
        ExtractedDocument document;
        try { document = extractor.Extract(path); }
        catch (ExtractionError error) { return new(path.Replace('\\', '/'), "failed", Error: error.Message); }
        var meta = document.Meta;
        if (repository.StoredFingerprint(meta) == meta.ContentSha256) return new(path.Replace('\\', '/'), "skipped");
        try
        {
            var chunks = DocumentChunker.Chunk(document, maxChars);
            var steps = SafetyStepExtractor.Extract(document);
            var vectors = embedder.Embed(chunks.Select(chunk => $"{chunk.Context}\n{chunk.Text}").ToArray());
            CheckVectors(vectors, chunks.Count, embedder);
            repository.ReplaceDocument(meta, chunks, vectors, steps, embedder.ModelName);
            return new(path.Replace('\\', '/'), "ingested", chunks.Count, steps.Count);
        }
        catch (DomainError error)
        {
            repository.MarkFailed(meta, error.Message);
            return new(path.Replace('\\', '/'), "failed", Error: error.Message);
        }
    }

    public static IReadOnlyList<IngestResult> IngestFiles(IEnumerable<string> paths, IEmbedder embedder,
        IDocumentRepository repository, IDocumentExtractor extractor, int maxChars = DocumentChunker.DefaultMaxChars) =>
        paths.Select(path => IngestFile(path, embedder, repository, extractor, maxChars)).ToArray();

    private static void CheckVectors(IReadOnlyList<IReadOnlyList<double>> vectors, int chunks, IEmbedder embedder)
    {
        if (vectors.Count != chunks) throw new ProviderResponseError(embedder.ModelName,
            $"embedder '{embedder.ModelName}' returned {vectors.Count} vectors for {chunks} chunks");
        for (var index = 0; index < vectors.Count; index++)
            if (vectors[index].Count != embedder.Dimension) throw new ProviderResponseError(embedder.ModelName,
                $"embedder '{embedder.ModelName}' vector {index} has {vectors[index].Count} values, expected {embedder.Dimension}");
    }
}
