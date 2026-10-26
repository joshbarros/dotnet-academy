using System.Text.Json;

namespace Academy.Shared.Messaging;

public record ReserveSeats(Guid EnrollmentId, Guid OwnerId, Guid CohortId, int Seats, string Reference);
public record SeatsHeld(Guid EnrollmentId, Guid OwnerId, Guid CohortId, int Seats, string Reference);
public record SeatsShort(Guid EnrollmentId, string Reference, int RequestedSeats, int SeatsLeft);
public record ReleaseSeats(Guid EnrollmentId, string Reason);
public record SeatsReleased(Guid EnrollmentId, int Seats, string Reason);
public record PassIssued(Guid EnrollmentId, string Number);
public record PassRejected(Guid EnrollmentId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
