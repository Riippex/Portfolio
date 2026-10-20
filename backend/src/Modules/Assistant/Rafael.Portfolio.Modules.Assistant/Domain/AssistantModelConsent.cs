namespace Rafael.Portfolio.Modules.Assistant.Domain;

/// <summary>Request-transient visitor choice, not an identity or a durable consent receipt.</summary>
public sealed record AssistantModelConsent(bool Accepted, string? NoticeVersion)
{
    public const string CurrentNoticeVersion = "rai-google-global-standard-v1";

    public bool GrantsModelProcessing() => Accepted && string.Equals(NoticeVersion, CurrentNoticeVersion, StringComparison.Ordinal);
}
