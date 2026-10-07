using Rafael.Portfolio.Modules.Contact.Domain;

namespace Rafael.Portfolio.UnitTests;

public sealed class ContactMessageTests
{
    [Fact]
    public void TryCreate_succeeds_with_valid_parameters()
    {
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            "jane@example.com",
            "Hello Rafael, this is a test inquiry about your work.",
            consent: true,
            out var message,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(message);
        Assert.Equal("Jane Doe", message.Name);
        Assert.Equal("jane@example.com", message.Email);
        Assert.Equal("Hello Rafael, this is a test inquiry about your work.", message.Message);
        Assert.True(message.Consent);
    }

    [Fact]
    public void TryCreate_trims_inputs()
    {
        var success = ContactMessage.TryCreate(
            "  Jane Doe  ",
            "  jane@example.com  ",
            "  Hello Rafael, this is a trimmed test inquiry.  ",
            consent: true,
            out var message,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(message);
        Assert.Equal("Jane Doe", message.Name);
        Assert.Equal("jane@example.com", message.Email);
        Assert.Equal("Hello Rafael, this is a trimmed test inquiry.", message.Message);
    }

    [Fact]
    public void TryCreate_fails_when_consent_is_false()
    {
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            "jane@example.com",
            "Hello Rafael, this is a test inquiry.",
            consent: false,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("Consent", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_fails_when_name_is_empty(string? name)
    {
        var success = ContactMessage.TryCreate(
            name,
            "jane@example.com",
            "Hello Rafael, this is a test inquiry.",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("Name is required", error);
    }

    [Theory]
    [InlineData("Jane\rDoe")]
    [InlineData("Jane\nDoe")]
    [InlineData("Jane Doe\r\n")]
    public void TryCreate_fails_when_name_contains_newlines(string nameWithNewlines)
    {
        var success = ContactMessage.TryCreate(
            nameWithNewlines,
            "jane@example.com",
            "Hello Rafael, this is a test inquiry.",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("newline", error);
    }

    [Fact]
    public void TryCreate_fails_when_name_exceeds_max_length()
    {
        var longName = new string('A', 101);
        var success = ContactMessage.TryCreate(
            longName,
            "jane@example.com",
            "Hello Rafael, this is a test inquiry.",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("maximum length of 100", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_fails_when_email_is_empty(string? email)
    {
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            email,
            "Hello Rafael, this is a test inquiry.",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("Email address is required", error);
    }

    [Theory]
    [InlineData("jane\r@example.com")]
    [InlineData("jane\n@example.com")]
    public void TryCreate_fails_when_email_contains_newlines(string emailWithNewlines)
    {
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            emailWithNewlines,
            "Hello Rafael, this is a test inquiry.",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("newline", error);
    }

    [Theory]
    [InlineData("jane@example.com, bob@example.com")]
    [InlineData("jane@example.com; bob@example.com")]
    [InlineData("Jane <jane@example.com>")]
    public void TryCreate_fails_when_multiple_mailboxes_or_angles_attempted(string invalidMailbox)
    {
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            invalidMailbox,
            "Hello Rafael, this is a test inquiry.",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("single email mailbox", error);
    }

    [Theory]
    [InlineData("invalid-email")]
    [InlineData("user@")]
    [InlineData("@example.com")]
    [InlineData("user@example")]
    public void TryCreate_fails_when_email_format_is_invalid(string invalidEmail)
    {
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            invalidEmail,
            "Hello Rafael, this is a test inquiry.",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("format is invalid", error);
    }

    [Fact]
    public void TryCreate_fails_when_email_exceeds_max_length()
    {
        var longEmail = $"{new string('a', 250)}@example.com";
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            longEmail,
            "Hello Rafael, this is a test inquiry.",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("maximum length of 254", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_fails_when_message_is_empty(string? messageText)
    {
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            "jane@example.com",
            messageText,
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("Message is required", error);
    }

    [Fact]
    public void TryCreate_fails_when_message_is_too_short()
    {
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            "jane@example.com",
            "Too short",
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("at least 10 characters", error);
    }

    [Fact]
    public void TryCreate_fails_when_message_exceeds_max_length()
    {
        var longMessage = new string('X', 5001);
        var success = ContactMessage.TryCreate(
            "Jane Doe",
            "jane@example.com",
            longMessage,
            consent: true,
            out var message,
            out var error);

        Assert.False(success);
        Assert.Null(message);
        Assert.Contains("maximum length of 5000", error);
    }
}
