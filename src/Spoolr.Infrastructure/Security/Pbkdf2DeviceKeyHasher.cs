using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Spoolr.Core.Abstractions;

namespace Spoolr.Infrastructure.Security;

/// <summary>
/// Issues device keys and stores them as PBKDF2-HMAC-SHA256 hashes.
/// </summary>
/// <remarks>
/// The key is 256 bits from a cryptographic random source, so guessing it is not the
/// threat being defended against. The hash is here so that a leaked database backup does
/// not hand an attacker a working credential for every printer in the estate. Iterations
/// are kept moderate for that reason: the cost is paid on every device poll, and stretching
/// a full-entropy secret buys far less than stretching a human-chosen password would.
///
/// The parameters are written into the stored string, so raising the iteration count later
/// does not invalidate keys already issued.
/// </remarks>
public sealed class Pbkdf2DeviceKeyHasher : IDeviceKeyHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int KeyBytes = 32;
    private const int Iterations = 100_000;

    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string GenerateKey() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyBytes));

    public string Hash(string deviceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(deviceKey, salt, Iterations);

        return string.Join(
            '$',
            Prefix,
            Iterations.ToString(),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string deviceKey, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(deviceKey) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        if (!TryParse(storedHash, out var iterations, out var salt, out var expected))
        {
            // A malformed or unrecognised hash is a failed verification, never an
            // exception that a caller might mistake for a transport error.
            return false;
        }

        var actual = Derive(deviceKey, salt, iterations);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string deviceKey, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(deviceKey),
            salt,
            iterations,
            Algorithm,
            HashBytes);

    private static bool TryParse(
        string storedHash,
        out int iterations,
        out byte[] salt,
        out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];

        var parts = storedHash.Split('$');

        if (parts.Length != 4 || parts[0] != Prefix)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out iterations) || iterations <= 0)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length == SaltBytes && hash.Length == HashBytes;
    }
}
