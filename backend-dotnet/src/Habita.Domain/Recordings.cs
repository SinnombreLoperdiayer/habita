namespace Habita.Domain;

public enum RecordingStatus { Recording, Queued, Transcribing, Drafting, Ready, Failed }

public enum ChunkStatus { Uploaded, Transcribed, Failed }

public enum MarkerKind { Agenda, Vote, Note }

/// <summary>Marca de tiempo de la sesión: cambio de punto del orden del día, votación o nota.</summary>
public sealed record Marker(long TMs, MarkerKind Kind, string Label);

/// <summary>Regla de negocio incumplida. La API la traduce a 422.</summary>
public sealed class DomainException(string message) : Exception(message);

/// <summary>Grabación de una asamblea. Agrega el consentimiento, los marcadores y el borrador del acta.</summary>
public sealed class Recording
{
    public const int MaxErrorLength = 500;

    private Recording() { } // EF Core

    public string Id { get; private set; } = "";
    public string CommunityId { get; private set; } = "";
    public int MeetingId { get; private set; }
    public string Title { get; private set; } = "";
    public string Community { get; private set; } = "";
    public string ConsentBy { get; private set; } = "";
    public DateTimeOffset ConsentAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public RecordingStatus Status { get; private set; } = RecordingStatus.Recording;
    public DateTimeOffset? FinishedAt { get; private set; }
    public IReadOnlyList<Marker> Markers { get; private set; } = [];
    public MeetingContext Context { get; private set; } = MeetingContext.Empty;
    public string Draft { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool AudioDeleted { get; private set; }

    public bool IsProcessing => Status is RecordingStatus.Queued or RecordingStatus.Transcribing or RecordingStatus.Drafting;

    public static Recording Start(Community community, int meetingId, string title, string consentBy, MeetingContext? context, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(consentBy))
            throw new DomainException("Se requiere registrar quién autoriza la grabación (consentimiento).");
        return new Recording
        {
            Id = Ids.New(),
            CommunityId = community.Id,
            MeetingId = meetingId,
            Title = title,
            Community = community.Name,
            ConsentBy = consentBy.Trim(),
            ConsentAt = now,
            CreatedAt = now,
            Context = context ?? MeetingContext.Empty,
        };
    }

    public void EnsureAcceptsAudio()
    {
        if (AudioDeleted) throw new DomainException("El audio de esta grabación fue eliminado.");
    }

    public void Finish(IEnumerable<Marker> markers, MeetingContext? context, DateTimeOffset now)
    {
        Markers = [.. markers.OrderBy(m => m.TMs)];
        Context = Context.MergeWith(context);
        FinishedAt = now;
        Status = RecordingStatus.Queued;
    }

    /// <summary>Vuelve a la cola: regeneración o fragmento que llegó tarde.</summary>
    public void Requeue() => Status = RecordingStatus.Queued;

    public void BeginTranscription()
    {
        Status = RecordingStatus.Transcribing;
        Error = "";
    }

    public void BeginDrafting() => Status = RecordingStatus.Drafting;

    public void Complete(string draft)
    {
        Draft = draft;
        Status = RecordingStatus.Ready;
    }

    public void Fail(string error)
    {
        Status = RecordingStatus.Failed;
        Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }

    public void EditDraft(string draft) => Draft = draft;

    public void MarkAudioDeleted()
    {
        if (IsProcessing) throw new DomainException("Espera a que termine el procesamiento para eliminar el audio.");
        AudioDeleted = true;
    }
}

/// <summary>Segmento de audio independiente (unos 60 s) subido durante la grabación.</summary>
public sealed class Chunk
{
    public const int MaxErrorLength = 300;

    private Chunk() { } // EF Core

    public Chunk(string recordingId, int seq, long startMs, long durationMs, string mime, string audioKey)
    {
        RecordingId = recordingId;
        Seq = seq;
        StartMs = startMs;
        DurationMs = durationMs;
        Mime = mime;
        AudioKey = audioKey;
    }

    public string RecordingId { get; private set; } = "";
    public int Seq { get; private set; }
    public long StartMs { get; private set; }
    public long DurationMs { get; private set; }
    public string Mime { get; private set; } = "";
    /// <summary>Clave del audio en el almacén (ruta relativa hoy, nombre de blob en Azure).</summary>
    public string AudioKey { get; private set; } = "";
    public ChunkStatus Status { get; private set; } = ChunkStatus.Uploaded;
    public string Text { get; private set; } = "";
    public string Error { get; private set; } = "";

    /// <summary>Punto medio: decide a qué punto del orden del día pertenece el segmento.</summary>
    public long MidMs => StartMs + DurationMs / 2;

    public void MarkTranscribed(string text)
    {
        Text = text;
        Status = ChunkStatus.Transcribed;
        Error = "";
    }

    public void MarkFailed(string error)
    {
        Status = ChunkStatus.Failed;
        Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }
}

public static class Timestamp
{
    /// <summary>Milisegundos a hh:mm:ss.</summary>
    public static string Format(long ms)
    {
        var s = Math.Max(0, ms) / 1000;
        return $"{s / 3600:00}:{s % 3600 / 60:00}:{s % 60:00}";
    }
}
