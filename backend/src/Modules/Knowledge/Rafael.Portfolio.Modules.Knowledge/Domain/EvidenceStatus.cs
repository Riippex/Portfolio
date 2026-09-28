namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public static class EvidenceStatus
{
    public const string Pending = "pending";
    public const string Verified = "verified";

    public static bool IsValid(string status) =>
        status is Pending or Verified;
}
