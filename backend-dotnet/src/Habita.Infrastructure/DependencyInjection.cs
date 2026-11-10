using Habita.Application;
using Habita.Infrastructure.Ai;
using Habita.Infrastructure.Persistence;
using Habita.Infrastructure.Queue;
using Habita.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Habita.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registra los adaptadores. La configuración se lee al resolver cada servicio, no al registrarlo.</summary>
    public static IServiceCollection AddHabitaInfrastructure(this IServiceCollection services)
    {
        // Base de datos: SQL Server (LocalDB en desarrollo, Azure SQL en producción). SQLite para pruebas o equipos sin SQL Server.
        services.AddDbContextFactory<HabitaDbContext>((sp, o) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var connection = config.GetConnectionString("Habita")
                ?? throw new InvalidOperationException("Falta la cadena de conexión 'ConnectionStrings:Habita'.");
            if (IsSqlite(config)) o.UseSqlite(connection);
            else o.UseSqlServer(connection, sql => sql.EnableRetryOnFailure());
        });
        services.AddSingleton<IRecordingRepository, EfRecordingRepository>();

        // Audio original en disco (Blob Storage en producción).
        services.AddSingleton<IAudioStore>(sp =>
        {
            var root = sp.GetRequiredService<IConfiguration>()["Storage:AudioPath"] ?? "data/audio";
            return new FileAudioStore(Path.GetFullPath(root, sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
        });

        // IA: Azure OpenAI si está configurado; si no, demo (solo si se pide) o ninguno.
        services.AddSingleton(sp => CreateAiAdapter(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Habita.Ai")));
        services.AddSingleton<ITranscriber>(sp => sp.GetRequiredService<IAiAdapter>());
        services.AddSingleton<ITextGenerator>(sp => sp.GetRequiredService<IAiAdapter>());

        // Cola y worker en proceso (Service Bus + worker separado en producción).
        services.AddSingleton<ChannelJobQueue>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<ChannelJobQueue>());
        services.AddHostedService<RecordingWorker>();
        return services;
    }

    /// <summary>
    /// Database:Initialize = Migrate (aplica migraciones de SQL Server), EnsureCreated (SQLite) o None (producción:
    /// las migraciones se aplican en el despliegue con un script idempotente).
    /// </summary>
    public static async Task InitializeHabitaDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var config = services.GetRequiredService<IConfiguration>();
        var mode = config["Database:Initialize"] ?? "None";
        if (mode.Equals("None", StringComparison.OrdinalIgnoreCase)) return;

        await using var db = await services.GetRequiredService<IDbContextFactory<HabitaDbContext>>().CreateDbContextAsync(ct);
        if (mode.Equals("Migrate", StringComparison.OrdinalIgnoreCase) && !IsSqlite(config)) await db.Database.MigrateAsync(ct);
        else await db.Database.EnsureCreatedAsync(ct);
    }

    private static bool IsSqlite(IConfiguration config) =>
        string.Equals(config["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase);

    private static IAiAdapter CreateAiAdapter(IConfiguration config, ILogger log)
    {
        var azure = config.GetSection(AzureOpenAiOptions.Section).Get<AzureOpenAiOptions>() ?? new AzureOpenAiOptions();
        var mode = (config["Ai:Provider"] ?? "auto").ToLowerInvariant();
        IAiAdapter adapter = mode switch
        {
            "demo" => new DemoAiAdapter(),
            "none" => new UnconfiguredAiAdapter(),
            "azure" when !azure.IsConfigured => throw new InvalidOperationException("Ai:Provider=azure exige AzureOpenAI:Endpoint y AzureOpenAI:ApiKey."),
            _ when azure.IsConfigured => new AzureOpenAiAdapter(azure),
            _ => new UnconfiguredAiAdapter(),
        };
        log.LogInformation("Proveedor de IA: {Provider}", adapter.Name);
        return adapter;
    }
}
