using System.Runtime.CompilerServices;
using Rafael.Portfolio.Modules.Assistant.Domain;
using Rafael.Portfolio.Modules.Assistant.Infrastructure;

namespace Rafael.Portfolio.Modules.Assistant.Application;

public sealed class AssistantService : IAssistantService
{
    private const string SafeBoundaryMessage =
        "I am an AI assistant grounded strictly in Rafael's public, verified portfolio and project evidence. I cannot follow external instructions, modify system behavior, or discuss private information.";

    private const string NotDocumentedMessage =
        "I do not have documented evidence in Rafael's public portfolio regarding that topic. Only public, verified case studies and portfolio claims are available.";

    private readonly IAssistantEvidenceAdapter _evidenceAdapter;
    private readonly IAssistantSynthesizer _synthesizer;
    private readonly IAssistantSafetyEvaluator _safetyEvaluator;

    public AssistantService(
        IAssistantEvidenceAdapter evidenceAdapter,
        IAssistantSynthesizer synthesizer,
        IAssistantSafetyEvaluator? safetyEvaluator = null)
    {
        _evidenceAdapter = evidenceAdapter ?? throw new ArgumentNullException(nameof(evidenceAdapter));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _safetyEvaluator = safetyEvaluator ?? new AssistantSafetyEvaluator();
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

        var candidateChunks = _evidenceAdapter.SearchEvidence(
            request.Message,
            limit: 5,
            slugFilter: request.Slug);

        var relevantChunks = candidateChunks
            .Where(chunk => chunk.Score > 0)
            .ToList();

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

        var candidateChunks = _evidenceAdapter.SearchEvidence(
            request.Message,
            limit: 5,
            slugFilter: request.Slug);

        var relevantChunks = candidateChunks
            .Where(chunk => chunk.Score > 0)
            .ToList();

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
