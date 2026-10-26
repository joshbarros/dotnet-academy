using Academy.Shared;
using Academy.Shared.Contracts;
using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Academy.Cohorts;

public static class CohortsModule
{
    public static IServiceCollection AddCohortsModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Academy") ?? throw new InvalidOperationException("Connection string 'Academy' is not configured.");
        services.AddDbContext<CohortsDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", CohortsDbContext.SchemaName)));
        services.AddScoped<CohortsHandler>();
        services.AddScoped<ICohortCatalog, CohortCatalog>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<CohortsDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<CohortsDbContext>>("seats-db", tags: ["ready"]);

        messaging.AddOutbox<CohortsDbContext>()
            .AddConsumer<CohortsHandler>(Topology.CohortsQueue, [Topology.ReserveKey, Topology.ReleaseKey], prefetch: 1);
        return services;
    }
}
