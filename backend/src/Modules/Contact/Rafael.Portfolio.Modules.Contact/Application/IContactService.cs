using Rafael.Portfolio.Modules.Contact.Domain;

namespace Rafael.Portfolio.Modules.Contact.Application;

public interface IContactService
{
    Task<ContactDeliveryResult> SendContactMessageAsync(ContactMessage message, CancellationToken cancellationToken = default);
}
