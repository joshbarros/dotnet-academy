using Academy.Api.Data;
using Academy.Api.Messaging;
using Academy.Api.Middleware;
using Academy.Api.Modules.Enrollments;
using Academy.Api.Modules.Cohorts;
using Academy.Api.Modules.Passes;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNameCaseInsensitive = true);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("Academy")
    ?? throw new InvalidOperationException("Connection string 'Academy' is not configured.");
builder.Services.AddDbContext<AcademyDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<RabbitOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.Configure<EnrollmentOptions>(builder.Configuration.GetSection("Enrollments"));
builder.Services.AddSingleton<RabbitConnection>();
builder.Services.AddSingleton<KafkaLog>();

builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<CohortsHandler>();
builder.Services.AddScoped<PassesHandler>();
builder.Services.AddScoped<EnrollmentsHandler>();

builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService(sp => new ModuleConsumer<CohortsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.CohortsQueue, [Topology.ReserveKey, Topology.ReleaseKey], Prefetch: 1),
    sp.GetRequiredService<ILogger<ModuleConsumer<CohortsHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<PassesHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.PassesQueue, [Topology.HeldKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<PassesHandler>>>()));
builder.Services.AddHostedService(sp => new ModuleConsumer<EnrollmentsHandler>(
    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<RabbitConnection>(),
    new ConsumerSpec(Topology.EnrollmentsQueue,
        [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], Prefetch: 4),
    sp.GetRequiredService<ILogger<ModuleConsumer<EnrollmentsHandler>>>()));

var app = builder.Build();

app.UseMiddleware<ExceptionHandler>();
app.MapControllers();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapGet("/api/health", () => Results.Text("Healthy"));

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AcademyDbContext>().Database.MigrateAsync();
}

app.Run();
