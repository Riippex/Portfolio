using Rafael.Portfolio.Modules.Contact.Application;
using Rafael.Portfolio.Modules.Contact.Domain;

namespace Rafael.Portfolio.UnitTests;

public sealed class ContactServiceTests
{
    private sealed class FakeContactRelay : IContactRelay
    {
        public ContactMessage? LastMessage { get; private set; }
        public ContactDeliveryResult ResultToReturn { get; set; } = ContactDeliveryResult.Delivered();

        public Task<ContactDeliveryResult> RelayAsync(ContactMessage message, CancellationToken cancellationToken = default)
        {
            LastMessage = message;
            return Task.FromResult(ResultToReturn);
        }
    }

    [Fact]
    public void Constructor_throws_when_relay_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => new ContactService(null!));
    }

    [Fact]
    public async Task SendContactMessageAsync_throws_when_message_is_null()
    {
        var relay = new FakeContactRelay();
        var service = new ContactService(relay);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SendContactMessageAsync(null!));
    }

    [Fact]
    public async Task SendContactMessageAsync_delegates_to_relay()
    {
        var relay = new FakeContactRelay();
        var service = new ContactService(relay);

        Assert.True(ContactMessage.TryCreate(
            "Alice",
            "alice@example.com",
            "Hello from unit tests.",
            consent: true,
            out var message,
            out _));

        var result = await service.SendContactMessageAsync(message!);

        Assert.Equal(ContactDeliveryStatus.Delivered, result.Status);
        Assert.Same(message, relay.LastMessage);
    }
}
