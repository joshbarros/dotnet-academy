using Academy.Enrollments;
using Academy.Passes;
using Academy.Shared;
using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Academy.IntegrationTests;

public class SagaTests
{
    private static EnrollmentsHandler Handler(EnrollmentsDbContext db) => new(db, Options.Create(new EnrollmentOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(5);
        var enrollment = await TestDatabase.AddEnrollmentAsync(cohort, "LRN-POISON-1", 2, EnrollmentStatus.Held);
        var held = new SeatsHeld(enrollment.Id, enrollment.OwnerId, cohort.Id, 2, enrollment.Reference);

        await using var docs = TestDatabase.Passes();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new PassesHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Passes();
        var doc = await check.AccessPasses.SingleAsync(d => d.EnrollmentId == enrollment.Id);
        Assert.Equal(PassStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(enrollment.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Withdrawal_ThenFinalMark_LeavesEnrollmentWithdrawnAndPassIssued()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(5);
        var owner = Guid.CreateVersion7();
        var enrollment = await TestDatabase.AddEnrollmentAsync(cohort, "LRN-1", 2, EnrollmentStatus.Held, owner);

        // The access pass is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Passes())
            await new PassesHandler(docs).IssueAsync(Guid.CreateVersion7(), new SeatsHeld(enrollment.Id, owner, cohort.Id, 2, enrollment.Reference));

        // The owner reverses first.
        await using (var enrollments = TestDatabase.Enrollments())
            await new EnrollmentService(enrollments, new FixedCatalog(cohort), new TestUser(owner)).WithdrawAsync(enrollment.Id, default);

        await using var late = TestDatabase.Enrollments();
        var mark = await Handler(late).MarkEnrolledAsync(Guid.CreateVersion7(), new PassIssued(enrollment.Id, "AP-TEST"));

        await using var check = TestDatabase.Enrollments();
        await using var checkDocs = TestDatabase.Passes();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(EnrollmentStatus.Withdrawn, (await check.Enrollments.SingleAsync()).Status);
        Assert.Equal(PassStatus.Issued, (await checkDocs.AccessPasses.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(5);
        var held = await TestDatabase.AddEnrollmentAsync(cohort, "LRN-A", 1, EnrollmentStatus.Held);
        var rejected = await TestDatabase.AddEnrollmentAsync(cohort, "LRN-B", 1, EnrollmentStatus.Rejected);
        var requested = await TestDatabase.AddEnrollmentAsync(cohort, "LRN-C", 1, EnrollmentStatus.Requested);

        await using (var a = TestDatabase.Enrollments())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkEnrolledAsync(Guid.CreateVersion7(), new PassIssued(held.Id, "AP-1")));
        await using (var b = TestDatabase.Enrollments())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkEnrolledAsync(Guid.CreateVersion7(), new PassIssued(rejected.Id, "AP-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Enrollments())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkEnrolledAsync(Guid.CreateVersion7(), new PassIssued(requested.Id, "AP-3")));

        await using var check = TestDatabase.Enrollments();
        var statuses = (await check.Enrollments.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(EnrollmentStatus.Enrolled, statuses["LRN-A"]);
        Assert.Equal(EnrollmentStatus.Rejected, statuses["LRN-B"]);
        Assert.Equal(EnrollmentStatus.Requested, statuses["LRN-C"]);
    }

    [Fact]
    public async Task Withdrawal_OfARejectedEnrollment_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(5);
        var owner = Guid.CreateVersion7();
        var enrollment = await TestDatabase.AddEnrollmentAsync(cohort, "LRN-1", 1, EnrollmentStatus.Rejected, owner);

        await using var db = TestDatabase.Enrollments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new EnrollmentService(db, new FixedCatalog(cohort), new TestUser(owner)).WithdrawAsync(enrollment.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Withdrawal_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(5);
        var enrollment = await TestDatabase.AddEnrollmentAsync(cohort, "LRN-1", 1, EnrollmentStatus.Held);

        await using var db = TestDatabase.Enrollments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new EnrollmentService(db, new FixedCatalog(cohort), new TestUser(Guid.NewGuid())).WithdrawAsync(enrollment.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Enrollments();
        Assert.Equal(EnrollmentStatus.Held, (await check.Enrollments.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanWithdrawAnyonesEnrollment()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(5);
        var enrollment = await TestDatabase.AddEnrollmentAsync(cohort, "LRN-1", 1, EnrollmentStatus.Held);

        await using var db = TestDatabase.Enrollments();
        var withdrawn = await new EnrollmentService(db, new FixedCatalog(cohort), new TestUser(Guid.NewGuid(), staff: true)).WithdrawAsync(enrollment.Id, default);
        Assert.Equal(EnrollmentStatus.Withdrawn, withdrawn.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Enrollments();
        var enrollment = await new EnrollmentService(db, new FixedCatalog(cohort), new TestUser(owner))
            .CreateAsync(new CreateEnrollmentRequest(cohort.Code, "LRN-1", 2), default);

        await using var check = TestDatabase.Enrollments();
        Assert.Equal(owner, (await check.Enrollments.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveSeats>(message.Payload).OwnerId);
        Assert.Equal(EnrollmentStatus.Requested, enrollment.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(5);
        await using var db = TestDatabase.Enrollments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new EnrollmentService(db, new FixedCatalog(cohort), new TestUser(null)).CreateAsync(new CreateEnrollmentRequest(cohort.Code, "LRN-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownCohort_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Enrollments();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new EnrollmentService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(new CreateEnrollmentRequest("NOPE", "LRN-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
