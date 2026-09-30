using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class AssistantSafetyEvaluator : IAssistantSafetyEvaluator
{
    private static readonly string[] DangerousKeywords =
    [
        "ignore previous instructions",
        "disregard all instructions",
        "disregard previous instructions",
        "forget your instructions",
        "forget all instructions",
        "reveal system prompt",
        "print system prompt",
        "output system prompt",
        "show system prompt",
        "what is your system prompt",
        "jailbreak",
        "dan mode",
        "act as unrestricted",
        "developer mode",
        "bypass safety",
        "override safety"
    ];

    public AssistantSafetyDecision Evaluate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new AssistantSafetyDecision(true);
        }

        foreach (var keyword in DangerousKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return new AssistantSafetyDecision(false, $"Detected unsafe instruction pattern: {keyword}");
            }
        }

        return new AssistantSafetyDecision(true);
    }
}
