using System.Net.Mail;

namespace Rafael.Portfolio.Modules.Contact.Infrastructure;

public sealed class ContactOptions
{
    public const string SectionName = "Contact";

    public bool Enabled { get; set; }
    public string? AccountId { get; set; }
    public string? ApiToken { get; set; }
    public string? SenderEmail { get; set; }
    public string? RecipientEmail { get; set; }

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(AccountId) &&
        !string.IsNullOrWhiteSpace(ApiToken) &&
        !string.IsNullOrWhiteSpace(SenderEmail) &&
        !string.IsNullOrWhiteSpace(RecipientEmail);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(AccountId))
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
