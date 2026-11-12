using Habita.Application;
using Habita.Domain;
using Microsoft.EntityFrameworkCore;

namespace Habita.Infrastructure.Persistence;

/// <summary>
/// Repositorio sobre EF Core (SQL Server LocalDB o Azure SQL). Cada operación usa un DbContext corto,
/// así que es seguro llamarlo en paralelo desde el worker.
/// </summary>
internal sealed class EfRecordingRepository(IDbContextFactory<HabitaDbContext> factory) : IRecordingRepository
{
    public async Task AddAsync(Recording recording, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Recordings.Add(recording);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Recording?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<string?> GetCommunityIdAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Recordings.Where(r => r.Id == id).Select(r => r.CommunityId).FirstOrDefaultAsync(ct);
    }

    public async Task SaveAsync(Recording recording, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Recordings.Update(recording);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListPendingIdsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Recordings
            .Where(r => r.Status == RecordingStatus.Queued || r.Status == RecordingStatus.Transcribing || r.Status == RecordingStatus.Drafting)
            .Select(r => r.Id)
            .ToListAsync(ct);
    }

    public async Task UpsertChunkAsync(Chunk chunk, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var exists = await db.Chunks.AnyAsync(c => c.RecordingId == chunk.RecordingId && c.Seq == chunk.Seq, ct);
        if (exists) db.Chunks.Update(chunk);
        else db.Chunks.Add(chunk);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (!exists)
        {
            // Otra subida del mismo fragmento ganó la carrera: se reintenta como actualización.
            await using var retry = await factory.CreateDbContextAsync(ct);
            retry.Chunks.Update(chunk);
            await retry.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<Chunk>> GetChunksAsync(string recordingId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Chunks.AsNoTracking().Where(c => c.RecordingId == recordingId).OrderBy(c => c.Seq).ToListAsync(ct);
    }
}
