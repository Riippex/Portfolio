using Rafael.Portfolio.Modules.Assistant.Domain;

namespace Rafael.Portfolio.Modules.Assistant.Application;

public interface IAssistantSafetyEvaluator
{
    AssistantSafetyDecision Evaluate(string message);
}
