using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Rafael.Portfolio.Modules.Contact.Domain;
using Rafael.Portfolio.Modules.Contact.Infrastructure;

namespace Rafael.Portfolio.UnitTests;

// Test-only fake transport: every case drives the real CloudflareEmailRelay through an
// HttpMessageHandler stub, so nothing here can reach a network or send an email.
public sealed class CloudflareEmailRelayBoundsTests
{
    private const string DeliveredEnvelope =
        """{"success":true,"errors":[],"messages":[],"result":{"delivered":["recipient@example.com"],"permanent_bounces":[],"queued":[]}}""";

    private sealed class FakeTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return responder(request, cancellationToken);
        }
    }

    // A non-seekable body: HttpContent cannot know its length, so no Content-Length is sent.
    // It records how many bytes the relay actually pulled from it.
    private sealed class CountingBody(byte[] prefix, long endlessPaddingBytes, bool never = false) : Stream
    {
        private long _position;

        public long Served { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (never && _position >= prefix.Length)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var total = prefix.Length + endlessPaddingBytes;
            var count = (int)Math.Min(buffer.Length, total - _position);
            for (var i = 0; i < count; i++)
            {
                buffer.Span[i] = _position + i < prefix.Length ? prefix[_position + i] : (byte)' ';
            }

            _position += count;
            Served += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static ContactOptions Options() => new()
    {
        Enabled = true,
        AccountId = "test-account-id",
        ApiToken = "test-api-token",
        SenderEmail = "sender@example.com",
        RecipientEmail = "recipient@example.com"
    };

    private static ContactMessage Message()
    {
        Assert.True(ContactMessage.TryCreate(
            "Alice Visitor", "alice@example.com", "Inquiring about systems architecture.", true, out var msg, out _));
        return msg!;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Streamed(HttpStatusCode status, Stream body) =>
        new(status) { Content = new StreamContent(body) };

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "provider_rejected")]
    [InlineData(HttpStatusCode.NotFound, "provider_rejected")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "provider_rejected")]
    [InlineData(HttpStatusCode.InternalServerError, "provider_error")]
    [InlineData(HttpStatusCode.BadGateway, "provider_error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "provider_error")]
    public async Task Non_success_http_status_never_becomes_a_delivery_even_with_a_success_envelope(
        HttpStatusCode status,
        string expectedOutcome)
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(Json(status, DeliveredEnvelope)));
        using var client = new HttpClient(transport);

        var result = await new CloudflareEmailRelay(client, Options()).RelayAsync(Message());

        Assert.False(result.IsSuccessful);
        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal(expectedOutcome, result.OutcomeCode);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Success_status_with_a_success_envelope_for_the_configured_recipient_is_delivered()
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(Json(HttpStatusCode.OK, DeliveredEnvelope)));
        using var client = new HttpClient(transport);

        var result = await new CloudflareEmailRelay(client, Options()).RelayAsync(Message());

        Assert.Equal(ContactDeliveryStatus.Delivered, result.Status);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Valid_envelope_padded_to_one_mebibyte_is_rejected_by_size()
    {
        var padded = DeliveredEnvelope + new string(' ', 1024 * 1024);
        var transport = new FakeTransport((_, _) => Task.FromResult(Json(HttpStatusCode.OK, padded)));
        using var client = new HttpClient(transport);

        var result = await new CloudflareEmailRelay(client, Options()).RelayAsync(Message());

        Assert.Equal(ContactDeliveryStatus.Failed, result.Status);
        Assert.Equal("provider_response_too_large", result.OutcomeCode);
    }

    [Fact]
    public async Task Response_without_content_length_is_cut_off_at_the_limit_instead_of_buffered()
    {
        const int limit = 4096;
        var body = new CountingBody(Encoding.UTF8.GetBytes(DeliveredEnvelope), endlessPaddingBytes: 512L * 1024 * 1024);
        var transport = new FakeTransport((_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, body)));
        using var client = new HttpClient(transport);

        var result = await new CloudflareEmailRelay(client, Options(), TimeSpan.FromSeconds(10), limit)
            .RelayAsync(Message());

        Assert.Equal("provider_response_too_large", result.OutcomeCode);
        Assert.InRange(body.Served, limit, limit + 8192);
    }

    [Fact]
    public async Task Response_exactly_at_the_limit_is_accepted_and_one_byte_more_is_not()
    {
        const int limit = 1024;
        var envelope = Encoding.UTF8.GetBytes(DeliveredEnvelope);

        async Task<ContactDeliveryResult> Relay(int size)
        {
            var body = new CountingBody(envelope, size - envelope.Length);
            var transport = new FakeTransport((_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, body)));
            using var client = new HttpClient(transport);
            return await new CloudflareEmailRelay(client, Options(), TimeSpan.FromSeconds(10), limit).RelayAsync(Message());
        }

        Assert.Equal(ContactDeliveryStatus.Delivered, (await Relay(limit)).Status);
        Assert.Equal("provider_response_too_large", (await Relay(limit + 1)).OutcomeCode);
    }

    [Fact]
    public async Task Declared_content_length_over_the_limit_is_rejected_without_reading_the_body()
    {
        var body = new CountingBody([], 0);
        var transport = new FakeTransport((_, _) =>
        {
            var response = Streamed(HttpStatusCode.OK, body);
            response.Content.Headers.ContentLength = 10 * 1024 * 1024;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(transport);

        var result = await new CloudflareEmailRelay(client, Options()).RelayAsync(Message());

        Assert.Equal("provider_response_too_large", result.OutcomeCode);
        Assert.Equal(0, body.Served);
    }

    [Fact]
    public async Task Deadline_applies_while_waiting_for_response_headers_and_is_not_retried()
    {
        var transport = new FakeTransport(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, DeliveredEnvelope);
        });
        using var client = new HttpClient(transport);
        var relay = new CloudflareEmailRelay(client, Options(), TimeSpan.FromMilliseconds(150), 4096);

        var clock = Stopwatch.StartNew();
        var result = await relay.RelayAsync(Message());

        Assert.Equal("timeout", result.OutcomeCode);
        Assert.Equal(1, transport.CallCount);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The deadline must bound the call.");
    }

    [Fact]
    public async Task Deadline_applies_while_reading_the_response_body_and_is_not_retried()
    {
        var body = new CountingBody(Encoding.UTF8.GetBytes("{\"success\":"), 0, never: true);
        var transport = new FakeTransport((_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, body)));
        using var client = new HttpClient(transport);
        var relay = new CloudflareEmailRelay(client, Options(), TimeSpan.FromMilliseconds(150), 4096);

        var clock = Stopwatch.StartNew();
        var result = await relay.RelayAsync(Message());

        Assert.Equal("timeout", result.OutcomeCode);
        Assert.Equal(1, transport.CallCount);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The deadline must bound the body read.");
    }

    [Fact]
    public async Task Caller_cancellation_during_the_body_read_is_reported_as_canceled()
    {
        using var cts = new CancellationTokenSource();
        var body = new CountingBody(Encoding.UTF8.GetBytes("{\"success\":"), 0, never: true);
        var transport = new FakeTransport((_, _) => Task.FromResult(Streamed(HttpStatusCode.OK, body)));
        using var client = new HttpClient(transport);
        var relay = new CloudflareEmailRelay(client, Options(), TimeSpan.FromSeconds(30), 4096);

        var pending = relay.RelayAsync(Message(), cts.Token);
        cts.CancelAfter(100);
        var result = await pending;

        Assert.Equal("canceled", result.OutcomeCode);
        Assert.Equal(1, transport.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Failed_or_ambiguous_attempts_are_never_retried_automatically(HttpStatusCode status)
    {
        var transport = new FakeTransport((_, _) => Task.FromResult(Json(status, "{}")));
        using var client = new HttpClient(transport);
        var relay = new CloudflareEmailRelay(client, Options());

        await relay.RelayAsync(Message());

        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Malformed_and_empty_bodies_with_success_status_are_failures_not_deliveries()
    {
        foreach (var body in new[] { "", "   ", "not json", "[]", "{\"success\":true}", "{\"success\":true,\"result\":{}}" })
        {
            var transport = new FakeTransport((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body)));
            using var client = new HttpClient(transport);

            var result = await new CloudflareEmailRelay(client, Options()).RelayAsync(Message());

            Assert.False(result.IsSuccessful, $"Body '{body}' must not be a success.");
            Assert.Equal(1, transport.CallCount);
        }
    }

    [Fact]
    public async Task Subject_is_static_and_visitor_text_stays_out_of_headers_and_subject()
    {
        string? captured = null;
        var transport = new FakeTransport(async (request, ct) =>
        {
            captured = await request.Content!.ReadAsStringAsync(ct);
            return Json(HttpStatusCode.OK, DeliveredEnvelope);
        });
        using var client = new HttpClient(transport);
        Assert.True(ContactMessage.TryCreate(
            "Mallory Visitor", "mallory@example.com", "Please reply about the project.", true, out var msg, out _));

        await new CloudflareEmailRelay(client, Options()).RelayAsync(msg!);

        using var doc = JsonDocument.Parse(captured!);
        var root = doc.RootElement;
        Assert.Equal("Portfolio contact message", root.GetProperty("subject").GetString());
        Assert.DoesNotContain("Mallory", root.GetProperty("subject").GetString());
        Assert.Equal("mallory@example.com", root.GetProperty("reply_to").GetString());
        Assert.Contains("Mallory Visitor", root.GetProperty("text").GetString());
        Assert.Equal(
            new[] { "from", "reply_to", "subject", "text", "to" },
            root.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }
}
