using System.Collections.Concurrent;
using Habita.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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

/// <summary>API completa en memoria, con SQLite y audio en una carpeta temporal propia de cada prueba.</summary>
public sealed class HabitaApiFactory(FakeAi ai) : WebApplicationFactory<Program>
{
    private readonly string _dir = Directory.CreateTempSubdirectory("habita-tests-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Habita", $"Data Source={Path.Combine(_dir, "habita.db")};Pooling=False");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:Initialize", "EnsureCreated");
        builder.UseSetting("Storage:AudioPath", Path.Combine(_dir, "audio"));
        builder.UseSetting("Processing:RetryBaseDelay", "00:00:00.010");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITranscriber>(ai);
            services.AddSingleton<ITextGenerator>(ai);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* SQLite puede tardar en liberar el archivo */ }
    }
}
