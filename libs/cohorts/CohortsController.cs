using Academy.Shared;
using Academy.Shared.Auth;
using Academy.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Academy.Cohorts;

public record CreateCohortRequest(string? Code, string? Title, string? Track, string? Instructor, DateTimeOffset? StartsAt, long SeatCapacity);

public record CohortDto(Guid Id, string Code, string Title, string Track, string Instructor, DateTimeOffset StartsAt, int SeatCapacity, int SeatsLeft)
{
    public static CohortDto From(Cohort s) => new(s.Id, s.Code, s.Title, s.Track, s.Instructor, s.StartsAt, s.SeatCapacity, s.SeatsLeft);
}

/// <summary>What other modules may ask Cohorts, over Cohorts's own tables.</summary>
public sealed class CohortCatalog(CohortsDbContext db) : ICohortCatalog
{
    public async Task<CohortInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Cohorts.AsNoTracking().Where(s => s.Code == code).Select(s => new CohortInfo(s.Id, s.Code)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/cohorts")]
[Authorize]
public class CohortsController(CohortsDbContext db) : ControllerBase
{
    private const int MaxSeatCapacity = 500;

    [HttpGet]
    public async Task<IEnumerable<CohortDto>> List(CancellationToken ct) =>
        (await db.Cohorts.AsNoTracking().OrderBy(s => s.StartsAt).ThenBy(s => s.Code).ToListAsync(ct)).Select(CohortDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<CohortDto>> Get(string code, CancellationToken ct)
    {
        var cohort = await db.Cohorts.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return cohort is null ? NotFound() : CohortDto.From(cohort);
    }

    /// <summary>Opens a cohort. Staff only. This is the only place SeatsLeft is initialised; after that only the Cohorts handler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<CohortDto>> Create(CreateCohortRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Track) || string.IsNullOrWhiteSpace(request.Instructor))
            throw new DomainException(400, "Title, track and instructor are required.");
        if (request.SeatCapacity < 1 || request.SeatCapacity > MaxSeatCapacity)
            throw new DomainException(400, $"Seat capacity must be between 1 and {MaxSeatCapacity} seats.");
        if (await db.Cohorts.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Cohort '{code}' already exists.");

        var cohort = new Cohort
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Title = request.Title.Trim(),
            Track = request.Track.Trim(),
            Instructor = request.Instructor.Trim(),
            StartsAt = request.StartsAt ?? DateTimeOffset.UtcNow.AddDays(14),
            SeatCapacity = (int)request.SeatCapacity,
            SeatsLeft = (int)request.SeatCapacity,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Cohorts.Add(cohort);
        await db.SaveChangesAsync(ct);
        return Created($"/api/cohorts/{cohort.Code}", CohortDto.From(cohort));
    }
}
