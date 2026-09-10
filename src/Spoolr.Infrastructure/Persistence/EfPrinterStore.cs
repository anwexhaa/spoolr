using Microsoft.EntityFrameworkCore;
using Spoolr.Core.Abstractions;
using Spoolr.Core.Printers;

namespace Spoolr.Infrastructure.Persistence;

/// <summary>
/// Entity Framework Core implementation of <see cref="IPrinterStore"/>.
/// </summary>
public sealed class EfPrinterStore(SpoolrDbContext db) : IPrinterStore
{
    public async Task AddAsync(Printer printer, CancellationToken cancellationToken = default) =>
        await db.Printers.AddAsync(printer, cancellationToken);

    public async Task<Printer?> FindAsync(Guid printerId, CancellationToken cancellationToken = default) =>
        await db.Printers.FirstOrDefaultAsync(p => p.Id == printerId, cancellationToken);

    public async Task<IReadOnlyList<Printer>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Printers
            .OrderBy(p => p.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken = default) =>
        await db.Printers.AnyAsync(p => p.Name == name, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await db.SaveChangesAsync(cancellationToken);
}
