using Academy.Shared;
using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Academy.Enrollments;

public static class EnrollmentsModule
{
    public static IServiceCollection AddEnrollmentsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Academy") ?? throw new InvalidOperationException("Connection string 'Academy' is not configured.");
        services.AddDbContext<EnrollmentsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", EnrollmentsDbContext.SchemaName)));
        services.Configure<EnrollmentOptions>(config.GetSection("Enrollments"));
        services.AddScoped<EnrollmentService>();
        services.AddScoped<EnrollmentsHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<EnrollmentsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<EnrollmentsDbContext>>("enrollments-db", tags: ["ready"]);

        messaging.AddOutbox<EnrollmentsDbContext>()
            .AddConsumer<EnrollmentsHandler>(Topology.EnrollmentsQueue,
                [Topology.HeldKey, Topology.ShortKey, Topology.IssuedKey, Topology.RejectedKey, Topology.ReleasedKey], prefetch: 4);
        return services;
    }
}
