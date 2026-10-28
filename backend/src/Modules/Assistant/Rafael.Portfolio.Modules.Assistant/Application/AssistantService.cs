using System.Runtime.CompilerServices;
using Rafael.Portfolio.Modules.Assistant.Application.Orchestration;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application;

public sealed class AssistantService : IAssistantService
{
    private const string SafeBoundaryMessage =
        "I can only share documented details about Rafael's verified public portfolio. Feel free to ask about his software engineering projects, system architecture, or technical focus areas.";

    // One answer for every case without verified evidence, in chat and in the stream. It names no
    // topic, so it cannot present anything as documented that the evidence does not support.
    internal const string NotDocumentedMessage =
        "I don't have documented evidence in Rafael's public portfolio for that specific topic yet. I'm happy to share what his public profile does document - could you tell me a little more about what you'd like to know?";

    private const string VerifiedEvidenceStatus = "verified";

    // Wide enough that a document the question names is seen even when other documents share its
    // generic words; at most VerifiedChunkLimit verified chunks are still used.
    private const int CandidateLimit = 20;
    private const int VerifiedChunkLimit = 5;

    private static readonly string[] LinkPlatformNames =
    [
        "github",
        "linkedin",
        "twitter",
        "instagram",
        "youtube",
        "gitlab"
    ];

    private readonly IAssistantEvidenceAdapter _evidenceAdapter;
    private readonly IAssistantSynthesizer _synthesizer;
    private readonly IAssistantSafetyEvaluator _safetyEvaluator;
    private readonly IAssistantTurnOrchestrator? _orchestrator;
    private readonly TimeProvider _clock;

    public AssistantService(
        IAssistantEvidenceAdapter evidenceAdapter,
        IAssistantSynthesizer synthesizer,
        IAssistantSafetyEvaluator safetyEvaluator,
        IAssistantTurnOrchestrator? orchestrator = null,
        TimeProvider? timeProvider = null)
    {
        _evidenceAdapter = evidenceAdapter ?? throw new ArgumentNullException(nameof(evidenceAdapter));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _safetyEvaluator = safetyEvaluator ?? throw new ArgumentNullException(nameof(safetyEvaluator));
        _orchestrator = orchestrator;
        _clock = timeProvider ?? TimeProvider.System;
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

    public async ValueTask<AssistantChatResponse> ChatAsync(AssistantChatRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (!_safetyEvaluator.Evaluate(request.Message).IsSafe)
            return new AssistantChatResponse(SafeBoundaryMessage, AssistantGroundingStatus.NotDocumented, []);
        var start = _clock.GetTimestamp();
        var chunks = FindVerifiedChunks(request);
        // Consent gates the entire orchestrator, including credentials, counting and reservations.
        var modeled = _orchestrator is not null && request.ModelConsent?.GrantsModelProcessing() == true
            ? await _orchestrator.RunAsync(request.Message, chunks, start, cancellationToken) : null;
        return modeled ?? _synthesizer.Synthesize(request.Message, chunks);
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

        var start = _clock.GetTimestamp();
        var relevantChunks = FindVerifiedChunks(request);

        if (relevantChunks.Count == 0)
        {
            yield return AssistantStreamEvent.Status(AssistantGroundingStatus.NotDocumented);
            yield return AssistantStreamEvent.Token(NotDocumentedMessage);
            yield return AssistantStreamEvent.DoneEvent();
            yield break;
        }

        // Finish validation/accounting before emitting any model-path event. A disabled model
        // retains the existing deterministic event order, citations and token segmentation.
        var modeled = _orchestrator is not null && request.ModelConsent?.GrantsModelProcessing() == true
            ? await _orchestrator.RunAsync(request.Message, relevantChunks, start, cancellationToken) : null;
        var synthesis = modeled ?? _synthesizer.Synthesize(request.Message, relevantChunks);
        yield return AssistantStreamEvent.Status(synthesis.GroundingStatus);

        foreach (var citation in synthesis.Citations)
        {
            yield return AssistantStreamEvent.CitationEvent(citation);
        }

        if (modeled is not null)
        {
            // Preserve every canonical character, including spaces at token boundaries.
            for (var offset = 0; offset < synthesis.Answer.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(40, synthesis.Answer.Length - offset);
                if (offset + length < synthesis.Answer.Length
                    && char.IsHighSurrogate(synthesis.Answer[offset + length - 1])
                    && char.IsLowSurrogate(synthesis.Answer[offset + length])) length--;
                yield return AssistantStreamEvent.Token(synthesis.Answer.Substring(offset, length));
                offset += length;
                await Task.Yield();
            }
            yield return AssistantStreamEvent.DoneEvent();
            yield break;
        }

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
        // Which subject the question names is decided over all evidence. The caller's slug filter
        // only chooses the source of the answer, so it must not hide a named pending project and
        // let a verified document stand in for it.
        var subjectChunks = request.Slug is null
            ? null
            : _evidenceAdapter.SearchEvidence(request.Message, limit: CandidateLimit, slugFilter: null);

        var candidateChunks = _evidenceAdapter.SearchEvidence(
            request.Message,
            limit: CandidateLimit,
            slugFilter: request.Slug);

        // A question that names a subject with no verified evidence is undocumented. Generic words
        // it shares with other documents (for example "AI engineering") must not make unrelated
        // verified evidence stand in for the named subject.
        var namedSubjects = subjectChunks ?? candidateChunks;
        if (namedSubjects.Any(chunk =>
                !IsVerified(chunk) &&
                NamesSubject(request.Message, chunk) &&
                !namedSubjects.Any(other => IsVerified(other) && string.Equals(other.Slug, chunk.Slug, StringComparison.Ordinal))))
        {
            return [];
        }

        var verifiedChunks = candidateChunks
            .Where(chunk => chunk.Score > 0 && IsVerified(chunk))
            .ToList();

        var paddedQuery = $" {NormalizeWords(request.Message)} ";
        var requestedPlatforms = LinkPlatformNames
            .Where(platform => ContainsPhrase(paddedQuery, platform))
            .ToList();

        if (requestedPlatforms.Count > 0)
        {
            var linksChunk = verifiedChunks.FirstOrDefault(chunk =>
                string.Equals(chunk.SectionSlug, "links", StringComparison.OrdinalIgnoreCase));

            if (linksChunk is null)
            {
                return [];
            }

            var lines = linksChunk.Content
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (requestedPlatforms.Any(platform =>
                    !lines.Any(line => ContainsPhrase($" {NormalizeWords(line)} ", platform))))
            {
                return [];
            }

            var matchingLines = lines
                .Where(line => requestedPlatforms.Any(platform =>
                    ContainsPhrase($" {NormalizeWords(line)} ", platform)))
                .ToList();

            var matchingApprovedClaims = (linksChunk.ApprovedClaims ?? [])
                .Where(claim => requestedPlatforms.Any(platform =>
                    ContainsPhrase($" {NormalizeWords(claim.Statement)} ", platform)))
                .ToList();

            var matchingClaimIds = matchingApprovedClaims.Count > 0
                ? matchingApprovedClaims.Select(claim => claim.ClaimId).ToList()
                : linksChunk.Claims;

            return
            [
                linksChunk with
                {
                    Content = string.Join("\n", matchingLines),
                    Claims = matchingClaimIds,
                    ApprovedClaims = matchingApprovedClaims.Count > 0 ? matchingApprovedClaims : linksChunk.ApprovedClaims
                }
            ];
        }

        return verifiedChunks
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
