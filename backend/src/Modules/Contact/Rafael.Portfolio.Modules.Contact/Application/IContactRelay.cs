using Rafael.Portfolio.Modules.Contact.Domain;

namespace Rafael.Portfolio.Modules.Contact.Application;

public interface IContactRelay
{
    Task<ContactDeliveryResult> RelayAsync(ContactMessage message, CancellationToken cancellationToken = default);
}
