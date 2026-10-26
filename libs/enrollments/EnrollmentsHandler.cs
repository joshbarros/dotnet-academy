using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Academy.Enrollments;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move an enrollment forward. Each step runs only from the status it expects.</summary>
public class EnrollmentsHandler(EnrollmentsDbContext db, IOptions<EnrollmentOptions> options) : IMessageHandler
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
