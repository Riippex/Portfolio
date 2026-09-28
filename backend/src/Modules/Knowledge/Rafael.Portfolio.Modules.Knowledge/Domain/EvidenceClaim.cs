namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public sealed record EvidenceClaim(
    string ClaimId,
    string Statement,
    string Status,
    string Citation);
