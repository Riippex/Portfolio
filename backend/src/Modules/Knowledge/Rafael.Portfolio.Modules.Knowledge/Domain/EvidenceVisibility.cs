namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public static class EvidenceVisibility
{
    public const string Public = "public";

    public static bool IsValid(string visibility) =>
        string.Equals(visibility, Public, StringComparison.OrdinalIgnoreCase);
}
