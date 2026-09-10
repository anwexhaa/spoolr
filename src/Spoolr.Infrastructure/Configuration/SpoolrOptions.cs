using System.ComponentModel.DataAnnotations;

namespace Spoolr.Infrastructure.Configuration;

/// <summary>Which database provider the service runs against.</summary>
public enum DatabaseProvider
{
    /// <summary>Local file database. The default, so the service runs with no setup.</summary>
    Sqlite = 0,

    /// <summary>Azure SQL, used in deployed environments.</summary>
    SqlServer = 1,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>
    /// Connection string. Left empty, SQLite falls back to a file beside the application.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Applies migrations at startup. Convenient locally, and deliberately off by default
    /// because a deployment should not race several replicas to migrate the same database.
    /// </summary>
    public bool MigrateOnStartup { get; set; }
}

public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    /// <summary>Ceiling of the first retry window.</summary>
    [Range(0.001, 3600)]
    public double BaseDelaySeconds { get; set; } = 1;

    /// <summary>Longest a retry will ever be held back.</summary>
    [Range(0.001, 86400)]
    public double MaxDelaySeconds { get; set; } = 300;

    [Range(1.0001, 100)]
    public double Multiplier { get; set; } = 2;

    /// <summary>Delivery attempts a job gets before it is failed for good.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 3;
}

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    /// <summary>
    /// How long a printer has to acknowledge a job before the sweep assumes it never
    /// arrived and returns the job to the queue.
    /// </summary>
    [Range(1, 3600)]
    public double DispatchTimeoutSeconds { get; set; } = 60;

    /// <summary>How long a recovered job waits before it becomes claimable again.</summary>
    [Range(0, 3600)]
    public double RequeueDelaySeconds { get; set; } = 5;

    /// <summary>Interval between stalled-job sweeps.</summary>
    [Range(1, 3600)]
    public double SweepIntervalSeconds { get; set; } = 30;

    /// <summary>Window a printer must check in within to be considered online.</summary>
    [Range(1, 3600)]
    public double HeartbeatWindowSeconds { get; set; } = 120;
}

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Namespace host, for example contoso.servicebus.windows.net. Left empty, job events
    /// go to the log instead and the service needs no Azure resources to run.
    /// </summary>
    public string? FullyQualifiedNamespace { get; set; }

    public string? TopicName { get; set; }

    /// <summary>True when enough is configured to reach a real broker.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(FullyQualifiedNamespace) && !string.IsNullOrWhiteSpace(TopicName);
}
