using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Spoolr.Infrastructure.Persistence;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as milliseconds since the Unix epoch.
/// </summary>
/// <remarks>
/// SQLite has no native offset-aware timestamp, and Entity Framework Core will not
/// translate an inequality over one. That matters because AvailableAt drives the
/// dispatcher's range scan, so a query it cannot translate is not a cosmetic problem.
///
/// An integer compares and sorts identically on SQLite and Azure SQL, and indexes as a
/// plain integer key on both, so local development and production exercise the same query
/// plan shape. The domain still speaks DateTimeOffset.
///
/// The offset itself is not preserved across a round trip, only the instant. Every
/// timestamp in this service is an instant, so nothing depends on the original offset.
/// </remarks>
public sealed class UtcMillisecondsConverter()
    : ValueConverter<DateTimeOffset, long>(
        value => value.ToUnixTimeMilliseconds(),
        stored => DateTimeOffset.FromUnixTimeMilliseconds(stored));
