using System.Collections.Concurrent;
using Academy.Cohorts;
using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.IntegrationTests;

public class CohortsConcurrencyTests
{
    [Fact]
    public async Task LastSeat_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(seatCapacity: 1);

        var outcomes = new ConcurrentBag<(Guid Enrollment, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var enrollmentId = Guid.CreateVersion7();
            await using var db = TestDatabase.Cohorts();
            var outcome = await new CohortsHandler(db).ReserveAsync(
                Guid.CreateVersion7(), new ReserveSeats(enrollmentId, Guid.NewGuid(), cohort.Id, 1, $"LRN-{n}"));
            outcomes.Add((enrollmentId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Cohorts();
        var remaining = await check.Cohorts.Where(s => s.Id == cohort.Id).Select(s => s.SeatsLeft).FirstAsync();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Enrollment;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(0, remaining);
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.EnrollmentId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task Release_GivesTheSeatsBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(seatCapacity: 4);
        var enrollmentId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Cohorts())
            await new CohortsHandler(db).ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(enrollmentId, Guid.NewGuid(), cohort.Id, 3, "LRN-1"));

        // Two different commands (poison compensation and a withdrawal) both ask for the same seats back.
        await using var first = TestDatabase.Cohorts();
        await using var second = TestDatabase.Cohorts();
        var one = await new CohortsHandler(first).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(enrollmentId, "poison"));
        var two = await new CohortsHandler(second).ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(enrollmentId, "withdrawal"));

        await using var check = TestDatabase.Cohorts();
        Assert.True(one);
        Assert.False(two);
        Assert.Equal(4, await check.Cohorts.Where(s => s.Id == cohort.Id).Select(s => s.SeatsLeft).FirstAsync());
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(seatCapacity: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveSeats(Guid.CreateVersion7(), Guid.NewGuid(), cohort.Id, 2, "LRN-1");

        await using var a = TestDatabase.Cohorts();
        await using var b = TestDatabase.Cohorts();
        var first = await new CohortsHandler(a).ReserveAsync(messageId, command);
        var second = await new CohortsHandler(b).ReserveAsync(messageId, command);

        await using var check = TestDatabase.Cohorts();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, await check.Cohorts.Where(s => s.Id == cohort.Id).Select(s => s.SeatsLeft).FirstAsync());
    }

    [Fact]
    public async Task TheDatabaseItself_RefusesANegativeRemainder()
    {
        await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(seatCapacity: 2);
        await using var db = TestDatabase.Cohorts();

        // A bug that skipped the WHERE clause would hit the check constraint instead of overselling.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Cohorts.Where(s => s.Id == cohort.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SeatsLeft, x => x.SeatsLeft - 3)));
    }
}
