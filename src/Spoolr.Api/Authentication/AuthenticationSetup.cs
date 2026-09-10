using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace Spoolr.Api.Authentication;

/// <summary>
/// Stands in for Microsoft Entra ID on a developer machine, authenticating every request as
/// a fixed local identity.
/// </summary>
/// <remarks>
/// Registered only when the environment is Development and no Entra ID tenant is configured.
/// Startup fails outside Development rather than falling back to this, because a service
/// that quietly accepts every caller is worse than one that refuses to start.
/// </remarks>
public sealed class LocalOperatorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string LocalOperatorName = "local-developer@localhost";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, LocalOperatorName),
                new Claim(ClaimTypes.Name, LocalOperatorName),
            ],
            SpoolrSchemes.Operator);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SpoolrSchemes.Operator)));
    }
}

public static class AuthenticationSetup
{
    private const string EntraSection = "AzureAd";

    public static IServiceCollection AddSpoolrAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var entraConfigured = !string.IsNullOrWhiteSpace(configuration[$"{EntraSection}:TenantId"])
            && !string.IsNullOrWhiteSpace(configuration[$"{EntraSection}:ClientId"]);

        if (!entraConfigured && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Microsoft Entra ID is not configured. Set {EntraSection}:TenantId and "
                + $"{EntraSection}:ClientId. The local development stand-in is refused "
                + "outside the Development environment.");
        }

        var authentication = services.AddAuthentication(SpoolrSchemes.Operator);

        if (entraConfigured)
        {
            authentication.AddMicrosoftIdentityWebApi(
                configuration,
                configSectionName: EntraSection,
                jwtBearerScheme: SpoolrSchemes.Operator);
        }
        else
        {
            authentication.AddScheme<AuthenticationSchemeOptions, LocalOperatorAuthenticationHandler>(
                SpoolrSchemes.Operator,
                displayName: "Local development operator",
                configureOptions: null);
        }

        authentication.AddScheme<AuthenticationSchemeOptions, DeviceKeyAuthenticationHandler>(
            SpoolrSchemes.Device,
            displayName: "Printer device key",
            configureOptions: null);

        services.AddAuthorizationBuilder()
            .AddPolicy(SpoolrPolicies.Operator, policy => policy
                .AddAuthenticationSchemes(SpoolrSchemes.Operator)
                .RequireAuthenticatedUser())
            .AddPolicy(SpoolrPolicies.Device, policy => policy
                .AddAuthenticationSchemes(SpoolrSchemes.Device)
                .RequireAuthenticatedUser()
                .RequireClaim(SpoolrClaims.PrinterId));

        return services;
    }
}
