using System.Net.Mail;

namespace Rafael.Portfolio.Modules.Contact.Domain;

public sealed record ContactMessage
{
    public const int MaxNameLength = 100;
    public const int MinMessageLength = 10;
    public const int MaxMessageLength = 5000;
    public const int MaxEmailLength = 254;

    public string Name { get; }
    public string Email { get; }
    public string Message { get; }
    public bool Consent { get; }

    private ContactMessage(string name, string email, string message, bool consent)
    {
        Name = name;
        Email = email;
        Message = message;
        Consent = consent;
    }

    public static bool TryCreate(
        string? name,
        string? email,
        string? message,
        bool consent,
        out ContactMessage? contactMessage,
        out string? error)
    {
        contactMessage = null;

        if (!consent)
        {
            error = "Consent to submit contact details and message is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name is required and cannot be empty.";
            return false;
        }

        if (name.Contains('\r') || name.Contains('\n'))
        {
            error = "Name must not contain newline characters.";
            return false;
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > MaxNameLength)
        {
            error = $"Name exceeds the maximum length of {MaxNameLength} characters.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            error = "Email address is required and cannot be empty.";
            return false;
        }

        if (email.Contains('\r') || email.Contains('\n'))
        {
            error = "Email address must not contain newline characters.";
            return false;
        }

        var trimmedEmail = email.Trim();
        if (trimmedEmail.Contains(',') || trimmedEmail.Contains(';') || trimmedEmail.Contains('<') || trimmedEmail.Contains('>'))
        {
            error = "Only a single email mailbox is permitted.";
            return false;
        }

        if (trimmedEmail.Length > MaxEmailLength)
        {
            error = $"Email address exceeds the maximum length of {MaxEmailLength} characters.";
            return false;
        }

        if (!IsValidEmail(trimmedEmail))
        {
            error = "Email address format is invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            error = "Message is required and cannot be empty.";
            return false;
        }

        var trimmedMessage = message.Trim();
        if (trimmedMessage.Length < MinMessageLength)
        {
            error = $"Message must contain at least {MinMessageLength} characters.";
            return false;
        }

        if (trimmedMessage.Length > MaxMessageLength)
        {
            error = $"Message exceeds the maximum length of {MaxMessageLength} characters.";
            return false;
        }

        contactMessage = new ContactMessage(trimmedName, trimmedEmail, trimmedMessage, consent);
        error = null;
        return true;
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
