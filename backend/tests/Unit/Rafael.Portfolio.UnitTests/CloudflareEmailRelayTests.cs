using System.Net;
using System.Text;
using System.Text.Json;
using Rafael.Portfolio.Modules.Contact.Domain;
using Rafael.Portfolio.Modules.Contact.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

public sealed class CloudflareEmailRelayTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responder;
        private readonly Exception? _exception;

        public StubHttpMessageHandler(HttpStatusCode statusCode, string body)
        {
            _responder = _ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public StubHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_responder!(request));
        }
    }

    private static ContactOptions CreateValidOptions() => new()
    {
        Enabled = true,
        AccountId = "test-account-id",
        ApiToken = "test-api-token",
        SenderEmail = "sender@example.com",
        RecipientEmail = "recipient@example.com"
    };

    private static ContactMessage CreateValidMessage()
    {
        Assert.True(ContactMessage.TryCreate(
            "Alice Visitor",
            "alice@example.com",
            "Inquiring about systems architecture.",
            consent: true,
            out var msg,
            out _));
        return msg!;
    }

    [Fact]
    public async Task Disabled_relay_returns_unavailable_without_network_request()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        var options = CreateValidOptions();
        options.Enabled = false;

        var relay = new CloudflareEmailRelay(client, options);
        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Unavailable, result.Status);
        Assert.Equal("unavailable", result.OutcomeCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Incomplete_configuration_returns_unavailable_without_network_request()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        var options = new ContactOptions
        {
            Enabled = true,
            AccountId = null, // Missing
            ApiToken = "test-token",
            SenderEmail = "sender@example.com",
            RecipientEmail = "recipient@example.com"
        };

        var relay = new CloudflareEmailRelay(client, options);
        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Unavailable, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Delivered_response_sets_delivered_status()
    {
        var responseJson = """
        {
            "success": true,
            "errors": [],
            "messages": [],
            "result": {
                "delivered": ["recipient@example.com"],
                "permanent_bounces": [],
                "queued": []
            }
        }
        """;

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, responseJson);
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Delivered, result.Status);
        Assert.Equal("delivered", result.OutcomeCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Queued_response_sets_queued_status()
    {
        var responseJson = """
        {
            "success": true,
            "errors": [],
            "messages": [],
            "result": {
                "delivered": [],
                "permanent_bounces": [],
                "queued": ["recipient@example.com"]
            }
        }
        """;

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, responseJson);
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Queued, result.Status);
        Assert.Equal("queued", result.OutcomeCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Permanent_bounce_response_sets_failed_status()
    {
        var responseJson = """
        {
            "success": true,
            "errors": [],
            "messages": [],
            "result": {
                "delivered": [],
                "permanent_bounces": ["recipient@example.com"],
                "queued": []
            }
        }
        """;

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, responseJson);
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("permanent_bounce", result.OutcomeCode);
    }

    [Fact]
    public async Task Cloudflare_rejection_sets_provider_rejected_status()
    {
        var responseJson = """
        {
            "success": false,
            "errors": [
                {
                    "code": 10001,
                    "message": "email.sending.error.invalid_request_schema"
                }
            ],
            "messages": [],
            "result": null
        }
        """;

        var handler = new StubHttpMessageHandler(HttpStatusCode.BadRequest, responseJson);
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("provider_rejected", result.OutcomeCode);
    }

    [Fact]
    public async Task Malformed_json_response_sets_malformed_status()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "not-valid-json");
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("malformed_provider_response", result.OutcomeCode);
    }

    [Fact]
    public async Task Recipient_not_in_delivered_or_queued_sets_unconfirmed()
    {
        var responseJson = """
        {
            "success": true,
            "errors": [],
            "messages": [],
            "result": {
                "delivered": ["other-person@example.com"],
                "permanent_bounces": [],
                "queued": []
            }
        }
        """;

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, responseJson);
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("delivery_unconfirmed", result.OutcomeCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Auth_failure_sets_provider_auth_error(HttpStatusCode authStatus)
    {
        var handler = new StubHttpMessageHandler(authStatus, "{}");
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("provider_auth_error", result.OutcomeCode);
    }

    [Fact]
    public async Task Throttling_sets_provider_rate_limited()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.TooManyRequests, "{}");
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("provider_rate_limited", result.OutcomeCode);
    }

    [Fact]
    public async Task Timeout_sets_timeout_and_does_not_retry()
    {
        var handler = new StubHttpMessageHandler(new TimeoutException("Gateway timeout"));
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage());

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("timeout", result.OutcomeCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Cancellation_sets_canceled_status()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new StubHttpMessageHandler(new OperationCanceledException(cts.Token));
        using var client = new HttpClient(handler);
        var relay = new CloudflareEmailRelay(client, CreateValidOptions());

        var result = await relay.RelayAsync(CreateValidMessage(), cts.Token);

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("canceled", result.OutcomeCode);
    }

    [Fact]
    public async Task Outgoing_payload_has_correct_structure_and_reply_to()
    {
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                    "success": true,
                    "result": { "delivered": ["recipient@example.com"], "queued": [] }
                }
                """, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var options = CreateValidOptions();
        var relay = new CloudflareEmailRelay(client, options);

        var msg = CreateValidMessage();
        var result = await relay.RelayAsync(msg);

        Assert.True(result.IsSuccessful);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-token", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Contains("accounts/test-account-id/email/sending/send", handler.LastRequest.RequestUri?.ToString());

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody);
        var root = doc.RootElement;
        Assert.Equal(options.SenderEmail, root.GetProperty("from").GetString());
        Assert.Equal(options.RecipientEmail, root.GetProperty("to").GetString());
        Assert.Equal(msg.Email, root.GetProperty("reply_to").GetString());
        Assert.Contains(msg.Name, root.GetProperty("subject").GetString());
        Assert.Contains(msg.Message, root.GetProperty("text").GetString());
    }
}
