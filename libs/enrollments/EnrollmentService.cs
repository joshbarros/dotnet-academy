using Academy.Shared;
using Academy.Shared.Auth;
using Academy.Shared.Contracts;
using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Academy.Enrollments;

/// <summary>HTTP side of the saga: writes the enrollment and its first outbox row in one local transaction.</summary>
public class EnrollmentService(EnrollmentsDbContext db, ICohortCatalog cohorts, ICurrentUser user)
{
    public async Task<Enrollment> CreateAsync(CreateEnrollmentRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.CohortCode))
            throw new DomainException(400, "Cohort code is required.");
        var error = EnrollmentRules.Validate(request.Reference, request.Seats);
        if (error is not null)
            throw new DomainException(400, error);

        var cohort = await cohorts.FindAsync(request.CohortCode.Trim(), ct)
            ?? throw new DomainException(404, $"Cohort '{request.CohortCode}' does not exist.");

        var seats = (int)request.Seats;
        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var enrollment = new Enrollment
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("enrollments", Topology.ReserveKey, enrollment.Id, new ReserveSeats(enrollment.Id, ownerId, cohort.Id, seats, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Enrollment> GetAsync(Guid id, CancellationToken ct)
    {
        var enrollment = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (enrollment is null || !OwnerAccess.CanAccess(user, enrollment.OwnerId))
            throw new DomainException(404, "Enrollment not found.");
        return enrollment;
    }

    public async Task<List<Enrollment>> ListAsync(string? cohortCode, CancellationToken ct)
    {
        var query = db.Enrollments.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see enrollments.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(cohortCode))
            query = query.Where(b => b.CohortCode == cohortCode);
        return await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
    }

    /// <summary>Moves Held or Enrolled to Withdrawn and asks Cohorts to give the seats back. The access pass is not touched.</summary>
    public async Task<Enrollment> WithdrawAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Enrollments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"An enrollment in status {current.Status} cannot be withdrawn.");
        }

        db.Enqueue("enrollments", Topology.ReleaseKey, id, new ReleaseSeats(id, "withdrawal"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Enrollments.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
