using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Spoolr.Core.Abstractions;
using Spoolr.Core.Printers;

namespace Spoolr.Api.Authentication;

/// <summary>
/// Authenticates a printer from the key it was issued at registration.
/// </summary>
/// <remarks>
/// Expects <c>Authorization: DeviceKey &lt;printerId&gt;:&lt;key&gt;</c>.
///
/// A printer cannot complete an interactive sign-in, so it cannot hold an Entra ID token
/// the way a person does. It presents a long random key instead, and the service compares
/// it against a stored hash.
/// </remarks>
public sealed class DeviceKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IPrinterStore printers,
    IDeviceKeyHasher hasher) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <summary>
    /// Cached across requests because deriving it is deliberately expensive. A benign race
    /// on first use just produces two equally valid decoys.
    /// </summary>
    private static string? _decoyHash;

    /// <summary>
    /// Verified when no printer matches, so a request for an unknown printer costs the same
    /// as one for a known printer with the wrong key. Without it, response time alone tells
    /// an attacker which printer ids are real.
    /// </summary>
    private string DecoyHash => _decoyHash ??= hasher.Hash(hasher.GenerateKey());

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var raw = header.ToString();

        if (!raw.StartsWith($"{SpoolrSchemes.Device} ", StringComparison.OrdinalIgnoreCase))
        {
            // Some other scheme. Let the next handler deal with it.
            return AuthenticateResult.NoResult();
        }

        var credentials = raw[(SpoolrSchemes.Device.Length + 1)..].Trim();
        var separator = credentials.IndexOf(':');

        if (separator <= 0 || separator == credentials.Length - 1)
        {
            return AuthenticateResult.Fail("Malformed device credentials.");
        }

        if (!Guid.TryParse(credentials[..separator], out var printerId))
        {
            return AuthenticateResult.Fail("Malformed device credentials.");
        }

        var presentedKey = credentials[(separator + 1)..];
        var printer = await printers.FindAsync(printerId, Context.RequestAborted);

        if (printer is null)
        {
            hasher.Verify(presentedKey, DecoyHash);

            return AuthenticateResult.Fail("Unknown printer or invalid device key.");
        }

        if (!hasher.Verify(presentedKey, printer.DeviceKeyHash))
        {
            Logger.LogWarning("Rejected a device key for printer {PrinterId}.", printerId);

            // Deliberately the same message as the unknown-printer case.
            return AuthenticateResult.Fail("Unknown printer or invalid device key.");
        }

        if (printer.ReportedStatus == PrinterStatus.Retired)
        {
            return AuthenticateResult.Fail("This printer has been retired.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, printer.Id.ToString()),
                new Claim(ClaimTypes.Name, printer.Name),
                new Claim(SpoolrClaims.PrinterId, printer.Id.ToString()),
            ],
            SpoolrSchemes.Device);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SpoolrSchemes.Device));
    }
}
