using Habita.Domain;

namespace Habita.Application;

// Puertos: contratos que la aplicación necesita de la infraestructura.

public interface IRecordingRepository
{
    Task AddAsync(Recording recording, CancellationToken ct = default);
    Task<Recording?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(Recording recording, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListPendingIdsAsync(CancellationToken ct = default);
    Task UpsertChunkAsync(Chunk chunk, CancellationToken ct = default);
    Task<IReadOnlyList<Chunk>> GetChunksAsync(string recordingId, CancellationToken ct = default);
}

/// <summary>Almacén del audio original (disco local hoy, Azure Blob Storage en producción).</summary>
public interface IAudioStore
{
    Task<string> SaveAsync(string recordingId, int seq, ReadOnlyMemory<byte> data, string extension, CancellationToken ct = default);
    Task<byte[]> ReadAsync(string audioKey, CancellationToken ct = default);
    Task DeleteRecordingAsync(string recordingId, CancellationToken ct = default);
}

public interface ITranscriber
{
    /// <summary>Nombre del proveedor que expone /api/health: azure-openai, demo o none.</summary>
    string Name { get; }
    Task<string> TranscribeAsync(byte[] audio, string fileName, string mime, string prompt, CancellationToken ct = default);
}

public interface ITextGenerator
{
    Task<string> GenerateAsync(string system, string user, CancellationToken ct = default);
}

/// <summary>Cola de trabajos de procesamiento (en memoria hoy, Azure Service Bus en producción).</summary>
public interface IJobQueue
{
    void Enqueue(string recordingId);
}

public sealed class RecordingNotFoundException(string id) : Exception($"Grabación no encontrada: {id}");
