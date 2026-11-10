using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Habita.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Habita.Application;
using Habita.Infrastructure;

namespace Habita.Tests;

/// <summary>Valida el adaptador contra un Azure OpenAI falso: rutas v1, credencial, deployments y parámetros.</summary>
public sealed class AzureOpenAiAdapterTests
{
    private sealed record Seen(string Path, string? Auth, string ContentType, string Body);

    private sealed class FakeAzure : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString() ?? "", body));
            var json = request.RequestUri.AbsolutePath.EndsWith("/audio/transcriptions", StringComparison.Ordinal)
                ? """{"text":"texto transcrito"}"""
                : """{"id":"x","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"borrador"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Uses_v1_endpoint_with_deployment_names()
    {
        var fake = new FakeAzure();
        using var http = new HttpClient(fake);
        var adapter = new AzureOpenAiAdapter(
            new AzureOpenAiOptions { Endpoint = "https://mi-recurso.openai.azure.com/", ApiKey = "k", TranscribeDeployment = "mi-transcribe", ChatDeployment = "mi-chat" },
            new HttpClientPipelineTransport(http));

        Assert.Equal("texto transcrito", await adapter.TranscribeAsync("RIFFxxxx"u8.ToArray(), "a-0.webm", "audio/webm", "contexto"));
        Assert.Equal("borrador", await adapter.GenerateAsync("sys", "user"));

        var (t, c) = (fake.Requests[0], fake.Requests[1]);
        Assert.Equal("/openai/v1/audio/transcriptions", t.Path);
        Assert.Equal("/openai/v1/chat/completions", c.Path);
        Assert.Equal("Bearer k", t.Auth);
        Assert.Equal("Bearer k", c.Auth);
        Assert.StartsWith("multipart/form-data", t.ContentType, StringComparison.Ordinal);
        Assert.Contains("mi-transcribe", t.Body, StringComparison.Ordinal);
        Assert.Matches("name=\"?language\"?[\\s\\S]*\\bes\\b", t.Body);
        Assert.Contains("contexto", t.Body, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"mi-chat\"", c.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://x.openai.azure.com", "https://x.openai.azure.com/openai/v1/")]
    [InlineData("https://x.openai.azure.com/openai/v1/", "https://x.openai.azure.com/openai/v1/")]
    [InlineData("https://x.services.ai.azure.com/", "https://x.services.ai.azure.com/openai/v1/")]
    public void Builds_v1_endpoint(string endpoint, string expected) =>
        Assert.Equal(expected, new AzureOpenAiOptions { Endpoint = endpoint }.V1Endpoint().ToString());

    [Theory]
    [InlineData(null, null, "auto", "none")]
    [InlineData("https://x.openai.azure.com", "k", "auto", "azure-openai")]
    [InlineData(null, null, "demo", "demo")]
    public void Configuration_selects_the_provider(string? endpoint, string? key, string mode, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureOpenAI:Endpoint"] = endpoint, ["AzureOpenAI:ApiKey"] = key, ["Ai:Provider"] = mode,
        }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .AddSingleton<IHostEnvironment>(new FakeEnv())
            .AddLogging()
            .AddHabitaInfrastructure()
            .BuildServiceProvider();
        Assert.Equal(expected, services.GetRequiredService<ITranscriber>().Name);
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Habita.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
