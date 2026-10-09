using System.Runtime.CompilerServices;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application;

public sealed class AssistantService : IAssistantService
{
    private const string SafeBoundaryMessage =
        "I can only share documented details about Rafael's verified public portfolio. Feel free to ask about his software engineering projects, system architecture, or technical focus areas.";

    private const string NotDocumentedMessage =
        "I don't have documented evidence in Rafael's public portfolio for that specific topic yet. You can explore documented topics like Vextis, StaffHub, or focus areas in AI & Software Engineering, or ask about specific project architecture.";

    private const string VerifiedEvidenceStatus = "verified";

    // Wide enough that a document the question names is seen even when other documents share its
    // generic words; at most VerifiedChunkLimit verified chunks are still used.
    private const int CandidateLimit = 20;
    private const int VerifiedChunkLimit = 5;

    private readonly IAssistantEvidenceAdapter _evidenceAdapter;
    private readonly IAssistantSynthesizer _synthesizer;
    private readonly IAssistantSafetyEvaluator _safetyEvaluator;

    public AssistantService(
        IAssistantEvidenceAdapter evidenceAdapter,
        IAssistantSynthesizer synthesizer,
        IAssistantSafetyEvaluator safetyEvaluator)
    {
        _evidenceAdapter = evidenceAdapter ?? throw new ArgumentNullException(nameof(evidenceAdapter));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _safetyEvaluator = safetyEvaluator ?? throw new ArgumentNullException(nameof(safetyEvaluator));
    }

    public AssistantChatResponse Chat(AssistantChatRequest request)
    {
        ValidateRequest(request);

        var safety = _safetyEvaluator.Evaluate(request.Message);
        if (!safety.IsSafe)
        {
            return new AssistantChatResponse(
                Answer: SafeBoundaryMessage,
                GroundingStatus: AssistantGroundingStatus.NotDocumented,
                Citations: []);
        }

        var relevantChunks = FindVerifiedChunks(request);

        return _synthesizer.Synthesize(request.Message, relevantChunks);
    }

    public async IAsyncEnumerable<AssistantStreamEvent> StreamChatAsync(
        AssistantChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var safety = _safetyEvaluator.Evaluate(request.Message);
        if (!safety.IsSafe)
        {
            yield return AssistantStreamEvent.Status(AssistantGroundingStatus.NotDocumented);
            yield return AssistantStreamEvent.Token(SafeBoundaryMessage);
            yield return AssistantStreamEvent.DoneEvent();
            yield break;
        }

        var relevantChunks = FindVerifiedChunks(request);

        if (relevantChunks.Count == 0)
        {
            yield return AssistantStreamEvent.Status(AssistantGroundingStatus.NotDocumented);
            yield return AssistantStreamEvent.Token(NotDocumentedMessage);
            yield return AssistantStreamEvent.DoneEvent();
            yield break;
        }

        yield return AssistantStreamEvent.Status(AssistantGroundingStatus.Grounded);

        var topChunks = relevantChunks.Take(3).ToList();
        foreach (var chunk in topChunks)
        {
            yield return AssistantStreamEvent.CitationEvent(new AssistantCitation(
                ChunkId: chunk.ChunkId,
                DocumentId: chunk.DocumentId,
                Slug: chunk.Slug,
                Title: chunk.Title,
                SectionHeading: chunk.SectionHeading,
                SourceUrl: chunk.SourceUrl,
                EvidenceStatus: chunk.EvidenceStatus,
                Version: chunk.Version,
                Claims: chunk.Claims));
        }

        var synthesis = _synthesizer.Synthesize(request.Message, relevantChunks);

        // Stream answer in small readable chunks to provide a natural SSE experience
        var words = synthesis.Answer.Split(' ');
        var currentChunk = new System.Text.StringBuilder();

        for (var i = 0; i < words.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (currentChunk.Length > 0)
            {
                currentChunk.Append(' ');
            }
            currentChunk.Append(words[i]);

            if (currentChunk.Length >= 40 || i == words.Length - 1)
            {
                yield return AssistantStreamEvent.Token(currentChunk.ToString());
                currentChunk.Clear();
                await Task.Yield();
            }
        }

        yield return AssistantStreamEvent.DoneEvent();
    }

    private List<AssistantEvidenceChunk> FindVerifiedChunks(AssistantChatRequest request)
    {
        var candidateChunks = _evidenceAdapter.SearchEvidence(
            request.Message,
            limit: CandidateLimit,
            slugFilter: request.Slug);

        // A question that names a subject with no verified evidence is undocumented. Generic words
        // it shares with other documents (for example "AI engineering") must not make unrelated
        // verified evidence stand in for the named subject.
        if (candidateChunks.Any(chunk =>
                !IsVerified(chunk) &&
                NamesSubject(request.Message, chunk) &&
                !candidateChunks.Any(other => IsVerified(other) && string.Equals(other.Slug, chunk.Slug, StringComparison.Ordinal))))
        {
            return [];
        }

        return candidateChunks
            .Where(chunk => chunk.Score > 0 && IsVerified(chunk))
            .Take(VerifiedChunkLimit)
            .ToList();
    }

    private static bool IsVerified(AssistantEvidenceChunk chunk) =>
        string.Equals(chunk.EvidenceStatus, VerifiedEvidenceStatus, StringComparison.OrdinalIgnoreCase);

    // The whole title or slug of the document must appear in the question as a phrase of words,
    // ignoring case and punctuation ("Kinetiq V", "kinetiq-v" and "KINETIQ V?" all name Kinetiq V).
    private static bool NamesSubject(string message, AssistantEvidenceChunk chunk)
    {
        var words = $" {NormalizeWords(message)} ";
        return ContainsPhrase(words, chunk.Title) || ContainsPhrase(words, chunk.Slug);
    }

    private static bool ContainsPhrase(string paddedWords, string name)
    {
        var phrase = NormalizeWords(name);
        return phrase.Length > 0 && paddedWords.Contains($" {phrase} ", StringComparison.Ordinal);
    }

    private static string NormalizeWords(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }

    private static void ValidateRequest(AssistantChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("Message cannot be empty or whitespace.", nameof(request));
        }

        if (request.Message.Length > AssistantChatRequest.MaxMessageLength)
        {
            throw new ArgumentException(
                $"Message exceeds maximum allowed length of {AssistantChatRequest.MaxMessageLength} characters.",
                nameof(request));
        }

        if (request.Slug is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Slug) ||
                request.Slug.Length > AssistantChatRequest.MaxSlugLength ||
                !AssistantChatRequest.IsValidSlug(request.Slug))
            {
                throw new ArgumentException(
                    $"Slug must be a valid alphanumeric identifier up to {AssistantChatRequest.MaxSlugLength} characters.",
                    nameof(request));
            }
        }
    }
}
