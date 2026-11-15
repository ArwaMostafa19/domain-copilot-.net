using System.Text.RegularExpressions;
using DomainCopilot.Domain.Rag;

namespace DomainCopilot.Application.Retrieval;

public static partial class HybridRetrieval
{
    public const int DenseLimit = 20, KeywordLimit = 20, TopResults = 6, RrfK = 60, MinTermLength = 3;
    private static readonly string[] SupersededPhrases = ["revision", "previous", "older", "changed between", "difference between"];
    public const int DENSE_LIMIT = DenseLimit, KEYWORD_LIMIT = KeywordLimit, TOP_RESULTS = TopResults, RRF_K = RrfK, MIN_TERM_LENGTH = MinTermLength;
    public static IReadOnlyList<string> SUPERSEDED_PHRASES => SupersededPhrases;
    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9-]*", RegexOptions.CultureInvariant)] private static partial Regex TermPattern();
    [GeneratedRegex(@"\brev\.?\s*[a-z]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex RevisionPattern();

    public static IReadOnlyList<string> KeywordTerms(string question) => TermPattern().Matches(question.ToLowerInvariant())
        .Select(m => m.Value.TrimEnd('-')).Where(t => t.Length >= MinTermLength).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static bool WantsSupersededRevisions(string question) => RevisionPattern().IsMatch(question) ||
        SupersededPhrases.Any(phrase => question.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<Evidence> ReciprocalRankFuse(IEnumerable<IEnumerable<Evidence>> lists, int k = RrfK, int? limit = null)
    {
        var scores = new Dictionary<long, double>();
        var merged = new Dictionary<long, Evidence>();
        foreach (var list in lists)
        {
            var rank = 0;
            foreach (var item in list)
            {
                rank++;
                scores[item.ChunkId] = scores.GetValueOrDefault(item.ChunkId) + 1.0 / (k + rank);
                if (!merged.TryGetValue(item.ChunkId, out var old)) merged[item.ChunkId] = item;
                else merged[item.ChunkId] = old with { DenseScore = Math.Max(old.DenseScore, item.DenseScore), KeywordScore = Math.Max(old.KeywordScore, item.KeywordScore) };
            }
        }
        var result = merged.Values.Select(x => x with { FusedScore = scores[x.ChunkId] })
            .OrderByDescending(x => x.FusedScore).ThenBy(x => x.ChunkId);
        var ordered = result.ToArray();
        if (limit is int n) ordered = ordered.Take(n >= 0 ? n : Math.Max(0, ordered.Length + n)).ToArray();
        return ordered;
    }

    public static IReadOnlyList<Evidence> CollapseDuplicates(IEnumerable<Evidence> evidence) => evidence
        .GroupBy(x => (x.DocId, x.Section))
        .Select(g => g.OrderByDescending(x => x.FusedScore).ThenBy(x => x.ChunkId).First())
        .OrderByDescending(x => x.FusedScore).ThenBy(x => x.ChunkId).ToArray();

    public static IReadOnlyList<Evidence> Retrieve(string question, string tenantId, IEmbedder embedder, IChunkSearch search, IRetrievalSettings settings)
    {
        var vector = embedder.Embed([question])[0];
        var superseded = WantsSupersededRevisions(question);
        var dense = search.DenseSearch(tenantId, vector, embedder.ModelName, superseded, DenseLimit);
        var keyword = search.KeywordSearch(tenantId, KeywordTerms(question), embedder.ModelName, superseded, KeywordLimit);
        return CollapseDuplicates(ReciprocalRankFuse([dense, keyword])).Take(TopResults).ToArray();
    }
}
