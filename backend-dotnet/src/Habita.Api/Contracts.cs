using System.Text.Json.Serialization;
using Habita.Application;
using Habita.Domain;

namespace Habita.Api;

// Contrato HTTP: JSON en snake_case, el mismo que espera el frontend (src/lib/recorderApi.ts).

public sealed record StartRecordingRequest(int MeetingId, string Title, string Community, string ConsentBy, MeetingContext? Context = null);

public sealed record MarkerRequest([property: JsonPropertyName("t_ms")] long TMs, string Kind, string Label);

public sealed record FinishRecordingRequest(IReadOnlyList<MarkerRequest>? Markers = null, MeetingContext? Context = null);

public sealed record DraftRequest(string Draft);

public sealed record HealthResponse(bool Ok, string AiProvider);

public sealed record ProgressResponse(int Chunks, int Transcribed, int Failed);

public sealed record TranscriptLineResponse([property: JsonPropertyName("t_ms")] long TMs, string Ts, string Text);

public sealed record MarkerResponse([property: JsonPropertyName("t_ms")] long TMs, string Kind, string Label);

public sealed record RecordingResponse(
    string Id,
    int MeetingId,
    string Title,
    string Status,
    string Error,
    string ConsentBy,
    double ConsentAt,
    double CreatedAt,
    double? FinishedAt,
    bool AudioDeleted,
    string Draft,
    ProgressResponse Progress,
    IReadOnlyList<TranscriptLineResponse> Transcript,
    IReadOnlyList<MarkerResponse> Markers)
{
    public static RecordingResponse From(Recording rec, IReadOnlyList<Chunk> chunks) => new(
        rec.Id,
        rec.MeetingId,
        rec.Title,
        Lower(rec.Status),
        rec.Error,
        rec.ConsentBy,
        Seconds(rec.ConsentAt),
        Seconds(rec.CreatedAt),
        rec.FinishedAt is { } f ? Seconds(f) : null,
        rec.AudioDeleted,
        rec.Draft,
        new ProgressResponse(chunks.Count, chunks.Count(c => c.Status == ChunkStatus.Transcribed), chunks.Count(c => c.Status == ChunkStatus.Failed)),
        [.. ActaDrafting.TranscriptLines(chunks).Select(l => new TranscriptLineResponse(l.StartMs, Timestamp.Format(l.StartMs), l.Text))],
        [.. rec.Markers.Select(m => new MarkerResponse(m.TMs, Lower(m.Kind), m.Label))]);

    /// <summary>Segundos Unix con decimales, como el prototipo.</summary>
    private static double Seconds(DateTimeOffset t) => t.ToUnixTimeMilliseconds() / 1000.0;

#pragma warning disable CA1308 // el contrato usa minúsculas ("ready", "agenda")
    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
#pragma warning restore CA1308

    internal static Marker ToMarker(MarkerRequest m)
    {
        if (m.TMs < 0) throw new DomainException("t_ms no puede ser negativo.");
        if (m.Label.Length > 300) throw new DomainException("La etiqueta del marcador supera 300 caracteres.");
        var kind = m.Kind switch
        {
            "agenda" => MarkerKind.Agenda,
            "vote" => MarkerKind.Vote,
            "note" => MarkerKind.Note,
            _ => throw new DomainException("kind debe ser agenda, vote o note."),
        };
        return new Marker(m.TMs, kind, m.Label);
    }
}
