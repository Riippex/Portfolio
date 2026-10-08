using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class ModelGroundedSynthesizer : IAssistantSynthesizer
{
    private readonly IModelProvider _modelProvider;
    private readonly IModelControlLedger _ledger;
    private readonly IAssistantSynthesizer _fallbackSynthesizer;

    public ModelGroundedSynthesizer(
        IModelProvider modelProvider,
        IModelControlLedger ledger,
        IAssistantSynthesizer fallbackSynthesizer)
    {
        _modelProvider = modelProvider;
        _ledger = ledger;
        _fallbackSynthesizer = fallbackSynthesizer;
    }

    public AssistantChatResponse Synthesize(string query, IReadOnlyList<AssistantEvidenceChunk> relevantChunks)
    {
        if (!_modelProvider.IsAvailable)
        {
            return _fallbackSynthesizer.Synthesize(query, relevantChunks);
        }

        var reservationId = $"res_synth_{Guid.NewGuid():N}";
        var reservationTask = _ledger.TryReserveAsync(reservationId, maxEstimatedCostMicroUsd: 15_000, maxInputTokens: 6000, maxOutputTokens: 600, maxCalls: 2);
        var reservation = reservationTask.IsCompletedSuccessfully
            ? reservationTask.Result
            : reservationTask.AsTask().GetAwaiter().GetResult();

        if (!reservation.Success)
        {
            return _fallbackSynthesizer.Synthesize(query, relevantChunks);
        }

        try
        {
            const string systemPrompt =
                "You are Rafael's grounded AI assistant. Answer using ONLY verified facts provided in context. " +
                "Do NOT speculate, guess, or invent claims. Keep responses concise, objective, and professional.";

            var userMessage = $"Question: {query}\n\nEvidence Context:\n" +
                string.Join("\n", relevantChunks.Select(c => $"- [{c.Slug} / {c.Title}]: {c.Content}"));

            var genTask = _modelProvider.GenerateAsync(new ModelProviderRequest(systemPrompt, userMessage, Tools: null, MaxOutputTokens: 600));
            var genResponse = genTask.IsCompletedSuccessfully
                ? genTask.Result
                : genTask.AsTask().GetAwaiter().GetResult();

            if (!genResponse.Success || string.IsNullOrWhiteSpace(genResponse.Text))
            {
                _ledger.ReleasePermitAsync(reservationId);
                return _fallbackSynthesizer.Synthesize(query, relevantChunks);
            }

            var commitTask = _ledger.CommitAsync(
                reservationId,
                actualCostMicroUsd: Math.Max(1, (genResponse.InputTokens * 75 + genResponse.OutputTokens * 300) / 1000),
                inputTokensUsed: genResponse.InputTokens,
                outputTokensUsed: genResponse.OutputTokens,
                callsMade: 1);

            if (!commitTask.IsCompletedSuccessfully)
            {
                commitTask.AsTask().GetAwaiter().GetResult();
            }

            var citations = relevantChunks.Select(c => new AssistantCitation(
                c.ChunkId,
                c.DocumentId,
                c.Slug,
                c.Title,
                c.SectionHeading,
                c.SourceUrl,
                c.EvidenceStatus,
                c.Version,
                c.Claims)).ToList();

            return new AssistantChatResponse(
                genResponse.Text.Trim(),
                AssistantGroundingStatus.Grounded,
                citations);
        }
        catch
        {
            _ledger.ReleasePermitAsync(reservationId);
            return _fallbackSynthesizer.Synthesize(query, relevantChunks);
        }
    }
}
