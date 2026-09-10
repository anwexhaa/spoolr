namespace Spoolr.Core.Printers;

/// <summary>
/// A physical printer registered with the service.
/// </summary>
/// <remarks>
/// Liveness is derived, not stored. A device is treated as online only while its last
/// heartbeat falls inside the heartbeat window, so a device that loses power is marked
/// offline by the passage of time rather than by anyone remembering to update it.
/// </remarks>
public sealed class Printer
{
    /// <summary>Default window a device must check in within to count as online.</summary>
    public static readonly TimeSpan DefaultHeartbeatWindow = TimeSpan.FromMinutes(2);

    /// <summary>Required by EF Core materialisation. Not for application use.</summary>
    private Printer()
    {
        Name = string.Empty;
        Location = string.Empty;
        Model = string.Empty;
        DeviceKeyHash = string.Empty;
    }

    public Guid Id { get; private init; }

    public string Name { get; private set; }

    public string Location { get; private set; }

    public string Model { get; private set; }

    /// <summary>
    /// PBKDF2 hash of the key the device presents when it polls for work. The raw key is
    /// returned once at registration and never stored.
    /// </summary>
    public string DeviceKeyHash { get; private set; }

    public bool SupportsColor { get; private init; }

    public bool SupportsDuplex { get; private init; }

    /// <summary>Largest job the device will accept, used to reject oversized work up front.</summary>
    public int MaxPagesPerJob { get; private init; }

    /// <summary>Status the device reported at its last check-in.</summary>
    public PrinterStatus ReportedStatus { get; private set; }

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public DateTimeOffset RegisteredAt { get; private init; }

    public int Version { get; private set; }

    /// <summary>
    /// Registers a new device.
    /// </summary>
    public static Printer Register(
        string name,
        string location,
        string model,
        string deviceKeyHash,
        DateTimeOffset now,
        bool supportsColor = false,
        bool supportsDuplex = false,
        int maxPagesPerJob = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKeyHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPagesPerJob);

        return new Printer
        {
            Id = Guid.CreateVersion7(now),
            Name = name.Trim(),
            Location = location.Trim(),
            Model = model.Trim(),
            DeviceKeyHash = deviceKeyHash,
            SupportsColor = supportsColor,
            SupportsDuplex = supportsDuplex,
            MaxPagesPerJob = maxPagesPerJob,
            ReportedStatus = PrinterStatus.Unknown,
            RegisteredAt = now,
        };
    }

    /// <summary>
    /// Records a device check-in and the status it reported.
    /// </summary>
    public void Heartbeat(PrinterStatus reportedStatus, DateTimeOffset now)
    {
        if (ReportedStatus == PrinterStatus.Retired)
        {
            throw new InvalidOperationException($"Printer {Id} is retired and cannot check in.");
        }

        if (reportedStatus is PrinterStatus.Retired or PrinterStatus.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportedStatus),
                reportedStatus,
                "A device may only report Online, Degraded, or Offline.");
        }

        ReportedStatus = reportedStatus;
        LastHeartbeatAt = now;
        Version++;
    }

    /// <summary>
    /// Takes the device out of service. It keeps its history but accepts no new jobs.
    /// </summary>
    public void Retire()
    {
        ReportedStatus = PrinterStatus.Retired;
        Version++;
    }

    /// <summary>
    /// Rotates the key the device authenticates with.
    /// </summary>
    public void RotateDeviceKey(string newKeyHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newKeyHash);

        DeviceKeyHash = newKeyHash;
        Version++;
    }

    /// <summary>
    /// Effective status at <paramref name="now"/>, downgrading a stale device to offline
    /// however healthy it claimed to be at its last check-in.
    /// </summary>
    public PrinterStatus StatusAt(DateTimeOffset now, TimeSpan? heartbeatWindow = null)
    {
        if (ReportedStatus is PrinterStatus.Retired or PrinterStatus.Unknown)
        {
            return ReportedStatus;
        }

        var window = heartbeatWindow ?? DefaultHeartbeatWindow;

        return LastHeartbeatAt is { } last && now - last <= window
            ? ReportedStatus
            : PrinterStatus.Offline;
    }

    /// <summary>
    /// True when the device is reachable and healthy enough to be given a job.
    /// </summary>
    public bool CanAcceptWorkAt(DateTimeOffset now, TimeSpan? heartbeatWindow = null) =>
        StatusAt(now, heartbeatWindow) == PrinterStatus.Online;
}
