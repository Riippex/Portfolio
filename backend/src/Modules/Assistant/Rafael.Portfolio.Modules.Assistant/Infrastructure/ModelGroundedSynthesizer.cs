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

        var reservationId = $"res_{Guid.NewGuid():N}";
        var reservation = Wait(_ledger.TryReserveAsync(reservationId));
        if (!reservation.Success)
        {
            return _fallbackSynthesizer.Synthesize(query, relevantChunks);
        }

        // A provider call has been authorized once this is true; from then on the outcome of
        // any failure is unknown and must stay charged instead of being refunded.
        var dispatched = false;
        try
        {
            const string systemPrompt =
                "You are Rafael's grounded AI assistant. Answer using ONLY verified facts provided in context. " +
                "Do NOT speculate, guess, or invent claims. Keep responses concise, objective, and professional.";

            var userMessage = $"Question: {query}\n\nEvidence Context:\n" +
                string.Join("\n", relevantChunks.Select(c => $"- [{c.Slug} / {c.Title}]: {c.Content}"));

            // One token per UTF-16 unit is an upper bound, so this can only over-reserve.
            var estimatedInputTokens = (int)Math.Min(int.MaxValue, (long)systemPrompt.Length + userMessage.Length);
            var call = Wait(_ledger.TryBeginCallAsync(reservationId, estimatedInputTokens, ModelControlPolicy.ApprovedMaxOutputTokens));
            if (!call.Allowed)
            {
                Wait(_ledger.CancelUndispatchedAsync(reservationId));
                return _fallbackSynthesizer.Synthesize(query, relevantChunks);
            }

            dispatched = true;
            var genResponse = Wait(_modelProvider.GenerateAsync(new ModelProviderRequest(
                systemPrompt,
                userMessage,
                Tools: null,
                MaxOutputTokens: ModelControlPolicy.ApprovedMaxOutputTokens)));

            if (!genResponse.Success || string.IsNullOrWhiteSpace(genResponse.Text))
            {
                Wait(_ledger.AbandonAsync(reservationId));
                return _fallbackSynthesizer.Synthesize(query, relevantChunks);
            }

            var completed = Wait(_ledger.CompleteCallAsync(
                reservationId,
                new ModelUsage(genResponse.InputTokens, genResponse.OutputTokens)));
            if (completed != ModelControlOutcome.Applied)
            {
                return _fallbackSynthesizer.Synthesize(query, relevantChunks);
            }

            Wait(_ledger.SettleAsync(reservationId));

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
            // Best effort and never a refund: without a dispatched call nothing was spent.
            try
            {
                Wait(dispatched ? _ledger.AbandonAsync(reservationId) : _ledger.CancelUndispatchedAsync(reservationId));
            }
            catch
            {
                // The ledger keeps the charge and the permit lease protects the allowance.
            }

            return _fallbackSynthesizer.Synthesize(query, relevantChunks);
        }
    }

    private static T Wait<T>(ValueTask<T> task) =>
        task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
}
