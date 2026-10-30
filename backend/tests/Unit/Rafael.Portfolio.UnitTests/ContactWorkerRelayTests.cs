using System.Net;
using System.Text;
using System.Text.Json;
using Rafael.Portfolio.Modules.Contact.Domain;
using Rafael.Portfolio.Modules.Contact.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class ContactWorkerRelayTests
{
    private const string Endpoint = "https://rafael-portfolio-contact-dev.example.workers.dev/v1/contact-relay/dev";
    private static ContactOptions Options() => new()
    {
        Enabled = true,
        RelayMode = "worker",
        WorkerUrl = Endpoint,
        ApiToken = new string('a', 64),
        SenderEmail = "sender@example.com",
        RecipientEmail = "recipient@example.net"
    };

    private sealed class Handler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Endpoint { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Endpoint = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static ContactMessage Message()
    {
        Assert.True(ContactMessage.TryCreate("Visitor", "visitor@example.org", "Hello about your portfolio.", true, out var message, out _));
        return message!;
    }

    [Fact]
    public async Task Worker_uses_only_configured_endpoint_and_existing_bounded_wire_contract()
    {
        var options = Options();
        options.Validate();
        options.ValidateStage("dev");
        Assert.Null(options.AccountId);
        var handler = new Handler(HttpStatusCode.OK, """{"success":true,"result":{"queued":["recipient@example.net"]}}""");
        using var client = new HttpClient(handler);
        var result = await new CloudflareEmailRelay(client, options).RelayAsync(Message());
        Assert.Equal(ContactDeliveryStatus.Queued, result.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(Endpoint, handler.Endpoint!.AbsoluteUri);
        Assert.Equal("Bearer " + options.ApiToken, handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(options.RecipientEmail, body.RootElement.GetProperty("to").GetString());
        Assert.Equal(options.SenderEmail, body.RootElement.GetProperty("from").GetString());
        Assert.Equal("visitor@example.org", body.RootElement.GetProperty("reply_to").GetString());
        Assert.Equal(CloudflareEmailRelay.Subject, body.RootElement.GetProperty("subject").GetString());
    }

    [Theory]
    [InlineData("http://rafael-portfolio-contact-dev.example.workers.dev/v1/contact-relay/dev")]
    [InlineData("https://attacker.example/v1/contact-relay/dev")]
    [InlineData("https://rafael-portfolio-contact-dev.example.workers.dev.attacker.example/v1/contact-relay/dev")]
    [InlineData("https://rafael-portfolio-contact-dev.example.workers.dev/v1/contact-relay/prod")]
    [InlineData("https://rafael-portfolio-contact-dev.example.workers.dev/v1/contact-relay/dev?recipient=other")]
    [InlineData("https://rafael-portfolio-contact-dev.example.workers.dev/v1/contact-relay/dev#fragment")]
    [InlineData("userinfo")]
    [InlineData("https://rafael-portfolio-contact-dev.example.workers.dev:444/v1/contact-relay/dev")]
    [InlineData(null)]
    public async Task Unsafe_endpoint_fails_startup_and_never_sends(string? endpoint)
    {
        if (endpoint == "userinfo") endpoint = new UriBuilder(Endpoint) { UserName = "user" }.Uri.AbsoluteUri;
        var options = Options();
        options.WorkerUrl = endpoint;
        Assert.Throws<InvalidOperationException>(options.Validate);
        var handler = new Handler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        Assert.Equal(ContactDeliveryStatus.Unavailable, (await new CloudflareEmailRelay(client, options).RelayAsync(Message())).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("short")]
    [InlineData(null)]
    public void Worker_requires_distinct_strong_capability(string? token)
    {
        var options = Options();
        options.ApiToken = token;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("prod")]
    [InlineData("unknown")]
    public void Worker_cannot_cross_host_stage(string stage) =>
        Assert.Throws<InvalidOperationException>(() => Options().ValidateStage(stage));

    [Theory]
    [InlineData(HttpStatusCode.Redirect, "provider_rejected")]
    [InlineData(HttpStatusCode.Forbidden, "provider_auth_error")]
    [InlineData(HttpStatusCode.TooManyRequests, "provider_rate_limited")]
    [InlineData(HttpStatusCode.BadGateway, "provider_error")]
    public async Task Bridge_failures_never_become_delivered_and_are_not_retried(HttpStatusCode code, string outcome)
    {
        var handler = new Handler(code, """{"success":true,"result":{"delivered":["recipient@example.net"]}}""");
        using var client = new HttpClient(handler);
        var result = await new CloudflareEmailRelay(client, Options()).RelayAsync(Message());
        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal(outcome, result.OutcomeCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Unknown_mode_never_dispatches()
    {
        var options = Options();
        options.RelayMode = "unknown";
        Assert.Throws<InvalidOperationException>(options.Validate);
        var handler = new Handler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        Assert.Equal(ContactDeliveryStatus.Unavailable, (await new CloudflareEmailRelay(client, options).RelayAsync(Message())).Status);
        Assert.Equal(0, handler.Calls);
    }
}
