using Academy.Api.Data;
using Academy.Api.Messaging;
using Academy.Api.Modules.Cohorts;
using Academy.Api.Modules.Passes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Academy.Api.Modules.Enrollments;

public enum EnrollmentStatus { Requested, Held, Enrolled, Withdrawn, Rejected }

public class Enrollment
{
    public Guid Id { get; set; }
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

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreateEnrollmentRequest(string? CohortCode, string? Reference, long Seats);

public record EnrollmentDto(
    Guid Id, string CohortCode, string Reference, int Seats, long FeeCents, string Status,
    string? Reason, string? PassNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? SeatsReleasedAt)
{
    public static EnrollmentDto From(Enrollment b) => new(
        b.Id, b.CohortCode, b.Reference, b.Seats, b.FeeCents, b.Status.ToString(),
        b.Reason, b.PassNumber, b.CreatedAt, b.UpdatedAt, b.SeatsReleasedAt);
}

/// <summary>HTTP side of the saga: writes the enrollment and its first outbox row in one local transaction.</summary>
public class EnrollmentService(AcademyDbContext db)
{
    public async Task<Enrollment> CreateAsync(CreateEnrollmentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CohortCode))
            throw new DomainException(400, "Cohort code is required.");
        var error = EnrollmentRules.Validate(request.Reference, request.Seats);
        if (error is not null)
            throw new DomainException(400, error);

        var cohort = await db.Cohorts.AsNoTracking().FirstOrDefaultAsync(s => s.Code == request.CohortCode.Trim(), ct)
            ?? throw new DomainException(404, $"Cohort '{request.CohortCode}' does not exist.");

        var seats = (int)request.Seats;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var enrollment = new Enrollment
        {
            Id = Guid.CreateVersion7(),
            CohortId = cohort.Id,
            CohortCode = cohort.Code,
            Reference = reference,
            Seats = seats,
            FeeCents = EnrollmentRules.FeeCents(seats),
            Status = EnrollmentStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Enrollments.Add(enrollment);
        db.Enqueue("enrollments", Topology.ReserveKey, enrollment.Id, new ReserveSeats(enrollment.Id, cohort.Id, seats, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {cohort.Code}.");
        }
        await tx.CommitAsync(ct);
        return enrollment;
    }

    /// <summary>Moves Held or Enrolled to Withdrawn and asks Cohorts to give the seats back. The access pass is not touched.</summary>
    public async Task<Enrollment> WithdrawAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Enrollments
            .Where(b => b.Id == id && (b.Status == EnrollmentStatus.Held || b.Status == EnrollmentStatus.Enrolled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, EnrollmentStatus.Withdrawn)
                .SetProperty(b => b.Reason, "Withdrawn by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Enrollment not found.");
            throw new DomainException(409, $"An enrollment in status {current.Status} cannot be withdrawn.");
        }

        db.Enqueue("enrollments", Topology.ReleaseKey, id, new ReleaseSeats(id, "withdrawal"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Enrollments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move an enrollment forward. Each step runs only from the status it expects.</summary>
public class EnrollmentsHandler(AcademyDbContext db, IOptions<EnrollmentOptions> options) : IMessageHandler
{
    public const string Name = "enrollments";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<SeatsHeld>(d.Body).EnrollmentId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<SeatsShort>(d.Body), ct),
        Topology.IssuedKey => MarkEnrolledAsync(d.MessageId, Json.Parse<PassIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<PassRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<SeatsReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Enrollments does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid enrollmentId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Enrollments
            .Where(b => b.Id == enrollmentId && b.Status == EnrollmentStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, EnrollmentStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, SeatsShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Enrollments
            .Where(b => b.Id == m.EnrollmentId && b.Status == EnrollmentStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, EnrollmentStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedSeats} seats requested, {m.SeatsLeft} remaining.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a withdrawal got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkEnrolledAsync(Guid messageId, PassIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Enrollments
            .Where(b => b.Id == m.EnrollmentId && b.Status == EnrollmentStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, EnrollmentStatus.Enrolled)
                .SetProperty(b => b.PassNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            enrollmentIdForOrderCheck: m.EnrollmentId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, PassRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Enrollments
            .Where(b => b.Id == m.EnrollmentId && (b.Status == EnrollmentStatus.Held || b.Status == EnrollmentStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, EnrollmentStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, SeatsReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Enrollments
            .Where(b => b.Id == m.EnrollmentId && b.SeatsReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.SeatsReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? enrollmentIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && enrollmentIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Enrollments.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == EnrollmentStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
