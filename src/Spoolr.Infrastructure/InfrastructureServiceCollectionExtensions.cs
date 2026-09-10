using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spoolr.Core.Abstractions;
using Spoolr.Core.Resilience;
using Spoolr.Infrastructure.Configuration;
using Spoolr.Infrastructure.Messaging;
using Spoolr.Infrastructure.Persistence;
using Spoolr.Infrastructure.Security;

namespace Spoolr.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence, security, and messaging.
    /// </summary>
    /// <remarks>
    /// Every external dependency degrades to a local equivalent when it is not configured,
    /// so a new contributor can clone the repository and run the service without an Azure
    /// subscription. Deployed environments supply the real ones through configuration.
    /// </remarks>
    public static IServiceCollection AddSpoolrInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddValidatedOptions<DatabaseOptions>(configuration, DatabaseOptions.SectionName);
        services.AddValidatedOptions<RetryOptions>(configuration, RetryOptions.SectionName);
        services.AddValidatedOptions<DispatchOptions>(configuration, DispatchOptions.SectionName);
        services.AddValidatedOptions<ServiceBusOptions>(configuration, ServiceBusOptions.SectionName);

        AddDatabase(services, configuration);

        services.AddScoped<IPrintJobStore, EfPrintJobStore>();
        services.AddScoped<IPrinterStore, EfPrinterStore>();
        services.AddSingleton<IDeviceKeyHasher, Pbkdf2DeviceKeyHasher>();

        services.AddSingleton(sp =>
        {
            var retry = sp.GetRequiredService<IOptions<RetryOptions>>().Value;

            return new BackoffPolicy(
                TimeSpan.FromSeconds(retry.BaseDelaySeconds),
                TimeSpan.FromSeconds(retry.MaxDelaySeconds),
                retry.Multiplier);
        });

        AddEventPublisher(services, configuration);

        return services;
    }

    private static void AddDatabase(IServiceCollection services, IConfiguration configuration)
    {
        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? new DatabaseOptions();

        services.AddDbContext<SpoolrDbContext>(options =>
        {
            switch (database.Provider)
            {
                case DatabaseProvider.SqlServer:
                    options.UseSqlServer(
                        database.ConnectionString
                            ?? throw new InvalidOperationException(
                                "Database:ConnectionString is required when the provider is SqlServer."),
                        sql => sql.EnableRetryOnFailure());
                    break;

                case DatabaseProvider.Sqlite:
                default:
                    options.UseSqlite(database.ConnectionString ?? "Data Source=spoolr.db");
                    break;
            }
        });
    }

    private static void AddEventPublisher(IServiceCollection services, IConfiguration configuration)
    {
        var serviceBus = configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>()
            ?? new ServiceBusOptions();

        if (!serviceBus.IsConfigured)
        {
            services.AddSingleton<IJobEventPublisher, LoggingJobEventPublisher>();
            return;
        }

        // Managed identity rather than a connection string, so no broker credential is
        // stored in configuration or rotated by hand.
        services.AddSingleton(_ => new ServiceBusClient(
            serviceBus.FullyQualifiedNamespace,
            new DefaultAzureCredential()));

        services.AddSingleton(sp => sp
            .GetRequiredService<ServiceBusClient>()
            .CreateSender(serviceBus.TopicName));

        services.AddSingleton<IJobEventPublisher, ServiceBusJobEventPublisher>();
    }

    /// <summary>
    /// Binds a section and validates it at startup rather than at first use, so a bad value
    /// stops the deployment instead of surfacing as a request failure hours later.
    /// </summary>
    private static void AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
