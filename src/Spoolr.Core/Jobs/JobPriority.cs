namespace Spoolr.Core.Jobs;

/// <summary>
/// Dispatch priority. Higher values are claimed first; ties break on submission time.
/// </summary>
public enum JobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
}
