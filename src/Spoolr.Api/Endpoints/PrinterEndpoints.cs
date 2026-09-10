using Microsoft.Extensions.Options;
using Spoolr.Api.Authentication;
using Spoolr.Api.Contracts;
using Spoolr.Core.Abstractions;
using Spoolr.Core.Printers;
using Spoolr.Infrastructure.Configuration;

namespace Spoolr.Api.Endpoints;

/// <summary>
/// Endpoints for managing the printer estate.
/// </summary>
public static class PrinterEndpoints
{
    public static IEndpointRouteBuilder MapPrinterEndpoints(this IEndpointRouteBuilder app)
    {
        var printers = app.MapGroup("/api/v1/printers")
            .RequireAuthorization(SpoolrPolicies.Operator)
            .WithTags("Printers");

        printers.MapPost("/", RegisterAsync)
            .WithName("RegisterPrinter")
            .WithSummary("Registers a printer and issues its device key.")
            .WithDescription("The device key is returned once and cannot be retrieved later.")
            .ValidatingBody<RegisterPrinterRequest>()
            .Produces<RegisterPrinterResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        printers.MapGet("/", ListAsync)
            .WithName("ListPrinters")
            .WithSummary("Lists registered printers and their current status.")
            .Produces<IReadOnlyList<PrinterResponse>>();

        printers.MapGet("/{printerId:guid}", GetAsync)
            .WithName("GetPrinter")
            .Produces<PrinterResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        printers.MapPost("/{printerId:guid}/retire", RetireAsync)
            .WithName("RetirePrinter")
            .WithSummary("Takes a printer out of service, keeping its job history.")
            .Produces<PrinterResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterPrinterRequest request,
        IPrinterStore printers,
        IDeviceKeyHasher hasher,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();

        if (await printers.NameExistsAsync(name, cancellationToken))
        {
            return TypedResults.Problem(
                $"A printer named {name} is already registered.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Generated here and handed back exactly once. Only the hash is persisted, so a
        // caller who loses the key has to rotate it rather than look it up.
        var deviceKey = hasher.GenerateKey();

        var printer = Printer.Register(
            name,
            request.Location,
            request.Model,
            hasher.Hash(deviceKey),
            clock.GetUtcNow(),
            request.SupportsColor,
            request.SupportsDuplex,
            request.MaxPagesPerJob);

        await printers.AddAsync(printer, cancellationToken);
        await printers.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/api/v1/printers/{printer.Id}",
            new RegisterPrinterResponse(printer.Id, printer.Name, deviceKey));
    }

    private static async Task<IResult> ListAsync(
        IPrinterStore printers,
        IOptions<DispatchOptions> dispatchOptions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var all = await printers.ListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var window = HeartbeatWindow(dispatchOptions);

        return TypedResults.Ok(all.Select(p => PrinterResponse.From(p, now, window)).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid printerId,
        IPrinterStore printers,
        IOptions<DispatchOptions> dispatchOptions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var printer = await printers.FindAsync(printerId, cancellationToken);

        return printer is null
            ? TypedResults.Problem($"No printer with id {printerId}.", statusCode: StatusCodes.Status404NotFound)
            : TypedResults.Ok(PrinterResponse.From(printer, clock.GetUtcNow(), HeartbeatWindow(dispatchOptions)));
    }

    private static async Task<IResult> RetireAsync(
        Guid printerId,
        IPrinterStore printers,
        IOptions<DispatchOptions> dispatchOptions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var printer = await printers.FindAsync(printerId, cancellationToken);

        if (printer is null)
        {
            return TypedResults.Problem($"No printer with id {printerId}.", statusCode: StatusCodes.Status404NotFound);
        }

        printer.Retire();
        await printers.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(PrinterResponse.From(printer, clock.GetUtcNow(), HeartbeatWindow(dispatchOptions)));
    }

    private static TimeSpan HeartbeatWindow(IOptions<DispatchOptions> options) =>
        TimeSpan.FromSeconds(options.Value.HeartbeatWindowSeconds);
}
