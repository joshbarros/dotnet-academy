using Academy.Enrollments;
using Xunit;

namespace Academy.UnitTests;

public class EnrollmentRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("LRN-1", 0)]
    [InlineData("LRN-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long seats) =>
        Assert.NotNull(EnrollmentRules.Validate(reference, seats));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(EnrollmentRules.Validate("LRN-1", EnrollmentRules.MaxSeatsPerEnrollment));
        Assert.NotNull(EnrollmentRules.Validate("LRN-1", EnrollmentRules.MaxSeatsPerEnrollment + 1));
        Assert.NotNull(EnrollmentRules.Validate(new string('x', EnrollmentRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one Seats
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long seats)
    {
        Assert.NotNull(EnrollmentRules.Validate("LRN-1", seats));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // passes what the naive cast would have done
    }

    [Fact]
    public void HappyFee_IsAcademyPerSeatsPlusDocumentationFee()
    {
        Assert.Null(EnrollmentRules.Validate("LRN-1", 3));
        Assert.Equal(3 * 4_900L + 500L, EnrollmentRules.FeeCents(3));
        Assert.Equal(4_900L + 500L, EnrollmentRules.FeeCents(1));
    }
}
