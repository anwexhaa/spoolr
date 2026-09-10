using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Spoolr.Api.Authentication;
using Spoolr.Api.Endpoints;
using Spoolr.Infrastructure;
using Spoolr.Infrastructure.Configuration;
using Spoolr.Infrastructure.Observability;
using Spoolr.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpoolrInfrastructure(builder.Configuration);
builder.Services.AddSpoolrAuthentication(builder.Configuration, builder.Environment);

// Turns unhandled exceptions and bare status codes into RFC 9457 problem details, so every
// error the API returns has the same shape whether it came from a handler or the pipeline.
builder.Services.AddProblemDetails();

builder.Services.AddOpenApi();

// Enums cross the wire as names, not ordinals. A client reading "Queued" does not break
// when a new status is inserted into the middle of the enum later.
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHealthChecks()
    // Liveness only answers "is this process still running", so it must not touch the
    // database. A database outage should not make the orchestrator restart every replica.
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<SpoolrDbContext>("database", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "spoolr",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithMetrics(metrics => metrics
        .AddMeter(SpoolrMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());

if (builder.Environment.IsDevelopment())
{
    builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddConsoleExporter());
    builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddConsoleExporter());
}

var app = builder.Build();

await ApplyMigrationsIfConfiguredAsync(app);

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new()
{
    Predicate = check => check.Tags.Contains("live"),
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new()
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

app.MapPrinterEndpoints();
app.MapJobEndpoints();
app.MapDeviceEndpoints();

await app.RunAsync();

/// <summary>
/// Creates the schema at startup when configuration asks for it.
/// </summary>
/// <remarks>
/// Off by default. Several replicas starting at once would otherwise race to migrate the
/// same database, so a deployed environment should migrate as a separate step.
/// </remarks>
static async Task ApplyMigrationsIfConfiguredAsync(WebApplication app)
{
    var database = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;

    if (!database.MigrateOnStartup)
    {
        return;
    }

    await using var scope = app.Services.CreateAsyncScope();

    var context = scope.ServiceProvider.GetRequiredService<SpoolrDbContext>();

    await context.Database.EnsureCreatedAsync();

    app.Logger.LogInformation("Database schema ensured at startup.");
}

/// <summary>
/// Named so the integration test project can drive this host through WebApplicationFactory.
/// </summary>
public partial class Program;
