namespace Academy.Api.Modules.Cohorts;

public class Cohort
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Track { get; set; } = "";
    public string Instructor { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public int SeatCapacity { get; set; }
    public int SeatsLeft { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class SeatHold
{
    public Guid EnrollmentId { get; set; }
    public Guid CohortId { get; set; }
    public int Seats { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
