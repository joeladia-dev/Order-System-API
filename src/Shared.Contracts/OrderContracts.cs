namespace Shared.Contracts;

public enum OrderStatus
{
    Pending,
    InventoryReserved,
    PaymentProcessing,
    PaymentCompleted,
    PaymentFailed,
    Shipped,
    Delivered,
    Cancelled,
    InventoryFailed
}

public sealed record EventMetadata(
    Guid EventId,
    Guid CorrelationId,
    string EventName,
    DateTimeOffset OccurredAt,
    int Version = 1);

public sealed record OrderItem(string ProductId, int Quantity);

public sealed record OrderCreated(
    EventMetadata Metadata,
    Guid OrderId,
    string CustomerId,
    string ShippingAddress,
    IReadOnlyCollection<OrderItem> Items,
    string PaymentMethod);

public sealed record OrderCancelled(
    EventMetadata Metadata,
    Guid OrderId,
    string Reason);

public sealed record InventoryReserved(
    EventMetadata Metadata,
    Guid OrderId,
    IReadOnlyCollection<OrderItem> ReservedItems);

public sealed record InventoryFailed(
    EventMetadata Metadata,
    Guid OrderId,
    string Reason);

public sealed record PaymentCompleted(
    EventMetadata Metadata,
    Guid OrderId,
    decimal Amount,
    string TransactionId);

public sealed record PaymentFailed(
    EventMetadata Metadata,
    Guid OrderId,
    string Reason);

public sealed record OrderShipped(
    EventMetadata Metadata,
    Guid OrderId,
    string TrackingNumber,
    DateTimeOffset EstimatedDeliveryDate);

public sealed record OrderDelivered(
    EventMetadata Metadata,
    Guid OrderId,
    DateTimeOffset DeliveredAt);
