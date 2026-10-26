namespace Academy.Api.Modules.Enrollments;

public static class EnrollmentRules
{
    public const int MaxSeatsPerEnrollment = 5;
    public const int MaxReferenceLength = 40;
    public const string PoisonToken = "POISON";
    public const long FeeCentsPerSeat = 4_900;
    public const long PlatformFeeCents = 500;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long seats)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Learner reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Learner reference must be at most {MaxReferenceLength} characters.";
        if (seats < 1)
            return "An enrollment needs at least 1 seat.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (seats > MaxSeatsPerEnrollment)
            return $"An enrollment takes at most {MaxSeatsPerEnrollment} seats.";
        return null;
    }

    /// <summary>Case-sensitive substring match: a longer token that merely contains the poison matches too.</summary>
    public static bool IsPoison(string reference) => reference.Contains(PoisonToken, StringComparison.Ordinal);

    /// <summary>Tuition per seat plus a flat platform fee, in cents. Overflow throws instead of wrapping.</summary>
    public static long FeeCents(int seats) => checked(seats * FeeCentsPerSeat + PlatformFeeCents);
}
