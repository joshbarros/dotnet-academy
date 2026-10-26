using Academy.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Academy.Passes;

public record AccessPassDto(Guid EnrollmentId, string? Number, string Status, string? Reason, DateTimeOffset CreatedAt);

[ApiController]
[Route("api/passes")]
[Authorize]
public class PassesController(PassesDbContext db, ICurrentUser user) : ControllerBase
{
    /// <summary>The owner or staff only; anyone else gets a 404.</summary>
    [HttpGet("{enrollmentId:guid}")]
    public async Task<ActionResult<AccessPassDto>> Get(Guid enrollmentId, CancellationToken ct)
    {
        var doc = await db.AccessPasses.AsNoTracking().FirstOrDefaultAsync(d => d.EnrollmentId == enrollmentId, ct);
        return doc is null || !OwnerAccess.CanAccess(user, doc.OwnerId)
            ? NotFound()
            : new AccessPassDto(doc.EnrollmentId, doc.Number, doc.Status.ToString(), doc.Reason, doc.CreatedAt);
    }
}
