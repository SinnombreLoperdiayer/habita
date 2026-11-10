using Habita.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Habita.Application;

public sealed class ProcessingOptions
{
    public const string Section = "Processing";

    /// <summary>Límite de archivo del servicio de transcripción (25 MB) con margen.</summary>
    public int MaxChunkBytes { get; set; } = 24 * 1024 * 1024;
    public int TranscriptionConcurrency { get; set; } = 3;
    public int DraftingConcurrency { get; set; } = 4;
    public int TranscriptionAttempts { get; set; } = 3;
    /// <summary>Espera base entre reintentos; se duplica en cada intento.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Proveedores de IA que usa el procesamiento: transcripción y redacción.</summary>
public sealed record RecordingAi(ITranscriber Transcriber, ITextGenerator Generator);

/// <summary>Casos de uso de grabación. No conoce OpenAI, SQL Server ni ASP.NET Core.</summary>
public sealed class RecordingService(
    IRecordingRepository repo,
    IAudioStore audio,
    RecordingAi ai,
    IJobQueue queue,
    TimeProvider clock,
    IOptions<ProcessingOptions> options,
    ILogger<RecordingService> log)
{
    private static readonly Dictionary<string, string> ExtensionByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/webm"] = "webm", ["audio/mp4"] = "mp4", ["audio/ogg"] = "ogg",
        ["audio/wav"] = "wav", ["audio/mpeg"] = "mp3", ["audio/x-m4a"] = "m4a",
    };

    private readonly ProcessingOptions _opt = options.Value;

    public int MaxChunkBytes => _opt.MaxChunkBytes;

    // ---- captura --------------------------------------------------------

    public async Task<Recording> StartAsync(int meetingId, string title, string community, string consentBy, MeetingContext? context, CancellationToken ct = default)
    {
        var rec = Recording.Start(meetingId, title, community, consentBy, context, clock.GetUtcNow());
        await repo.AddAsync(rec, ct);
        return rec;
    }

    public async Task<Chunk> UploadChunkAsync(string recordingId, int seq, long startMs, long durationMs, string contentType, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var rec = await GetAsync(recordingId, ct);
        rec.EnsureAcceptsAudio();
        if (data.IsEmpty) throw new DomainException("Fragmento vacío.");
        if (data.Length > _opt.MaxChunkBytes) throw new DomainException("Fragmento demasiado grande.");

        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        var key = await audio.SaveAsync(recordingId, seq, data, ExtensionByMime.GetValueOrDefault(mime, "webm"), ct);
        var chunk = new Chunk(recordingId, seq, startMs, durationMs, mime, key);
        await repo.UpsertChunkAsync(chunk, ct);

        if (rec.Status is RecordingStatus.Ready or RecordingStatus.Failed) // fragmento tardío: reprocesar
        {
            rec.Requeue();
            await repo.SaveAsync(rec, ct);
            queue.Enqueue(recordingId);
        }
        return chunk;
    }

    public async Task<Recording> FinishAsync(string recordingId, IEnumerable<Marker> markers, MeetingContext? context, CancellationToken ct = default)
    {
        var rec = await GetAsync(recordingId, ct);
        rec.Finish(markers, context, clock.GetUtcNow());
        await repo.SaveAsync(rec, ct);
        queue.Enqueue(recordingId);
        return rec;
    }

    // ---- procesamiento (worker) ----------------------------------------

    public async Task ProcessAsync(string recordingId, CancellationToken ct = default)
    {
        var rec = await GetAsync(recordingId, ct);
        try
        {
            rec.BeginTranscription();
            await repo.SaveAsync(rec, ct);
            await TranscribePendingAsync(rec, ct);

            var chunks = await repo.GetChunksAsync(recordingId, ct);
            if (chunks.Count > 0 && chunks.All(c => c.Status == ChunkStatus.Failed))
                throw new InvalidOperationException("No se pudo transcribir ningún fragmento: " + (chunks[0].Error is { Length: > 0 } e ? e : "error desconocido"));

            rec.BeginDrafting();
            await repo.SaveAsync(rec, ct);
            rec.Complete(await BuildDraftAsync(rec, chunks, ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Fallo procesando la grabación {RecordingId}", recordingId);
            rec.Fail(ex.Message);
        }
        await repo.SaveAsync(rec, ct);
    }

    private async Task TranscribePendingAsync(Recording rec, CancellationToken ct)
    {
        var pending = (await repo.GetChunksAsync(rec.Id, ct)).Where(c => c.Status != ChunkStatus.Transcribed).ToList();
        var prompt = ActaDrafting.TranscriptionPrompt(rec);
        using var gate = new SemaphoreSlim(_opt.TranscriptionConcurrency);

        await Task.WhenAll(pending.Select(async c =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var fileName = $"{rec.Id}-{c.Seq}.{Path.GetExtension(c.AudioKey).TrimStart('.')}";
                var lastError = "";
                for (var attempt = 0; attempt < _opt.TranscriptionAttempts; attempt++)
                {
                    try
                    {
                        var data = await audio.ReadAsync(c.AudioKey, ct);
                        c.MarkTranscribed(await ai.Transcriber.TranscribeAsync(data, fileName, c.Mime, prompt, ct));
                        break;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        lastError = ex.Message;
                        log.LogWarning(ex, "Intento {Attempt} fallido transcribiendo {File}", attempt + 1, fileName);
                        if (attempt + 1 < _opt.TranscriptionAttempts)
                            await Task.Delay(_opt.RetryBaseDelay * Math.Pow(2, attempt), clock, ct);
                    }
                }
                if (c.Status != ChunkStatus.Transcribed) c.MarkFailed(lastError);
                await repo.UpsertChunkAsync(c, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private async Task<string> BuildDraftAsync(Recording rec, IReadOnlyList<Chunk> chunks, CancellationToken ct)
    {
        var sections = ActaDrafting.Sections(rec, chunks);
        using var gate = new SemaphoreSlim(_opt.DraftingConcurrency);

        var bodies = await Task.WhenAll(sections.Select(async s =>
        {
            if (!s.HasSpeech) return ActaDrafting.NoSpeech;
            await gate.WaitAsync(ct);
            try
            {
                return (await ai.Generator.GenerateAsync(ActaDrafting.SectionSystemPrompt, ActaDrafting.SectionUserPrompt(s), ct)).Trim();
            }
            finally
            {
                gate.Release();
            }
        }));

        return ActaDrafting.RenderActa(rec, [.. sections.Select((s, i) => (s.Title, bodies[i]))]);
    }

    // ---- revisión ---------------------------------------------------------

    public async Task<Recording> RegenerateAsync(string recordingId, CancellationToken ct = default)
    {
        var rec = await GetAsync(recordingId, ct);
        rec.Requeue();
        await repo.SaveAsync(rec, ct);
        queue.Enqueue(recordingId);
        return rec;
    }

    public async Task<Recording> SaveDraftAsync(string recordingId, string draft, CancellationToken ct = default)
    {
        var rec = await GetAsync(recordingId, ct);
        rec.EditDraft(draft);
        await repo.SaveAsync(rec, ct);
        return rec;
    }

    public async Task<Recording> DeleteAudioAsync(string recordingId, CancellationToken ct = default)
    {
        var rec = await GetAsync(recordingId, ct);
        rec.MarkAudioDeleted();
        await audio.DeleteRecordingAsync(recordingId, ct);
        await repo.SaveAsync(rec, ct);
        return rec;
    }

    public async Task<(Recording Recording, IReadOnlyList<Chunk> Chunks)> ViewAsync(string recordingId, CancellationToken ct = default) =>
        (await GetAsync(recordingId, ct), await repo.GetChunksAsync(recordingId, ct));

    public Task<IReadOnlyList<Chunk>> ChunksAsync(string recordingId, CancellationToken ct = default) => repo.GetChunksAsync(recordingId, ct);

    public string ProviderName => ai.Transcriber.Name;

    private async Task<Recording> GetAsync(string id, CancellationToken ct) =>
        await repo.GetAsync(id, ct) ?? throw new RecordingNotFoundException(id);
}
