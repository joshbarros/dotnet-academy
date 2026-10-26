using Academy.Api.Data;
using Academy.Api.Messaging;
using Academy.Api.Modules.Enrollments;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api.Modules.Passes;

public enum PassStatus { Issued, Rejected }

public class AccessPass
{
    public Guid EnrollmentId { get; set; }
    public string? Number { get; set; }
    public PassStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the access pass once the seats are held. A poison reference is committed as rejected.</summary>
public class PassesHandler(AcademyDbContext db) : IMessageHandler
{
    public const string Name = "passes";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Passes does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<SeatsHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, SeatsHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (EnrollmentRules.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Passes.Add(new AccessPass
            {
                EnrollmentId = held.EnrollmentId,
                Status = PassStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("passes", Topology.ReleaseKey, held.EnrollmentId, new ReleaseSeats(held.EnrollmentId, "poison"));
            db.Enqueue("passes", Topology.RejectedKey, held.EnrollmentId, new PassRejected(held.EnrollmentId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"AP-{held.EnrollmentId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Passes.Add(new AccessPass
            {
                EnrollmentId = held.EnrollmentId,
                Number = number,
                Status = PassStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("passes", Topology.IssuedKey, held.EnrollmentId, new PassIssued(held.EnrollmentId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
