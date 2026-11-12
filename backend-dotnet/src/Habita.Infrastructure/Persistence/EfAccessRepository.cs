using Habita.Application;
using Habita.Domain;
using Microsoft.EntityFrameworkCore;

namespace Habita.Infrastructure.Persistence;

/// <summary>Comunidades, personas y roles sobre EF Core. Un DbContext corto por operación, como EfRecordingRepository.</summary>
internal sealed class EfAccessRepository(IDbContextFactory<HabitaDbContext> factory) : IAccessRepository
{
    public async Task AddCommunityAsync(Community community, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Communities.Add(community);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Community?> GetCommunityAsync(string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Person?> FindPersonByExternalIdAsync(string externalId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.ExternalId == externalId, ct);
    }

    public async Task<Person?> FindPersonByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Email == email, ct);
    }

    public async Task AddPersonAsync(Person person, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.People.Add(person);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryLinkAsync(string personId, string externalId, string? displayName, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        try
        {
            // La condición ExternalId == null hace que dos inicios de sesión simultáneos no se pisen.
            var linked = await db.People
                .Where(p => p.Id == personId && p.ExternalId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExternalId, externalId), ct);
            if (linked == 1 && displayName is not null)
                await db.People.Where(p => p.Id == personId && p.DisplayName == p.Email)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.DisplayName, displayName), ct);
            return linked == 1;
        }
        catch (DbUpdateException)
        {
            return false; // la cuenta externa ya estaba vinculada a otra persona (índice único)
        }
    }

    public async Task<IReadOnlyList<CommunityRole>> GetRolesAsync(string personId, string communityId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Memberships.Where(m => m.PersonId == personId && m.CommunityId == communityId).Select(m => m.Role).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MembershipView>> GetMembershipsAsync(string personId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await (
            from m in db.Memberships
            join c in db.Communities on m.CommunityId equals c.Id
            where m.PersonId == personId
            select new { c.Id, c.Name, m.Role }).ToListAsync(ct);
        return [.. rows.GroupBy(r => (r.Id, r.Name))
            .OrderBy(g => g.Key.Name, StringComparer.CurrentCulture)
            .Select(g => new MembershipView(g.Key.Id, g.Key.Name, [.. g.Select(r => r.Role).Order()]))];
    }

    public async Task<IReadOnlyList<Member>> GetMembersAsync(string communityId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await (
            from m in db.Memberships
            join p in db.People on m.PersonId equals p.Id
            where m.CommunityId == communityId
            select new { Person = p, m.Role }).AsNoTracking().ToListAsync(ct);
        return [.. rows.GroupBy(r => r.Person.Id)
            .Select(g => new Member(g.First().Person, [.. g.Select(r => r.Role).Order()]))
            .OrderBy(m => m.Person.DisplayName, StringComparer.CurrentCulture)];
    }

    public async Task<bool> AddMembershipAsync(Membership membership, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var exists = await db.Memberships.AnyAsync(
            m => m.CommunityId == membership.CommunityId && m.PersonId == membership.PersonId && m.Role == membership.Role, ct);
        if (exists) return false;
        db.Memberships.Add(membership);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // otra solicitud agregó el mismo rol al mismo tiempo
        }
    }

    public async Task<bool> RemoveMembershipAsync(string communityId, string personId, CommunityRole role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var removed = await db.Memberships
            .Where(m => m.CommunityId == communityId && m.PersonId == personId && m.Role == role)
            .ExecuteDeleteAsync(ct);
        return removed > 0;
    }

    public async Task<int> CountRoleAsync(string communityId, CommunityRole role, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Memberships.CountAsync(m => m.CommunityId == communityId && m.Role == role, ct);
    }
}
