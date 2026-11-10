using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.RegularExpressions;
using Habita.Application;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;

namespace Habita.Infrastructure.Ai;

public sealed class AzureOpenAiOptions
{
    public const string Section = "AzureOpenAI";

    /// <summary>Recurso de Azure OpenAI o de Azure AI Foundry, por ejemplo https://mi-recurso.openai.azure.com.</summary>
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>Nombres de los DEPLOYMENTS creados en Azure, no de los modelos.</summary>
    public string TranscribeDeployment { get; set; } = "gpt-4o-transcribe";
    public string ChatDeployment { get; set; } = "gpt-4o";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>API v1 de Azure OpenAI: compatible con el SDK oficial de OpenAI y sin api-version.</summary>
    public Uri V1Endpoint()
    {
        var baseUrl = Endpoint!.Trim().TrimEnd('/');
        var v1Suffix = string.Join('/', string.Empty, "openai", "v1");
        if (!baseUrl.EndsWith(v1Suffix, StringComparison.OrdinalIgnoreCase)) baseUrl += v1Suffix;
        return new Uri(baseUrl + '/');
    }
}

/// <summary>Un mismo proveedor transcribe y redacta.</summary>
public interface IAiAdapter : ITranscriber, ITextGenerator;

/// <summary>Azure OpenAI con el SDK oficial de OpenAI para .NET. El "modelo" es el nombre del deployment.</summary>
public sealed class AzureOpenAiAdapter : IAiAdapter
{
    private readonly AudioClient _audio;
    private readonly ChatClient _chat;

    public AzureOpenAiAdapter(AzureOpenAiOptions options, PipelineTransport? transport = null)
    {
        var clientOptions = new OpenAIClientOptions { Endpoint = options.V1Endpoint(), NetworkTimeout = options.Timeout };
        if (transport is not null) clientOptions.Transport = transport;
        var credential = new ApiKeyCredential(options.ApiKey!);
        _audio = new AudioClient(options.TranscribeDeployment, credential, clientOptions);
        _chat = new ChatClient(options.ChatDeployment, credential, clientOptions);
    }

    public string Name => "azure-openai";

    public async Task<string> TranscribeAsync(byte[] audio, string fileName, string mime, string prompt, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(audio, writable: false);
        var options = new AudioTranscriptionOptions { Language = "es", Prompt = prompt, ResponseFormat = AudioTranscriptionFormat.Simple };
        AudioTranscription result = await _audio.TranscribeAudioAsync(stream, fileName, options, ct);
        return result.Text;
    }

    public async Task<string> GenerateAsync(string system, string user, CancellationToken ct = default)
    {
        ChatCompletion completion = await _chat.CompleteChatAsync([new SystemChatMessage(system), new UserChatMessage(user)], cancellationToken: ct);
        return completion.Content.Count > 0 ? completion.Content[0].Text : "";
    }
}

/// <summary>
/// Modo demostración sin ningún servicio externo: transcripción y redacción SIMULADAS.
/// Sirve para probar la interfaz de punta a punta; nunca para actas reales.
/// </summary>
public sealed partial class DemoAiAdapter : IAiAdapter
{
    private static readonly string[] Phrases =
    [
        "Buenas noches a todos, siendo las seis de la tarde damos inicio a la asamblea general ordinaria. Hay quórum para deliberar y decidir.",
        "Propongo como presidente de la asamblea al señor del apartamento A ciento dos y como secretaria a la señora del apartamento B trescientos cuatro.",
        "El administrador presenta el informe de gestión: se ejecutó el noventa por ciento del presupuesto y se renovó el sistema de cámaras de seguridad.",
        "Se somete a consideración el presupuesto del año siguiente, con un incremento del seis por ciento en las cuotas de administración.",
        "Un copropietario solicita que el parque infantil se incluya en la renovación de zonas comunes. El consejo toma nota de la proposición.",
    ];

    public string Name => "demo";

    public Task<string> TranscribeAsync(byte[] audio, string fileName, string mime, string prompt, CancellationToken ct = default)
    {
        var match = SeqPattern().Match(fileName);
        var seq = match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return Task.FromResult($"[SIMULADO] {Phrases[seq % Phrases.Length]}");
    }

    public Task<string> GenerateAsync(string system, string user, CancellationToken ct = default)
    {
        var lines = user.Split('\n').Where(l => l.StartsWith('[')).Select(l => "- " + l);
        return Task.FromResult("[SIMULADO] Redacción de prueba, sin modelo de IA. Fragmentos de este punto:\n\n" + string.Join("\n", lines));
    }

    [GeneratedRegex(@"-(\d+)\.[^.]+$")]
    private static partial Regex SeqPattern();
}

/// <summary>Sin proveedor: se puede grabar, pero no transcribir.</summary>
public sealed class UnconfiguredAiAdapter : IAiAdapter
{
    private const string Message = "No hay proveedor de IA configurado (define la sección AzureOpenAI).";

    public string Name => "none";

    public Task<string> TranscribeAsync(byte[] audio, string fileName, string mime, string prompt, CancellationToken ct = default) =>
        Task.FromException<string>(new InvalidOperationException(Message));

    public Task<string> GenerateAsync(string system, string user, CancellationToken ct = default) =>
        Task.FromException<string>(new InvalidOperationException(Message));
}
