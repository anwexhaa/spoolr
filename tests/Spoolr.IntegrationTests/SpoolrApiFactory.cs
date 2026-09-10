using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Spoolr.Infrastructure.Persistence;
using Spoolr.Infrastructure.Workers;

namespace Spoolr.IntegrationTests;

/// <summary>
/// Boots the real API in memory against a throwaway SQLite database.
/// </summary>
/// <remarks>
/// The host is the one that ships: real routing, real authentication handlers, real
/// endpoint filters, real Entity Framework mappings. Only the database is swapped, and it
/// is swapped for another relational engine rather than an in-memory fake, so a query the
/// provider cannot translate still fails here.
/// </remarks>
public sealed class SpoolrApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Shared with the test so responses can be read with the same conventions.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private SqliteConnection? _connection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development is what enables the local operator stand-in, so the tests do not need
        // an Entra ID tenant to exercise the operator endpoints.
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                // Lets the host create the schema through its own startup path, against the
                // connection swapped in below.
                ["Database:MigrateOnStartup"] = "true",

                // Deterministic backoff: a retry is claimable immediately, so a test does
                // not have to wait out a real jittered delay.
                ["Retry:BaseDelaySeconds"] = "0.001",
                ["Retry:MaxDelaySeconds"] = "0.002",
                ["Retry:MaxAttempts"] = "2",
            }));

        builder.ConfigureServices(services =>
        {
            RemoveBackgroundSweeper(services);
            UseIsolatedSqlite(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _connection?.Dispose();
        }
    }

    /// <summary>
    /// The sweep would otherwise run on its own schedule underneath the assertions and
    /// requeue jobs a test had deliberately left in flight.
    /// </summary>
    private static void RemoveBackgroundSweeper(IServiceCollection services)
    {
        var registration = services.FirstOrDefault(d => d.ImplementationType == typeof(StalledJobSweeper));

        if (registration is not null)
        {
            services.Remove(registration);
        }
    }

    private void UseIsolatedSqlite(IServiceCollection services)
    {
        services.RemoveAll<DbContextOptions<SpoolrDbContext>>();
        services.RemoveAll<DbContextOptions>();

        // Kept open for the lifetime of the factory: an in-memory SQLite database is
        // discarded as soon as its last connection closes.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        services.AddDbContext<SpoolrDbContext>(options => options.UseSqlite(_connection));
    }
}
