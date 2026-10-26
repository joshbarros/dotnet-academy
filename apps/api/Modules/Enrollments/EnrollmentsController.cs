using System.Text.Json;
using Academy.Api.Data;
using Academy.Api.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Academy.Api.Modules.Enrollments;

[ApiController]
[Route("api/enrollments")]
public class EnrollmentsController(AcademyDbContext db, EnrollmentService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the enrollment. The seat hold and the access pass follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<EnrollmentDto>> Create(CreateEnrollmentRequest request, CancellationToken ct)
    {
        var enrollment = await service.CreateAsync(request, ct);
        return Accepted($"/api/enrollments/{enrollment.Id}", EnrollmentDto.From(enrollment));
    }

    [HttpGet]
    public async Task<IEnumerable<EnrollmentDto>> List([FromQuery] string? cohortCode, CancellationToken ct)
    {
        var query = db.Enrollments.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(cohortCode))
            query = query.Where(b => b.CohortCode == cohortCode);
        return (await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct)).Select(EnrollmentDto.From);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EnrollmentDto>> Get(Guid id, CancellationToken ct)
    {
        var enrollment = await db.Enrollments.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return enrollment is null ? NotFound() : EnrollmentDto.From(enrollment);
    }

    [HttpPost("{id:guid}/withdrawal")]
    public async Task<ActionResult<EnrollmentDto>> Withdraw(Guid id, CancellationToken ct) =>
        Accepted($"/api/enrollments/{id}", EnrollmentDto.From(await service.WithdrawAsync(id, ct)));

    /// <summary>The Kafka event log for one enrollment, read from the start of the topic.</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
