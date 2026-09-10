namespace Spoolr.Core.Printers;

/// <summary>
/// Reported health of a registered printer.
/// </summary>
public enum PrinterStatus
{
    /// <summary>Registered but has never checked in.</summary>
    Unknown = 0,

    /// <summary>Heartbeating and accepting work.</summary>
    Online = 1,

    /// <summary>Heartbeating but reporting a fault, such as a paper jam or empty tray.</summary>
    Degraded = 2,

    /// <summary>Has not checked in within the heartbeat window.</summary>
    Offline = 3,

    /// <summary>Removed from service. Keeps its job history but accepts no new work.</summary>
    Retired = 4,
}
