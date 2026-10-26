using Academy.Enrollments;
using Academy.Cohorts;
using Xunit;

namespace Academy.UnitTests;

public class DtoTests
{
    [Fact]
    public void EnrollmentDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var enrollment = new Enrollment
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            CohortId = Guid.NewGuid(),
            CohortCode = "S-1",
            Reference = "LRN-1",
            Seats = 3,
            FeeCents = 277_500,
            Status = EnrollmentStatus.Enrolled,
            PassNumber = "AP-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = EnrollmentDto.From(enrollment);

        Assert.Equal(enrollment.Id, dto.Id);
        Assert.Equal(enrollment.OwnerId, dto.OwnerId);
        Assert.Equal("Enrolled", dto.Status);
        Assert.Equal(277_500, dto.FeeCents);
        Assert.Equal("AP-1", dto.PassNumber);
    }

    [Fact]
    public void CohortDto_MapsTheScarceNumber()
    {
        var cohort = new Cohort { Id = Guid.NewGuid(), Code = "S-1", Title = "v", Track = "o", Instructor = "d", SeatCapacity = 10, SeatsLeft = 4 };
        var dto = CohortDto.From(cohort);
        Assert.Equal(10, dto.SeatCapacity);
        Assert.Equal(4, dto.SeatsLeft);
    }
}
