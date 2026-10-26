using Academy.Api.Messaging;
using Academy.Api.Modules.Enrollments;
using Academy.Api.Modules.Cohorts;
using Academy.Api.Modules.Passes;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api.Data;

public class AcademyDbContext(DbContextOptions<AcademyDbContext> options) : DbContext(options)
{
    public DbSet<Cohort> Cohorts => Set<Cohort>();
    public DbSet<SeatHold> Holds => Set<SeatHold>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<AccessPass> Passes => Set<AccessPass>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
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

        b.Entity<Enrollment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(EnrollmentRules.MaxReferenceLength);
            e.HasIndex(x => new { x.CohortId, x.Reference }).IsUnique();
            e.HasOne<Cohort>().WithMany().HasForeignKey(x => x.CohortId);
        });

        b.Entity<AccessPass>(e =>
        {
            e.ToTable("AccessPasses");
            e.HasKey(x => x.EnrollmentId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });
    }
}
