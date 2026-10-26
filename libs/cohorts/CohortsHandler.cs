using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Academy.Cohorts;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of Cohort.SeatsLeft and of seat holds.
/// The reservation is one conditional UPDATE: it matches only while the remaining Seats still covers the request.
/// </summary>
public class CohortsHandler(CohortsDbContext db) : IMessageHandler
{
    public const string Name = "cohorts";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveSeats>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseSeats>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Cohorts does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveSeats command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var updated = await db.Cohorts
            .Where(s => s.Id == command.CohortId && s.SeatsLeft >= command.Seats)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeatsLeft, x => x.SeatsLeft - command.Seats), ct);

        ReserveOutcome outcome;
        if (updated == 1)
        {
            db.Holds.Add(new SeatHold
            {
                EnrollmentId = command.EnrollmentId,
                CohortId = command.CohortId,
                Seats = command.Seats,
                Status = HoldStatus.Held,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("cohorts", Topology.HeldKey, command.EnrollmentId,
                new SeatsHeld(command.EnrollmentId, command.OwnerId, command.CohortId, command.Seats, command.Reference));
            outcome = ReserveOutcome.Held;
        }
        else
        {
            var remaining = await db.Cohorts.Where(s => s.Id == command.CohortId)
                .Select(s => s.SeatsLeft).FirstOrDefaultAsync(ct);
            db.Enqueue("cohorts", Topology.ShortKey, command.EnrollmentId,
                new SeatsShort(command.EnrollmentId, command.Reference, command.Seats, remaining));
            outcome = ReserveOutcome.Short;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>Gives held seats back exactly once. A second release for the same enrollment changes nothing.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseSeats command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.EnrollmentId == command.EnrollmentId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.EnrollmentId == command.EnrollmentId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Cohorts
                    .Where(s => s.Id == hold.CohortId && s.SeatsLeft + hold.Seats <= s.SeatCapacity)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeatsLeft, x => x.SeatsLeft + hold.Seats), ct);
                db.Enqueue("cohorts", Topology.ReleasedKey, command.EnrollmentId,
                    new SeatsReleased(command.EnrollmentId, hold.Seats, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
