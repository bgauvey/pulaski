using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Application.Materials;

namespace Pulaski.Application;

public static class DependencyInjection
{
    /// <summary>Registers every command and query handler in this assembly, plus id and event services and the material and people source adapters (D-053, D-054).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // To whole microseconds, so times worked out in memory equal the same times read back from PostgreSQL.
        services.TryAddSingleton<TimeProvider>(MicrosecondClock.Instance);
        services.TryAddSingleton<Live.LiveHub>();
        services.TryAddSingleton<Health.ServiceHealth>();
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        services.AddScoped<IEventFactory, EventFactory>();
        services.AddSingleton<IMaterialSourceAdapter, CsvMaterialSource>();
        services.AddSingleton<IMaterialSourceAdapter, B2mmlMaterialSource>();
        services.AddSingleton<IPeopleSourceAdapter, CsvPeopleSource>();
        services.AddSingleton<IPeopleSourceAdapter, ScimPeopleSource>();
        services.AddScoped<MachineData.OeeRollups>();
        // What release approval can release (#138): one per kind of document.
        services.AddScoped<Approvals.IReleasableDocument, Processes.ProcessReleasable>();
        services.AddScoped<Approvals.IReleasableDocument, Boms.BomReleasable>();
        services.AddScoped<Approvals.IReleasableDocument, Quality.SpecReleasable>();
        services.AddScoped<MachineData.OeeTargetWrites>();
        services.AddScoped<Inventory.FeedCounterPoster>();
        services.AddScoped<Inventory.AllocationAutoRefresh>();
        services.AddScoped<Scheduling.RateRefreshRunner>();
        services.AddScoped<Maintenance.MaintenancePlanRunner>();
        services.AddScoped<Quality.TagSampler>();
        services.AddScoped<Materials.ScheduledConnections>();
        services.AddScoped<Labor.QualificationExpiryNotices>();
        services.AddScoped<Alerts.AlertSender>();
        services.AddScoped<Localization.ShippedTranslations>();
        services.TryAddScoped<Execution.ILineLabels, Execution.NoLineLabels>();
        services.TryAddSingleton<Alerts.IAlertSettings, Alerts.NoAlertLinks>();

        // Password reset by e-mail is off unless the host provides settings and a way to send (D-093).
        services.TryAddSingleton<Security.IPasswordResetSettings, Security.PasswordResetOff>();
        services.TryAddSingleton<Security.IMailOutbox, Security.NoMail>();

        var handlerInterfaces = new[] { typeof(ICommandHandler<,>), typeof(IQueryHandler<,>) };
        var handlers = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false });

        foreach (var type in handlers)
        {
            foreach (var contract in type.GetInterfaces())
            {
                if (contract.IsGenericType && handlerInterfaces.Contains(contract.GetGenericTypeDefinition()))
                {
                    services.AddScoped(contract, type);
                }
            }
        }

        return services;
    }
}
