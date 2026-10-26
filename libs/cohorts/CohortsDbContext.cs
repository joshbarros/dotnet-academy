using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Academy.Cohorts;

public class CohortsDbContext(DbContextOptions<CohortsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "seats";

    public DbSet<Cohort> Cohorts => Set<Cohort>();
    public DbSet<SeatHold> Holds => Set<SeatHold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Cohort>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Cohorts_Remaining", "\"SeatsLeft\" >= 0 AND \"SeatsLeft\" <= \"SeatCapacity\""));
        });

        b.Entity<SeatHold>(e =>
        {
            e.ToTable("SeatHolds");
            e.HasKey(x => x.EnrollmentId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Cohort>().WithMany().HasForeignKey(x => x.CohortId);
        });
    }
}
