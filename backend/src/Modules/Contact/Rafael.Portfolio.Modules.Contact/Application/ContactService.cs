using Rafael.Portfolio.Modules.Contact.Domain;

namespace Rafael.Portfolio.Modules.Contact.Application;

public sealed class ContactService : IContactService
{
    private readonly IContactRelay _contactRelay;

    public ContactService(IContactRelay contactRelay)
    {
        _contactRelay = contactRelay ?? throw new ArgumentNullException(nameof(contactRelay));
    }

    public Task<ContactDeliveryResult> SendContactMessageAsync(
        ContactMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _contactRelay.RelayAsync(message, cancellationToken);
    }
}
