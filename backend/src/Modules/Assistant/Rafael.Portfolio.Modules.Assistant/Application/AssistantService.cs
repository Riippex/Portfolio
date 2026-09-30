using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application;

public sealed class AssistantService : IAssistantService
{
    private readonly IAssistantEvidenceAdapter _evidenceAdapter;
    private readonly IAssistantSynthesizer _synthesizer;

    public AssistantService(
        IAssistantEvidenceAdapter evidenceAdapter,
        IAssistantSynthesizer synthesizer)
    {
        _evidenceAdapter = evidenceAdapter ?? throw new ArgumentNullException(nameof(evidenceAdapter));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
    }

    public AssistantChatResponse Chat(AssistantChatRequest request)
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

        var candidateChunks = _evidenceAdapter.SearchEvidence(
            request.Message,
            limit: 5,
            slugFilter: request.Slug);

        var relevantChunks = candidateChunks
            .Where(chunk => chunk.Score > 0)
            .ToList();

        return _synthesizer.Synthesize(request.Message, relevantChunks);
    }
}
