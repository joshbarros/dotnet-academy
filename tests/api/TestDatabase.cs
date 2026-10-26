using Academy.Api.Data;
using Academy.Api.Modules.Enrollments;
using Academy.Api.Modules.Cohorts;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ACADEMY_TEST_DB")
        ?? "Host=localhost;Port=5474;Database=academy_test;Username=academy;Password=academy";

    private static readonly DbContextOptions<AcademyDbContext> Options =
        new DbContextOptionsBuilder<AcademyDbContext>().UseNpgsql(ConnectionString).Options;

    public static AcademyDbContext Create() => new(Options);

    public static async Task<AcademyDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"AccessPasses\", \"SeatHolds\", \"Enrollments\", \"Cohorts\" RESTART IDENTITY CASCADE;");
        return db;
    }

    public static async Task<Cohort> AddCohortAsync(AcademyDbContext db, int seatCapacity)
    {
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

    public static async Task<Enrollment> AddEnrollmentAsync(AcademyDbContext db, Cohort cohort, string reference, int seats, EnrollmentStatus status)
    {
        var enrollment = new Enrollment
        {
            Id = Guid.CreateVersion7(),
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
