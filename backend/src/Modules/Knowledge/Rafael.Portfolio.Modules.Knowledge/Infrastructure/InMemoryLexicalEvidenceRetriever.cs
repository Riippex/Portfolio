using Rafael.Portfolio.Modules.Knowledge.Application;
using Rafael.Portfolio.Modules.Knowledge.Domain;

namespace Rafael.Portfolio.Modules.Knowledge.Infrastructure;

public sealed class InMemoryLexicalEvidenceRetriever : IEvidenceRetriever
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const double HeadingWeight = 3.0;
    private const double ClaimWeight = 2.0;
    private const double TitleWeight = 1.5;
    private const double ContentWeight = 1.0;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for", "of",
        "with", "by", "from", "up", "about", "into", "over", "after", "is", "are",
        "was", "were", "be", "been", "being", "have", "has", "had", "do", "does",
        "did", "will", "would", "shall", "should", "may", "might", "must", "can",
        "could", "it", "its", "this", "that", "these", "those"
    };

    private readonly IReadOnlyList<IndexedChunk> _chunks;
    private readonly Dictionary<string, double> _idf;
    private readonly double _avgDocLength;

    public InMemoryLexicalEvidenceRetriever(IEvidenceSource source)
        : this(source.GetAllEvidence())
    {
    }

    public InMemoryLexicalEvidenceRetriever(IReadOnlyList<IngestedEvidenceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var chunks = new List<IndexedChunk>();
        var termDocCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in documents)
        {
            foreach (var section in doc.Sections)
            {
                var chunkId = $"{doc.Item.Id}#{section.Slug}";
                var citations = BuildCitations(doc, section);

                var termFrequencies = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                AddTokens(termFrequencies, section.Heading, HeadingWeight);
                AddTokens(termFrequencies, doc.Item.Title, TitleWeight);
                if (!string.IsNullOrWhiteSpace(doc.Item.Headline))
                {
                    AddTokens(termFrequencies, doc.Item.Headline, TitleWeight);
                }

                foreach (var claim in section.Claims)
                {
                    AddTokens(termFrequencies, claim.Statement, ClaimWeight);
                }

                AddTokens(termFrequencies, section.Content, ContentWeight);

                var docLength = termFrequencies.Values.Sum();

                foreach (var term in termFrequencies.Keys)
                {
                    termDocCounts[term] = termDocCounts.GetValueOrDefault(term) + 1;
                }

                chunks.Add(new IndexedChunk(
                    chunkId,
                    doc.Item.Id,
                    doc.Item.Slug,
                    doc.Item.Title,
                    section.Heading,
                    section.Slug,
                    section.Content,
                    section.Claims,
                    doc.Item.SourceUrl,
                    doc.Item.EvidenceStatus,
                    doc.Item.Version,
                    doc.Visibility,
                    citations,
                    doc.Item.Kind,
                    termFrequencies,
                    docLength));
            }
        }

        _chunks = chunks;

        var totalDocs = Math.Max(1, chunks.Count);
        _avgDocLength = chunks.Count > 0 ? chunks.Average(c => c.DocLength) : 1.0;

        _idf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, count) in termDocCounts)
        {
            var idf = Math.Log(1.0 + (totalDocs - count + 0.5) / (count + 0.5));
            _idf[term] = Math.Max(0.1, idf);
        }
    }

    public IReadOnlyList<RetrievedChunk> Retrieve(string queryText, int limit = 5, string? slugFilter = null) =>
        Retrieve(new RetrievalQuery(queryText, limit, slugFilter));

    public IReadOnlyList<RetrievedChunk> Retrieve(RetrievalQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.QueryText.Length > RetrievalQuery.MaxQueryTextLength)
        {
            throw new ArgumentException(
                $"Query text exceeds the maximum length of {RetrievalQuery.MaxQueryTextLength} characters.",
                nameof(query));
        }

        if (query.SlugFilter is not null &&
            (query.SlugFilter.Length > RetrievalQuery.MaxSlugFilterLength ||
             !RetrievalQuery.IsValidSlugFilter(query.SlugFilter)))
        {
            throw new ArgumentException(
                "Slug filter must be a non-empty lowercase slug (letters, digits, hyphens) within the maximum length.",
                nameof(query));
        }

        if (string.IsNullOrWhiteSpace(query.QueryText))
        {
            return [];
        }

        var queryTerms = Tokenize(query.QueryText).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (queryTerms.Count == 0)
        {
            return [];
        }

        var effectiveLimit = Math.Clamp(query.Limit, 1, 50);

        var scoredChunks = new List<(IndexedChunk Chunk, double Score)>();

        foreach (var chunk in _chunks)
        {
            if (!string.IsNullOrWhiteSpace(query.SlugFilter) &&
                !string.Equals(chunk.Slug, query.SlugFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = 0.0;
            foreach (var term in queryTerms)
            {
                if (chunk.TermFrequencies.TryGetValue(term, out var tf) && _idf.TryGetValue(term, out var idf))
                {
                    var denominator = tf + K1 * (1.0 - B + B * (chunk.DocLength / _avgDocLength));
                    score += idf * (tf * (K1 + 1.0)) / denominator;
                }
            }

            if (score > 0.0)
            {
                scoredChunks.Add((chunk, score));
            }
        }

        return scoredChunks
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.ChunkId, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .Select(x => new RetrievedChunk(
                x.Chunk.ChunkId,
                x.Chunk.DocumentId,
                x.Chunk.Slug,
                x.Chunk.Title,
                x.Chunk.SectionHeading,
                x.Chunk.SectionSlug,
                x.Chunk.Content,
                x.Chunk.Claims,
                x.Chunk.SourceUrl,
                x.Chunk.EvidenceStatus,
                x.Chunk.Version,
                x.Chunk.Visibility,
                Math.Round(x.Score, 4),
                x.Chunk.Citations,
                x.Chunk.Kind))
            .ToList();
    }

    private static IReadOnlyList<string> BuildCitations(IngestedEvidenceDocument doc, EvidenceSection section)
    {
        var citations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var defaultCitation = $"{doc.Item.DocumentPath}#{section.Slug}";
        citations.Add(defaultCitation);

        foreach (var claim in section.Claims)
        {
            if (!string.IsNullOrWhiteSpace(claim.Citation))
            {
                citations.Add(claim.Citation);
            }
        }

        return citations.OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    private static void AddTokens(Dictionary<string, double> termFrequencies, string? text, double weight)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var token in Tokenize(text))
        {
            termFrequencies[token] = termFrequencies.GetValueOrDefault(token) + weight;
        }
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var start = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else
            {
                if (start >= 0)
                {
                    var word = text[start..i].ToLowerInvariant();
                    start = -1;
                    if (!StopWords.Contains(word))
                    {
                        AddExpandedTokens(tokens, word);
                    }
                }
            }
        }

        if (start >= 0)
        {
            var word = text[start..].ToLowerInvariant();
            if (!StopWords.Contains(word))
            {
                AddExpandedTokens(tokens, word);
            }
        }

        return tokens;
    }

    private static void AddExpandedTokens(List<string> tokens, string token)
    {
        tokens.Add(token);

        if (token.Length > 4 && token.EndsWith("ies", StringComparison.Ordinal))
        {
            tokens.Add(token[..^3] + "y");
        }
        else if (token.Length > 4 && token.EndsWith("es", StringComparison.Ordinal))
        {
            tokens.Add(token[..^2]);
        }
        else if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
        {
            tokens.Add(token[..^1]);
        }
    }

    private sealed record IndexedChunk(
        string ChunkId,
        string DocumentId,
        string Slug,
        string Title,
        string SectionHeading,
        string SectionSlug,
        string Content,
        IReadOnlyList<EvidenceClaim> Claims,
        string? SourceUrl,
        string EvidenceStatus,
        string Version,
        string Visibility,
        IReadOnlyList<string> Citations,
        string Kind,
        IReadOnlyDictionary<string, double> TermFrequencies,
        double DocLength);
}
