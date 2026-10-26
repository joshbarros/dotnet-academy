namespace Academy.Shared.Contracts;

public record CohortInfo(Guid Id, string Code);

/// <summary>
/// What Enrollments may ask Cohorts, without referencing it. Cohorts implements it over its own tables;
/// Enrollments never reads another module's schema.
/// </summary>
public interface ICohortCatalog
{
    Task<CohortInfo?> FindAsync(string code, CancellationToken ct);
}
