namespace Rafael.Portfolio.Modules.Assistant.Domain;

public static class AssistantGroundingStatus
{
    public const string Grounded = "grounded";
    public const string NotDocumented = "not_documented";

    public static bool IsValid(string status) =>
        status is Grounded or NotDocumented;
}
