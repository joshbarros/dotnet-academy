using System.Text.Json;
using Academy.Shared.Auth;
using Academy.Shared.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Academy.Enrollments;

[ApiController]
[Route("api/enrollments")]
[Authorize]
public class EnrollmentsController(EnrollmentService service, KafkaLog kafka) : ControllerBase
{
    /// <summary>Accepts the enrollment for the signed-in user. Cohorts and the access pass follow asynchronously; poll GET to see them.</summary>
    [HttpPost]
    public async Task<ActionResult<EnrollmentDto>> Create(CreateEnrollmentRequest request, CancellationToken ct)
    {
        var enrollment = await service.CreateAsync(request, ct);
        return Accepted($"/api/enrollments/{enrollment.Id}", EnrollmentDto.From(enrollment));
    }

    /// <summary>Your enrollments. Staff see everyone's.</summary>
    [HttpGet]
    public async Task<IEnumerable<EnrollmentDto>> List([FromQuery] string? cohortCode, CancellationToken ct) =>
        (await service.ListAsync(cohortCode, ct)).Select(EnrollmentDto.From);

    [HttpGet("{id:guid}")]
    public async Task<EnrollmentDto> Get(Guid id, CancellationToken ct) => EnrollmentDto.From(await service.GetAsync(id, ct));

    [HttpPost("{id:guid}/withdrawal")]
    public async Task<ActionResult<EnrollmentDto>> Withdraw(Guid id, CancellationToken ct) =>
        Accepted($"/api/enrollments/{id}", EnrollmentDto.From(await service.WithdrawAsync(id, ct)));

    /// <summary>The Kafka event log for one enrollment, read from the start of the topic. Staff only.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Policies.ViewAll)]
    public async Task<IEnumerable<JsonElement>> Events(Guid id, CancellationToken ct) =>
        (await Task.Run(() => kafka.Read(id.ToString(), TimeSpan.FromSeconds(10)), ct))
            .Select(v => JsonDocument.Parse(v).RootElement.Clone());
}
