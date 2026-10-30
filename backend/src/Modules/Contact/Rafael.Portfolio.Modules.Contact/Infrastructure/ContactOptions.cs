using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Rafael.Portfolio.Modules.Contact.Infrastructure;

public sealed class ContactOptions
{
    public const string SectionName = "Contact";

    public bool Enabled { get; set; }
    public string RelayMode { get; set; } = "rest";
    public string? WorkerUrl { get; set; }
    public string? AccountId { get; set; }
    public string? ApiToken { get; set; }
    public string? SenderEmail { get; set; }
    public string? RecipientEmail { get; set; }

    public bool IsConfigured =>
        Enabled &&
        (RelayMode == "rest" ? !string.IsNullOrWhiteSpace(AccountId) : RelayMode == "worker" && IsWorkerConfigured) &&
        !string.IsNullOrWhiteSpace(ApiToken) &&
        !string.IsNullOrWhiteSpace(SenderEmail) &&
        !string.IsNullOrWhiteSpace(RecipientEmail);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (RelayMode is not ("rest" or "worker"))
        {
            throw new InvalidOperationException("Contact:RelayMode must be rest or worker.");
        }

        if (RelayMode == "worker" && !IsWorkerConfigured)
        {
            throw new InvalidOperationException("Contact worker requires its stage-specific HTTPS endpoint and a 64-character lowercase hexadecimal relay token.");
        }

        if (RelayMode == "rest" && string.IsNullOrWhiteSpace(AccountId))
        {
            throw new InvalidOperationException("Contact:AccountId is required when Contact:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(ApiToken))
        {
            throw new InvalidOperationException("Contact:ApiToken is required when Contact:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(SenderEmail) || !IsValidEmail(SenderEmail))
        {
            throw new InvalidOperationException("Contact:SenderEmail must be a valid email address when Contact:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(RecipientEmail) || !IsValidEmail(RecipientEmail))
        {
            throw new InvalidOperationException("Contact:RecipientEmail must be a valid email address when Contact:Enabled is true.");
        }
    }

    // A configuration value cannot become an arbitrary URL fetch or a credential redirect.
    public bool IsWorkerConfigured =>
        WorkerUrl is not null &&
        Regex.IsMatch(WorkerUrl, @"\Ahttps://rafael-portfolio-contact-(dev|prod)\.[a-z0-9-]+\.workers\.dev/v1/contact-relay/\1\z", RegexOptions.CultureInvariant) &&
        ApiToken is not null && Regex.IsMatch(ApiToken, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant);

    public void ValidateStage(string stage)
    {
        if (Enabled && RelayMode == "worker" &&
            (stage is not ("dev" or "prod") || WorkerUrl is null ||
             !WorkerUrl.EndsWith($"/contact-relay/{stage}", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Contact worker must match the explicit deployment stage.");
        }
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new MailAddress(email);
            return addr.Address == email && email.Contains('.') && !email.EndsWith('.');
        }
        catch
        {
            return false;
        }
    }
}
