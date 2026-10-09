using System.Text;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class DeterministicGroundedSynthesizer : IAssistantSynthesizer
{
    public AssistantChatResponse Synthesize(string query, IReadOnlyList<AssistantEvidenceChunk> relevantChunks)
    {
        ArgumentNullException.ThrowIfNull(relevantChunks);

        if (relevantChunks.Count == 0)
        {
            return new AssistantChatResponse(
                Answer: AssistantService.NotDocumentedMessage,
                GroundingStatus: AssistantGroundingStatus.NotDocumented,
                Citations: []);
        }

        var topChunks = relevantChunks.Take(3).ToList();
        var citations = new List<AssistantCitation>();
        var sb = new StringBuilder();

        sb.Append("Here is the verified evidence from Rafael's public portfolio:\n\n");

        foreach (var chunk in topChunks)
        {
            sb.AppendLine($"### {chunk.Title} — {chunk.SectionHeading}");
            sb.AppendLine(chunk.Content);
            sb.AppendLine();

            citations.Add(new AssistantCitation(
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

        return new AssistantChatResponse(
            Answer: sb.ToString().TrimEnd(),
            GroundingStatus: AssistantGroundingStatus.Grounded,
            Citations: citations);
    }
}
