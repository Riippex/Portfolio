namespace Rafael.Portfolio.Modules.Contact.Domain;

public sealed record ContactRelayRequest(
    string? Name = null,
    string? Email = null,
    string? Message = null,
    bool Consent = false,
    string? TurnstileToken = null);
