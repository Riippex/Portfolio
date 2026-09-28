namespace Rafael.Portfolio.Modules.Portfolio.Domain;

public sealed record ProjectClaim(
    string ClaimId,
    string Statement,
    string Status,
    string Citation);
