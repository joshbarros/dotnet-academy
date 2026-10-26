using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Academy.Enrollments;

public class EnrollmentsDbContext(DbContextOptions<EnrollmentsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "enrollments";

    public DbSet<Enrollment> Enrollments => Set<Enrollment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Enrollment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(EnrollmentRules.MaxReferenceLength);
            e.HasIndex(x => new { x.CohortId, x.Reference }).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
    }
}
