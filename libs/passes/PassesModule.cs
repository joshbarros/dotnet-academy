using Academy.Shared;
using Academy.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Academy.Passes;

public static class PassesModule
{
    public static IServiceCollection AddPassesModule(this IServiceCollection services, IConfiguration config, MessagingBuilder messaging)
    {
        var connection = config.GetConnectionString("Academy") ?? throw new InvalidOperationException("Connection string 'Academy' is not configured.");
        services.AddDbContext<PassesDbContext>(o => o.UseNpgsql(connection,
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", PassesDbContext.SchemaName)));
        services.AddScoped<PassesHandler>();
        services.AddSingleton<IModuleMigrator>(new EfMigrator<PassesDbContext>(order: 10));
        services.AddHealthChecks().AddCheck<DbReadinessCheck<PassesDbContext>>("passes-db", tags: ["ready"]);

        messaging.AddOutbox<PassesDbContext>()
            .AddConsumer<PassesHandler>(Topology.PassesQueue, [Topology.HeldKey], prefetch: 4);
        return services;
    }
}
