using System.Security.Claims;
using System.Text.Encodings.Web;
using Habita.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Habita.Api;

/// <summary>Sección "Authentication" de la configuración.</summary>
public sealed class HabitaAuthOptions
{
    public const string Section = "Authentication";
    public const string EntraMode = "EntraExternalId";
    public const string DevelopmentMode = "Development";

    /// <summary>EntraExternalId (tokens JWT de Microsoft Entra External ID) o Development (usuario fijo, solo en el entorno Development).</summary>
    public string Mode { get; set; } = EntraMode;
    public EntraExternalIdOptions EntraExternalId { get; set; } = new();
    public DevelopmentUserOptions Development { get; set; } = new();

    public bool IsDevelopmentMode => string.Equals(Mode, DevelopmentMode, StringComparison.OrdinalIgnoreCase);
}

public sealed class EntraExternalIdOptions
{
    /// <summary>https://&lt;subdominio&gt;.ciamlogin.com/&lt;id-del-tenant&gt;/v2.0</summary>
    public string? Authority { get; set; }
    /// <summary>Application (client) ID de la app registrada para la API.</summary>
    public string? Audience { get; set; }
}

/// <summary>Persona con la que entra toda solicitud en modo Development.</summary>
public sealed class DevelopmentUserOptions
{
    public string ExternalId { get; set; } = "dev-admin";
    public string Email { get; set; } = "admin@habita.local";
    public string Name { get; set; } = "Administración (desarrollo)";
    public bool SuperAdmin { get; set; } = true;
    /// <summary>Comunidad que se crea al arrancar, con esta persona como administradora.</summary>
    public string Community { get; set; } = "Conjunto de desarrollo";
}

/// <summary>Claims de los tokens de Entra External ID que usa Habita.</summary>
public static class HabitaClaims
{
    public const string ObjectId = "oid";
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Roles = "roles";
    /// <summary>App role de la API en Entra: la superadministración de Habita.</summary>
    public const string SuperAdminRole = "Habita.SuperAdmin";
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddHabitaAuthentication(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var section = config.GetSection(HabitaAuthOptions.Section);
        var options = section.Get<HabitaAuthOptions>() ?? new HabitaAuthOptions();
        services.Configure<HabitaAuthOptions>(section);
        services.AddAuthorization();

        if (options.IsDevelopmentMode)
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Authentication:Mode=Development solo se permite en el entorno Development.");
            services.AddAuthentication(DevelopmentAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentAuthHandler.SchemeName, null);
            return services;
        }

        if (!string.Equals(options.Mode, HabitaAuthOptions.EntraMode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Authentication:Mode debe ser {HabitaAuthOptions.EntraMode} o {HabitaAuthOptions.DevelopmentMode}.");

        var entra = options.EntraExternalId;
        if (!Uri.TryCreate(entra.Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(entra.Audience))
            throw new InvalidOperationException(
                "Configura Authentication:EntraExternalId:Authority (https://<subdominio>.ciamlogin.com/<id-del-tenant>/v2.0) " +
                "y Authentication:EntraExternalId:Audience (client ID de la app de la API).");

        var audience = entra.Audience.Trim();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                // El emisor y las claves de firma salen del documento de metadatos del tenant.
                o.Authority = authority.AbsoluteUri.TrimEnd('/');
                o.MapInboundClaims = false;
                o.TokenValidationParameters.ValidAudiences = [audience, "api://" + audience];
                o.TokenValidationParameters.NameClaimType = HabitaClaims.Name;
                o.TokenValidationParameters.RoleClaimType = HabitaClaims.Roles;
            });
        return services;
    }

    /// <summary>Convierte el usuario autenticado en el Caller que entienden los casos de uso.</summary>
    public static Caller ToCaller(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(HabitaClaims.ObjectId) ?? user.FindFirstValue(HabitaClaims.Subject)
            ?? throw new AccessDeniedException("El token no identifica a la persona (falta el claim oid).");
        return new Caller(
            id,
            user.FindFirstValue(HabitaClaims.Email),
            user.FindFirstValue(HabitaClaims.Name),
            user.HasClaim(HabitaClaims.Roles, HabitaClaims.SuperAdminRole));
    }

    /// <summary>En modo Development, deja lista una comunidad con el usuario de desarrollo como administrador.</summary>
    public static async Task SeedDevelopmentAccessAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var options = services.GetRequiredService<IOptions<HabitaAuthOptions>>().Value;
        if (!options.IsDevelopmentMode) return;
        var dev = options.Development;
        var developer = new Caller(dev.ExternalId, dev.Email, dev.Name, dev.SuperAdmin);
        await services.GetRequiredService<AccessService>().EnsureDevelopmentCommunityAsync(developer, dev.Community, ct);
    }
}

/// <summary>
/// Autenticación de desarrollo: toda solicitud entra como el usuario configurado. Permite usar el frontend y
/// Swagger sin un tenant de Entra. AddHabitaAuthentication la rechaza fuera del entorno Development.
/// </summary>
internal sealed class DevelopmentAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<HabitaAuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Development";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var dev = auth.Value.Development;
        List<Claim> claims =
        [
            new(HabitaClaims.ObjectId, dev.ExternalId),
            new(HabitaClaims.Email, dev.Email),
            new(HabitaClaims.Name, dev.Name),
        ];
        if (dev.SuperAdmin) claims.Add(new Claim(HabitaClaims.Roles, HabitaClaims.SuperAdminRole));
        var identity = new ClaimsIdentity(claims, SchemeName, HabitaClaims.Name, HabitaClaims.Roles);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
