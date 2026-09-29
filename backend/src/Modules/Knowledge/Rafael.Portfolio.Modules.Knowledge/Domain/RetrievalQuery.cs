namespace Rafael.Portfolio.Modules.Knowledge.Domain;

public sealed record RetrievalQuery(
    string QueryText,
    int Limit = 5,
    string? SlugFilter = null);
