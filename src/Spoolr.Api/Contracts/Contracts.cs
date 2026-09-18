using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spoolr.Core.Jobs;
using Spoolr.Core.Printers;

namespace Spoolr.Api.Contracts;

/// <summary>A page of results, with enough context for the caller to ask for the next one.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

public sealed record RegisterPrinterRequest
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string Name { get; init; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public required string Location { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string Model { get; init; }

    public bool SupportsColor { get; init; }

    public bool SupportsDuplex { get; init; }

    [Range(1, 10_000)]
    public int MaxPagesPerJob { get; init; } = 500;
}

/// <summary>
/// Returned once, at registration. The device key is not recoverable afterwards: the
/// service keeps only a hash of it.
/// </summary>
public sealed record RegisterPrinterResponse(Guid Id, string Name, string DeviceKey);

public sealed record PrinterResponse(
    Guid Id,
    string Name,
    string Location,
    string Model,
    PrinterStatus Status,
    DateTimeOffset? LastHeartbeatAt,
    bool SupportsColor,
    bool SupportsDuplex,
    int MaxPagesPerJob)
{
    public static PrinterResponse From(Printer printer, DateTimeOffset now, TimeSpan heartbeatWindow) =>
        new(
            printer.Id,
            printer.Name,
            printer.Location,
            printer.Model,

            // The derived status, not the last one reported. A device that stopped checking
            // in reads as Offline here even though its stored status still says Online.
            printer.StatusAt(now, heartbeatWindow),
            printer.LastHeartbeatAt,
            printer.SupportsColor,
            printer.SupportsDuplex,
            printer.MaxPagesPerJob);
}

public sealed record SubmitJobRequest
{
    [Required]
    public required Guid PrinterId { get; init; }

    [Required]
    [StringLength(260, MinimumLength = 1)]
    public required string DocumentName { get; init; }

    [Range(1, 10_000)]
    public required int PageCount { get; init; }

    public JobPriority Priority { get; init; } = JobPriority.Normal;

    /// <summary>
    /// A stable hash of what this request asks for, compared when an idempotency key is
    /// reused. Built from the normalised fields rather than the raw body, so a retry that
    /// differs only in whitespace or property order still counts as the same request.
    /// </summary>
    public string Fingerprint()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            PrinterId,
            DocumentName = DocumentName.Trim(),
            PageCount,
            Priority,
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record JobAttemptResponse(
    int AttemptNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? FinishedAt,
    bool Succeeded,
    string? Error);

public sealed record JobResponse(
    Guid Id,
    Guid PrinterId,
    string DocumentName,
    int PageCount,
    JobPriority Priority,
    JobStatus Status,
    int AttemptCount,
    int MaxAttempts,
    string SubmittedBy,
    DateTimeOffset SubmittedAt,
    DateTimeOffset AvailableAt,
    DateTimeOffset? CompletedAt,
    string? LastError,
    IReadOnlyList<JobAttemptResponse> Attempts)
{
    public static JobResponse From(PrintJob job) =>
        new(
            job.Id,
            job.PrinterId,
            job.DocumentName,
            job.PageCount,
            job.Priority,
            job.Status,
            job.AttemptCount,
            job.MaxAttempts,
            job.SubmittedBy,
            job.SubmittedAt,
            job.AvailableAt,
            job.CompletedAt,
            job.LastError,
            [.. job.Attempts.Select(a => new JobAttemptResponse(
                a.AttemptNumber,
                a.StartedAt,
                a.AcknowledgedAt,
                a.FinishedAt,
                a.Succeeded,
                a.Error))]);
}

/// <summary>What a device sends when it checks in.</summary>
public sealed record HeartbeatRequest
{
    /// <summary>Online, Degraded, or Offline. A device cannot claim Retired or Unknown.</summary>
    [Required]
    public required PrinterStatus Status { get; init; }
}

/// <summary>What a device sends when it finishes, or fails, a job.</summary>
public sealed record JobResultRequest
{
    public required bool Succeeded { get; init; }

    [StringLength(1024)]
    public string? Error { get; init; }
}

public sealed record CancelJobRequest
{
    [StringLength(1024)]
    public string? Reason { get; init; }
}
