using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rafael.Portfolio.Modules.Assistant.Application;
using Rafael.Portfolio.Modules.Contact.Application;
using Rafael.Portfolio.Modules.Contact.Infrastructure;
using Rafael.Portfolio.Web.Endpoints;

namespace Rafael.Portfolio.UnitTests;

// Drives POST /v1/contact over a real Kestrel listener (loopback, ephemeral port), where
// synchronous I/O is disallowed exactly as in production. The only fake is the provider
// transport behind the real CloudflareEmailRelay, so no network call or email can happen.
public sealed class ContactEndpointTests
{
    private const int Limit = 64 * 1024;

    private const string DeliveredEnvelope =
        """{"success":true,"result":{"delivered":["recipient@example.com"],"queued":[]}}""";

    private const string QueuedEnvelope =
        """{"success":true,"result":{"delivered":[],"queued":["recipient@example.com"]}}""";

    private sealed class FakeProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _calls;

        public int CallCount => Volatile.Read(ref _calls);

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return await responder(request, cancellationToken);
        }
    }

    private sealed class AlwaysValidTurnstile : ITurnstileValidator
    {
        public Task<bool> ValidateAsync(string? token, string? remoteIp, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    // HttpContent whose length is unknown, so HttpClient sends it chunked without Content-Length.
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ContactHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ContactHost(WebApplication app, HttpClient client, FakeProvider provider)
        {
            _app = app;
            Client = client;
            Provider = provider;
        }

        public HttpClient Client { get; }

        public FakeProvider Provider { get; }

        public static async Task<ContactHost> StartAsync(
            FakeProvider provider,
            bool enabled = true,
            int attemptsPerWindow = 100,
            TimeSpan? providerDeadline = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var options = new ContactOptions
            {
                Enabled = enabled,
                AccountId = "test-account-id",
                ApiToken = "test-api-token",
                SenderEmail = "sender@example.com",
                RecipientEmail = "recipient@example.com"
            };

            var relay = new CloudflareEmailRelay(
                new HttpClient(provider),
                options,
                providerDeadline ?? CloudflareEmailRelay.DefaultDeadline,
                CloudflareEmailRelay.DefaultMaxResponseBytes);

            builder.Services.AddSingleton<IContactRelay>(relay);
            builder.Services.AddSingleton<IContactRateLimiter>(new InMemoryContactRateLimiter(attemptsPerWindow, TimeSpan.FromMinutes(10)));
            builder.Services.AddSingleton<IContactService, ContactService>();
            builder.Services.AddSingleton<ITurnstileValidator, AlwaysValidTurnstile>();

            var app = builder.Build();
            app.MapGroup("/v1").MapContactEndpoints();
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            return new ContactHost(app, client, provider);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static byte[] ValidBody(string? pad = null)
    {
        var padding = pad is null ? string.Empty : $",\"pad\":\"{pad}\"";
        return Encoding.UTF8.GetBytes(
            "{\"name\":\"Alice Visitor\",\"email\":\"alice@example.com\"," +
            "\"message\":\"Inquiring about systems architecture.\",\"consent\":true" + padding + "}");
    }

    // A valid request that is exactly `size` bytes, padded with single-byte characters.
    private static byte[] ValidBodyOfSize(int size)
    {
        var baseLength = ValidBody("").Length;
        return ValidBody(new string('a', size - baseLength));
    }

    private static HttpRequestMessage Post(HttpContent content) =>
        new(HttpMethod.Post, "/v1/contact") { Content = content };

    private static HttpContent Json(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        return content;
    }

    private static HttpContent Chunked(byte[] bytes)
    {
        var content = new UnknownLengthContent(bytes);
        content.Headers.ContentType = new("application/json");
        return content;
    }

    private static HttpResponseMessage Provider(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Successful_contact_is_delivered_over_http_through_the_fake_provider()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))));

        using var response = await host.Client.SendAsync(Post(Json(ValidBody())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("delivered", json.GetProperty("status").GetString());
        Assert.Equal("delivered", json.GetProperty("outcome").GetString());
        Assert.Equal(1, host.Provider.CallCount);

        using var sent = JsonDocument.Parse(host.Provider.LastBody!);
        Assert.Equal("Portfolio contact message", sent.RootElement.GetProperty("subject").GetString());
        Assert.Equal("alice@example.com", sent.RootElement.GetProperty("reply_to").GetString());
        Assert.Equal("recipient@example.com", sent.RootElement.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Queued_provider_outcome_is_reported_as_queued()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, QueuedEnvelope))));

        using var response = await host.Client.SendAsync(Post(Json(ValidBody())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("queued", json.GetProperty("status").GetString());
        Assert.Equal("queued", json.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Body_without_content_length_is_read_asynchronously_and_accepted()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))));

        using var request = Post(Chunked(ValidBody()));
        using var response = await host.Client.SendAsync(request);

        Assert.Null(request.Content!.Headers.ContentLength);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, host.Provider.CallCount);
    }

    [Fact]
    public async Task Body_of_exactly_64_kib_is_accepted_and_one_byte_more_is_rejected_with_and_without_length()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))));

        using var atLimit = await host.Client.SendAsync(Post(Json(ValidBodyOfSize(Limit))));
        using var atLimitChunked = await host.Client.SendAsync(Post(Chunked(ValidBodyOfSize(Limit))));
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, atLimitChunked.StatusCode);
        Assert.Equal(2, host.Provider.CallCount);

        using var over = await host.Client.SendAsync(Post(Json(ValidBodyOfSize(Limit + 1))));
        using var overChunked = await host.Client.SendAsync(Post(Chunked(ValidBodyOfSize(Limit + 1))));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overChunked.StatusCode);
        Assert.Equal(2, host.Provider.CallCount);
    }

    [Fact]
    public async Task Limit_is_counted_in_bytes_so_multibyte_text_cannot_slip_past_it()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))));

        // 32,769 two-byte characters: fewer than 64 Ki characters, but more than 64 KiB.
        var multibyte = ValidBody(new string('é', 32_769));
        Assert.True(multibyte.Length > Limit);
        Assert.True(Encoding.UTF8.GetString(multibyte).Length < Limit);

        using var chunked = await host.Client.SendAsync(Post(Chunked(multibyte)));
        using var sized = await host.Client.SendAsync(Post(Json(multibyte)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, sized.StatusCode);
        Assert.Equal(0, host.Provider.CallCount);

        // The same character count just under the byte limit is accepted.
        var under = ValidBody(new string('é', 32_000));
        Assert.True(under.Length < Limit);
        using var accepted = await host.Client.SendAsync(Post(Chunked(under)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    public async Task Empty_or_blank_body_is_a_bad_request_without_a_provider_call(string body)
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))));

        using var response = await host.Client.SendAsync(Post(Chunked(Encoding.UTF8.GetBytes(body))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Provider.CallCount);
    }

    [Fact]
    public async Task Malformed_json_and_invalid_utf8_are_bad_requests_without_a_provider_call()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))));

        using var malformed = await host.Client.SendAsync(Post(Chunked(Encoding.UTF8.GetBytes("{not json"))));
        using var invalidUtf8 = await host.Client.SendAsync(
            Post(Chunked([(byte)'{', (byte)'"', 0xC3, 0x28, (byte)'"', (byte)':', (byte)'1', (byte)'}'])));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidUtf8.StatusCode);
        Assert.Equal(0, host.Provider.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "provider_rejected")]
    [InlineData(HttpStatusCode.InternalServerError, "provider_error")]
    public async Task Provider_http_error_with_a_success_envelope_is_a_failure_and_is_not_retried(
        HttpStatusCode providerStatus,
        string expectedOutcome)
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(providerStatus, DeliveredEnvelope))));

        using var response = await host.Client.SendAsync(Post(Json(ValidBody())));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(expectedOutcome, (await ReadJson(response)).GetProperty("outcome").GetString());
        Assert.Equal(1, host.Provider.CallCount);
    }

    [Fact]
    public async Task Provider_timeout_is_a_failure_and_the_message_is_not_resent()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Provider(HttpStatusCode.OK, DeliveredEnvelope);
            }),
            providerDeadline: TimeSpan.FromMilliseconds(200));

        using var response = await host.Client.SendAsync(Post(Json(ValidBody())));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("timeout", (await ReadJson(response)).GetProperty("outcome").GetString());
        Assert.Equal(1, host.Provider.CallCount);
    }

    [Fact]
    public async Task Disabled_contact_returns_unavailable_without_a_provider_call()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))),
            enabled: false);

        using var response = await host.Client.SendAsync(Post(Json(ValidBody())));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("unavailable", (await ReadJson(response)).GetProperty("outcome").GetString());
        Assert.Equal(0, host.Provider.CallCount);
    }

    [Fact]
    public async Task Each_submission_reaches_the_provider_exactly_once_including_a_double_submission()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))));

        // The backend holds no receipts (zero persistence), so two submissions are two
        // deliveries; preventing an accidental double submit is the form's job. What the
        // backend guarantees is that it never adds a send of its own.
        var both = await Task.WhenAll(
            host.Client.SendAsync(Post(Json(ValidBody()))),
            host.Client.SendAsync(Post(Json(ValidBody()))));

        Assert.All(both, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(2, host.Provider.CallCount);
        foreach (var response in both)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Contact_rate_limit_stops_further_submissions_before_the_provider()
    {
        await using var host = await ContactHost.StartAsync(
            new FakeProvider((_, _) => Task.FromResult(Provider(HttpStatusCode.OK, DeliveredEnvelope))),
            attemptsPerWindow: 3);

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await host.Client.SendAsync(Post(Json(ValidBody())));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
            statuses);
        Assert.Equal(3, host.Provider.CallCount);
    }

    [Fact]
    public async Task Bounded_reader_never_uses_synchronous_reads_and_stops_at_the_limit()
    {
        var stream = new AsyncOnlyStream(new byte[100_000]);

        var result = await ContactEndpoints.ReadBoundedBodyAsync(stream, Limit, CancellationToken.None);

        Assert.Null(result);
        Assert.InRange(stream.BytesServed, Limit + 1, Limit + 8192);
    }

    [Fact]
    public async Task Bounded_reader_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var stream = new AsyncOnlyStream([], blockForever: true);

        var pending = ContactEndpoints.ReadBoundedBodyAsync(stream, Limit, cts.Token);
        cts.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class AsyncOnlyStream(byte[] data, bool blockForever = false) : Stream
    {
        private long _position;

        public long BytesServed => _position;

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
            if (blockForever)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var count = (int)Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory((int)_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous reads are not allowed.");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
