using System.Collections.Concurrent;
using Academy.Api.Messaging;
using Academy.Api.Modules.Cohorts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Academy.Api.Tests;

public class CohortsConcurrencyTests
{
    [Fact]
    public async Task LastSeat_TwoConcurrentReservations_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(db, seatCapacity: 1);

        var outcomes = new ConcurrentBag<(Guid Enrollment, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var enrollmentId = Guid.CreateVersion7();
            var handler = new CohortsHandler(TestDatabase.Create());
            var outcome = await handler.ReserveAsync(
                Guid.CreateVersion7(), new ReserveSeats(enrollmentId, cohort.Id, 1, $"LRN-{n}"));
            outcomes.Add((enrollmentId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
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
        var db = await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(db, seatCapacity: 4);
        var enrollmentId = Guid.CreateVersion7();
        var handler = new CohortsHandler(TestDatabase.Create());
        await handler.ReserveAsync(Guid.CreateVersion7(), new ReserveSeats(enrollmentId, cohort.Id, 3, "LRN-1"));

        // Two different commands (poison compensation and a withdrawal) both ask for the same seats back.
        var first = await new CohortsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(enrollmentId, "poison"));
        var second = await new CohortsHandler(TestDatabase.Create())
            .ReleaseAsync(Guid.CreateVersion7(), new ReleaseSeats(enrollmentId, "withdrawal"));

        var remaining = await TestDatabase.Create().Cohorts.Where(s => s.Id == cohort.Id).Select(s => s.SeatsLeft).FirstAsync();
        Assert.True(first);
        Assert.False(second);
        Assert.Equal(4, remaining);
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var cohort = await TestDatabase.AddCohortAsync(db, seatCapacity: 5);
        var messageId = Guid.CreateVersion7();
        var command = new ReserveSeats(Guid.CreateVersion7(), cohort.Id, 2, "LRN-1");

        var first = await new CohortsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new CohortsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        var remaining = await TestDatabase.Create().Cohorts.Where(s => s.Id == cohort.Id).Select(s => s.SeatsLeft).FirstAsync();
        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(3, remaining);
    }
}
