using Spoolr.Core.Printers;

namespace Spoolr.Core.Abstractions;

/// <summary>
/// Persistence for registered printers.
/// </summary>
public interface IPrinterStore
{
    Task AddAsync(Printer printer, CancellationToken cancellationToken = default);

    Task<Printer?> FindAsync(Guid printerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Printer>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>True when a printer with this name is already registered.</summary>
    Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
