using Academy.Api.Messaging;
using Academy.Api.Modules.Enrollments;
using Academy.Api.Modules.Cohorts;
using Academy.Api.Modules.Passes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Academy.Api.Tests;

public class SagaTests
{
    private static EnrollmentsHandler Enrollments() =>
        new(TestDatabase.Create(), Options.Create(new EnrollmentOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(db, 5);
        var enrollment = await TestDatabase.AddEnrollmentAsync(db, cohort, "LRN-POISON-1", 2, EnrollmentStatus.Held);
        var held = new SeatsHeld(enrollment.Id, cohort.Id, 2, enrollment.Reference);

        var handler = new PassesHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Passes.SingleAsync(d => d.EnrollmentId == enrollment.Id);
        Assert.Equal(PassStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Withdrawal_ThenFinalMark_LeavesEnrollmentWithdrawnAndPassIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(db, 5);
        var enrollment = await TestDatabase.AddEnrollmentAsync(db, cohort, "LRN-1", 2, EnrollmentStatus.Held);

        // The access pass is issued; the final mark has not landed yet.
        var number = "AP-TEST";
        await new PassesHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new SeatsHeld(enrollment.Id, cohort.Id, 2, enrollment.Reference));

        // The withdrawal gets there first.
        await new EnrollmentService(TestDatabase.Create()).WithdrawAsync(enrollment.Id, default);

        var mark = await Enrollments().MarkEnrolledAsync(Guid.CreateVersion7(), new PassIssued(enrollment.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(EnrollmentStatus.Withdrawn, (await check.Enrollments.SingleAsync()).Status);
        Assert.Equal(PassStatus.Issued, (await check.Passes.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(db, 5);
        var held = await TestDatabase.AddEnrollmentAsync(db, cohort, "LRN-A", 1, EnrollmentStatus.Held);
        var rejected = await TestDatabase.AddEnrollmentAsync(db, cohort, "LRN-B", 1, EnrollmentStatus.Rejected);
        var requested = await TestDatabase.AddEnrollmentAsync(db, cohort, "LRN-C", 1, EnrollmentStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Enrollments().MarkEnrolledAsync(Guid.CreateVersion7(), new PassIssued(held.Id, "AP-1")));
        Assert.Equal(MarkOutcome.Skipped, await Enrollments().MarkEnrolledAsync(Guid.CreateVersion7(), new PassIssued(rejected.Id, "AP-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Enrollments().MarkEnrolledAsync(Guid.CreateVersion7(), new PassIssued(requested.Id, "AP-3")));

        var statuses = (await TestDatabase.Create().Enrollments.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(EnrollmentStatus.Enrolled, statuses["LRN-A"]);
        Assert.Equal(EnrollmentStatus.Rejected, statuses["LRN-B"]);
        Assert.Equal(EnrollmentStatus.Requested, statuses["LRN-C"]);
    }

    [Fact]
    public async Task Withdrawal_OfARejectedEnrollment_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(db, 5);
        var enrollment = await TestDatabase.AddEnrollmentAsync(db, cohort, "LRN-1", 1, EnrollmentStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new EnrollmentService(TestDatabase.Create()).WithdrawAsync(enrollment.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
