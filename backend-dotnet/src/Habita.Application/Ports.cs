using Habita.Domain;

namespace Habita.Application;

// Puertos: contratos que la aplicación necesita de la infraestructura.

public interface IRecordingRepository
{
    Task AddAsync(Recording recording, CancellationToken ct = default);
    Task<Recording?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Comunidad a la que pertenece la grabación, o null si no existe.</summary>
    Task<string?> GetCommunityIdAsync(string id, CancellationToken ct = default);
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

/// <summary>Comunidades, personas y sus roles (Azure SQL).</summary>
public interface IAccessRepository
{
    Task AddCommunityAsync(Community community, CancellationToken ct = default);
    Task<Community?> GetCommunityAsync(string id, CancellationToken ct = default);
    Task<Person?> FindPersonByExternalIdAsync(string externalId, CancellationToken ct = default);
    Task<Person?> FindPersonByEmailAsync(string email, CancellationToken ct = default);
    Task AddPersonAsync(Person person, CancellationToken ct = default);
    /// <summary>Vincula una persona invitada con su cuenta externa. Devuelve false si ya estaba vinculada.</summary>
    Task<bool> TryLinkAsync(string personId, string externalId, string? displayName, CancellationToken ct = default);
    Task<IReadOnlyList<CommunityRole>> GetRolesAsync(string personId, string communityId, CancellationToken ct = default);
    Task<IReadOnlyList<MembershipView>> GetMembershipsAsync(string personId, CancellationToken ct = default);
    Task<IReadOnlyList<Member>> GetMembersAsync(string communityId, CancellationToken ct = default);
    /// <summary>Agrega el rol si no existía. Devuelve false si la persona ya lo tenía.</summary>
    Task<bool> AddMembershipAsync(Membership membership, CancellationToken ct = default);
    Task<bool> RemoveMembershipAsync(string communityId, string personId, CommunityRole role, CancellationToken ct = default);
    Task<int> CountRoleAsync(string communityId, CommunityRole role, CancellationToken ct = default);
}

public sealed class RecordingNotFoundException(string id) : Exception($"Grabación no encontrada: {id}");
