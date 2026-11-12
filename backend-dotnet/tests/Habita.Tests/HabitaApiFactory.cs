using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Habita.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Habita.Tests;

/// <summary>IA falsa: registra lo que recibe y falla si el nombre del archivo contiene alguno de los textos indicados.</summary>
public sealed class FakeAi(params string[] failWhenFileContains) : ITranscriber, ITextGenerator
{
    public ConcurrentQueue<string> DraftPrompts { get; } = new();

    public string Name => "fake";

    public Task<string> TranscribeAsync(byte[] audio, string fileName, string mime, string prompt, CancellationToken ct = default) =>
        failWhenFileContains.Any(fileName.Contains)
            ? Task.FromException<string>(new InvalidOperationException("boom"))
            : Task.FromResult($"texto de {fileName}");

    public Task<string> GenerateAsync(string system, string user, CancellationToken ct = default)
    {
        DraftPrompts.Enqueue(user);
        return Task.FromResult("Se trató el punto. [00:00:05]");
    }
}

/// <summary>Persona de prueba: se convierte en un token de Entra External ID firmado con la clave de prueba.</summary>
public sealed record TestUser(string Oid, string? Email, string Name = "Persona de prueba", bool SuperAdmin = false)
{
    public static readonly TestUser Super = new("oid-super", "super@habita.test", "Superadministración", SuperAdmin: true);
    public static readonly TestUser Admin = new("oid-admin", "admin@olivos.test", "Administración Los Olivos");
}

/// <summary>
/// API completa en memoria, con SQLite y audio en una carpeta temporal propia de cada prueba.
/// La autenticación es la real (JwtBearer de Entra External ID), con una clave de firma de prueba.
/// Con development=true usa el modo Development, como al correr la API en local.
/// </summary>
public sealed class HabitaApiFactory(FakeAi ai, bool development = false) : WebApplicationFactory<Program>
{
    public const string Issuer = "https://tenant-prueba.ciamlogin.com/tenant-prueba/v2.0";
    public const string Audience = "habita-api-prueba";
    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32));

    private readonly string _dir = Directory.CreateTempSubdirectory("habita-tests-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(development ? "Development" : "Testing");
        builder.UseSetting("ConnectionStrings:Habita", $"Data Source={Path.Combine(_dir, "habita.db")};Pooling=False");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:Initialize", "EnsureCreated");
        builder.UseSetting("Storage:AudioPath", Path.Combine(_dir, "audio"));
        builder.UseSetting("Processing:RetryBaseDelay", "00:00:00.010");
        builder.UseSetting("Ai:Provider", "none");
        if (development)
        {
            builder.UseSetting("Authentication:Mode", "Development");
        }
        else
        {
            builder.UseSetting("Authentication:Mode", "EntraExternalId");
            builder.UseSetting("Authentication:EntraExternalId:Authority", Issuer);
            builder.UseSetting("Authentication:EntraExternalId:Audience", Audience);
        }
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITranscriber>(ai);
            services.AddSingleton<ITextGenerator>(ai);
            if (!development)
            {
                // Sin descargar metadatos del tenant: el emisor y la clave de firma son los de prueba.
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    o.TokenValidationParameters.ValidIssuer = Issuer;
                    o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                });
            }
        });
    }

    /// <summary>Cliente HTTP con el token de la persona indicada.</summary>
    public HttpClient ClientFor(TestUser user)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        return client;
    }

    public static string Token(TestUser user, string audience = Audience, string issuer = Issuer, SecurityKey? key = null)
    {
        var claims = new Dictionary<string, object> { ["oid"] = user.Oid, ["name"] = user.Name };
        if (user.Email is not null) claims["email"] = user.Email;
        if (user.SuperAdmin) claims["roles"] = new[] { "Habita.SuperAdmin" };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>La superadministración crea una comunidad con adminEmail como administración; devuelve su id.</summary>
    public async Task<string> CreateCommunityAsync(string name = "Los Olivos", string adminEmail = "admin@olivos.test")
    {
        using var super = ClientFor(TestUser.Super);
        var r = await super.PostAsJsonAsync("/api/communities", new { name, admin_email = adminEmail });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* SQLite puede tardar en liberar el archivo */ }
    }
}
