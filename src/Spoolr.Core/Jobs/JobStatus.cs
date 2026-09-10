namespace Spoolr.Core.Jobs;

/// <summary>
/// Lifecycle states for a print job. Transitions are enforced by <see cref="PrintJob"/>.
/// </summary>
public enum JobStatus
{
    /// <summary>Accepted and waiting for a dispatcher to claim it.</summary>
    Queued = 0,

    /// <summary>Claimed by the dispatcher and handed to a printer, awaiting acknowledgement.</summary>
    Dispatched = 1,

    /// <summary>The printer acknowledged the job and is rendering it.</summary>
    Printing = 2,

    /// <summary>Terminal. The printer reported success.</summary>
    Completed = 3,

    /// <summary>Terminal. Every delivery attempt failed.</summary>
    Failed = 4,

    /// <summary>Terminal. Cancelled by the submitter before it finished.</summary>
    Cancelled = 5,
}
