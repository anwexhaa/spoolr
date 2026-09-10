namespace Spoolr.Core.Abstractions;

/// <summary>
/// Issues and verifies the keys printers authenticate with.
/// </summary>
/// <remarks>
/// A printer is not a person, so it cannot complete an interactive sign-in. It gets a
/// long random key at registration instead. The service stores only a hash of that key,
/// so a leaked database dump does not let an attacker impersonate the estate.
/// </remarks>
public interface IDeviceKeyHasher
{
    /// <summary>
    /// Generates a fresh device key. Returned to the caller once at registration and
    /// never recoverable afterwards.
    /// </summary>
    string GenerateKey();

    /// <summary>Hashes a device key for storage.</summary>
    string Hash(string deviceKey);

    /// <summary>
    /// Checks a presented key against a stored hash in constant time with respect to the
    /// hash contents, so a caller cannot learn the hash by timing the comparison.
    /// </summary>
    bool Verify(string deviceKey, string storedHash);
}
