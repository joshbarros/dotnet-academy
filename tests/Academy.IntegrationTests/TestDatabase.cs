using Academy.Enrollments;
using Academy.Cohorts;
using Academy.Passes;
using Academy.Shared.Auth;
using Academy.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Academy.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ACADEMY_TEST_DB")
        ?? "Host=localhost;Port=5474;Database=academy_test;Username=academy;Password=academy";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static EnrollmentsDbContext Enrollments() => new(Options<EnrollmentsDbContext>(EnrollmentsDbContext.SchemaName));
    public static CohortsDbContext Cohorts() => new(Options<CohortsDbContext>(CohortsDbContext.SchemaName));
    public static PassesDbContext Passes() => new(Options<PassesDbContext>(PassesDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var enrollments = Enrollments();
        await using var seats = Cohorts();
        await using var passes = Passes();
        await enrollments.Database.MigrateAsync();
        await seats.Database.MigrateAsync();
        await passes.Database.MigrateAsync();
        await enrollments.Database.ExecuteSqlRawAsync(
            "TRUNCATE enrollments.\"InboxMessages\", enrollments.\"OutboxMessages\", enrollments.\"Enrollments\", " +
            "seats.\"InboxMessages\", seats.\"OutboxMessages\", seats.\"SeatHolds\", seats.\"Cohorts\", " +
            "passes.\"InboxMessages\", passes.\"OutboxMessages\", passes.\"AccessPasses\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Cohort> AddCohortAsync(int seatCapacity)
    {
        await using var db = Cohorts();
        var cohort = new Cohort
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Title = "MV Test",
            Track = "Engineering",
            Instructor = "Ada Lovelace",
            StartsAt = DateTimeOffset.UtcNow.AddDays(7),
            SeatCapacity = seatCapacity,
            SeatsLeft = seatCapacity,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Cohorts.Add(cohort);
        await db.SaveChangesAsync();
        return cohort;
    }

    public static async Task<Enrollment> AddEnrollmentAsync(Cohort cohort, string reference, int seats, EnrollmentStatus status, Guid? ownerId = null)
    {
        await using var db = Enrollments();
        var enrollment = new Enrollment
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            CohortId = cohort.Id,
            CohortCode = cohort.Code,
            Reference = reference,
            Seats = seats,
            FeeCents = EnrollmentRules.FeeCents(seats),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Enrollments.Add(enrollment);
        await db.SaveChangesAsync();
        return enrollment;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Cohort[] cohorts) : ICohortCatalog
{
    public Task<CohortInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(cohorts.Where(s => s.Code == code).Select(s => new CohortInfo(s.Id, s.Code)).FirstOrDefault());
}
