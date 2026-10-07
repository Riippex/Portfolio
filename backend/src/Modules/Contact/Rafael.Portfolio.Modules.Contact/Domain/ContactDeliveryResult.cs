namespace Rafael.Portfolio.Modules.Contact.Domain;

public sealed record ContactDeliveryResult(
    ContactDeliveryStatus Status,
    string OutcomeCode,
    string? ErrorMessage = null)
{
    public bool IsSuccessful => Status is ContactDeliveryStatus.Delivered or ContactDeliveryStatus.Queued;

    public static ContactDeliveryResult Delivered() =>
        new(ContactDeliveryStatus.Delivered, "delivered");

    public static ContactDeliveryResult Queued() =>
        new(ContactDeliveryStatus.Queued, "queued");

    public static ContactDeliveryResult Unavailable(string? message = null) =>
        new(ContactDeliveryStatus.Unavailable, "unavailable", message);

    public static ContactDeliveryResult Failed(string outcomeCode, string? message = null) =>
        new(ContactDeliveryStatus.Failed, outcomeCode, message);
}
