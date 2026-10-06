namespace Rafael.Portfolio.Modules.JobMatching.Domain;

public sealed record JobAnalysisRequest(string VacancyText)
{
    public const int MinVacancyLength = 10;
    public const int MaxVacancyLength = 5000;

    public static bool IsValid(JobAnalysisRequest? request, out string? error)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.VacancyText))
        {
            error = "Vacancy text is required and cannot be empty.";
            return false;
        }

        var trimmed = request.VacancyText.Trim();
        if (trimmed.Length < MinVacancyLength)
        {
            error = $"Vacancy text must contain at least {MinVacancyLength} characters.";
            return false;
        }

        if (trimmed.Length > MaxVacancyLength)
        {
            error = $"Vacancy text exceeds the maximum length of {MaxVacancyLength} characters.";
            return false;
        }

        error = null;
        return true;
    }
}
