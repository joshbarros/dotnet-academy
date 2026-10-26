using Academy.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api.Modules.Passes;

public record AccessPassDto(Guid EnrollmentId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/passes")]
public class PassesController(AcademyDbContext db) : ControllerBase
{
    [HttpGet("{enrollmentId:guid}")]
    public async Task<ActionResult<AccessPassDto>> Get(Guid enrollmentId, CancellationToken ct)
    {
        var doc = await db.Passes.AsNoTracking().FirstOrDefaultAsync(d => d.EnrollmentId == enrollmentId, ct);
        return doc is null
            ? NotFound()
            : new AccessPassDto(doc.EnrollmentId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
