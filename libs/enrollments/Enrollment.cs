namespace Academy.Enrollments;

public enum EnrollmentStatus { Requested, Held, Enrolled, Withdrawn, Rejected }

public class Enrollment
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid CohortId { get; set; }
    public string CohortCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Seats { get; set; }
    public long FeeCents { get; set; }
    public EnrollmentStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? PassNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SeatsReleasedAt { get; set; }
}

public class EnrollmentOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the withdrawal race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateEnrollmentRequest(string? CohortCode, string? Reference, long Seats);

public record EnrollmentDto(
    Guid Id, Guid OwnerId, string CohortCode, string Reference, int Seats, long FeeCents, string Status,
    string? Reason, string? PassNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? SeatsReleasedAt)
{
    public static EnrollmentDto From(Enrollment b) => new(
        b.Id, b.OwnerId, b.CohortCode, b.Reference, b.Seats, b.FeeCents, b.Status.ToString(),
        b.Reason, b.PassNumber, b.CreatedAt, b.UpdatedAt, b.SeatsReleasedAt);
}
