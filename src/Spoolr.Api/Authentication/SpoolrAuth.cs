namespace Spoolr.Api.Authentication;

/// <summary>Authentication scheme names used by this service.</summary>
public static class SpoolrSchemes
{
    /// <summary>
    /// People and services acting on behalf of people. Backed by Microsoft Entra ID, or by
    /// a local stand-in when the service runs on a developer machine.
    /// </summary>
    public const string Operator = "Operator";

    /// <summary>Printers presenting the key they were issued at registration.</summary>
    public const string Device = "DeviceKey";
}

/// <summary>Authorization policy names used by this service.</summary>
public static class SpoolrPolicies
{
    /// <summary>Submitting, listing, and cancelling jobs; registering printers.</summary>
    public const string Operator = "operator";

    /// <summary>Polling for work, acknowledging it, reporting results, heartbeating.</summary>
    public const string Device = "device";
}

/// <summary>Claim types this service issues to authenticated devices.</summary>
public static class SpoolrClaims
{
    public const string PrinterId = "printer_id";
}
