using Habita.Domain;

namespace Habita.Application;

/// <summary>Quién hace la solicitud, según el token de Entra External ID.</summary>
/// <param name="ExternalId">Claim "oid": identificador estable de la cuenta en el tenant.</param>
/// <param name="IsSuperAdmin">App role Habita.SuperAdmin, el único rol global.</param>
public sealed record Caller(string ExternalId, string? Email, string? DisplayName, bool IsSuperAdmin);

public sealed record MembershipView(string CommunityId, string CommunityName, IReadOnlyList<CommunityRole> Roles);

public sealed record Member(Person Person, IReadOnlyList<CommunityRole> Roles);

/// <summary>Perfil de quien inició sesión. PersonId es null si nadie lo ha invitado a una comunidad.</summary>
public sealed record Profile(string? PersonId, string? Email, string? DisplayName, bool IsSuperAdmin, IReadOnlyList<MembershipView> Memberships);

/// <summary>La persona pertenece a la comunidad, pero su rol no permite la acción. La API la traduce a 403.</summary>
public sealed class AccessDeniedException(string message) : Exception(message);

/// <summary>La comunidad no existe o la persona no pertenece a ella. La API la traduce a 404.</summary>
public sealed class CommunityNotFoundException(string id) : Exception($"Comunidad no encontrada: {id}");

/// <summary>Casos de uso de acceso: perfil, comunidades, miembros y verificación de roles.</summary>
public sealed class AccessService(IAccessRepository repo, TimeProvider clock)
{
    public async Task<Profile> GetProfileAsync(Caller caller, CancellationToken ct = default)
    {
        var person = await ResolvePersonAsync(caller, ct);
        var memberships = person is null ? [] : await repo.GetMembershipsAsync(person.Id, ct);
        return new Profile(person?.Id, person?.Email ?? Person.NormalizeEmail(caller.Email), person?.DisplayName ?? caller.DisplayName, caller.IsSuperAdmin, memberships);
    }

    public async Task<Community> CreateCommunityAsync(Caller caller, string name, string adminEmail, CancellationToken ct = default)
    {
        if (!caller.IsSuperAdmin) throw new AccessDeniedException("Solo la superadministración de Habita puede crear comunidades.");
        var now = clock.GetUtcNow();
        var community = Community.Create(name, now);
        var admin = await FindOrInviteAsync(adminEmail, null, ct);
        await repo.AddCommunityAsync(community, ct);
        await repo.AddMembershipAsync(new Membership(community.Id, admin.Id, CommunityRole.Administrator, now), ct);
        return community;
    }

    public async Task<IReadOnlyList<Member>> ListMembersAsync(Caller caller, string communityId, CancellationToken ct = default)
    {
        await RequireManagerAsync(caller, communityId, ct);
        return await repo.GetMembersAsync(communityId, ct);
    }

    public async Task<Member> AddMemberAsync(Caller caller, string communityId, string email, CommunityRole role, string? displayName, CancellationToken ct = default)
    {
        await RequireManagerAsync(caller, communityId, ct);
        var person = await FindOrInviteAsync(email, displayName, ct);
        await repo.AddMembershipAsync(new Membership(communityId, person.Id, role, clock.GetUtcNow()), ct);
        return new Member(person, await repo.GetRolesAsync(person.Id, communityId, ct));
    }

    public async Task RemoveRoleAsync(Caller caller, string communityId, string personId, CommunityRole role, CancellationToken ct = default)
    {
        await RequireManagerAsync(caller, communityId, ct);
        var roles = await repo.GetRolesAsync(personId, communityId, ct);
        if (!roles.Contains(role)) throw new DomainException("La persona no tiene ese rol en la comunidad.");
        if (role == CommunityRole.Administrator && await repo.CountRoleAsync(communityId, role, ct) <= 1)
            throw new DomainException("La comunidad debe conservar al menos una persona con rol de administración.");
        await repo.RemoveMembershipAsync(communityId, personId, role, ct);
    }

    /// <summary>
    /// Exige uno de los roles indicados en la comunidad. Si la persona no pertenece a ella, responde como si
    /// la comunidad no existiera, para no revelar qué comunidades hay.
    /// </summary>
    public async Task<Community> RequireRoleAsync(Caller caller, string communityId, IReadOnlyCollection<CommunityRole> allowed, CancellationToken ct = default)
    {
        var community = await repo.GetCommunityAsync(communityId, ct) ?? throw new CommunityNotFoundException(communityId);
        var person = await ResolvePersonAsync(caller, ct);
        var roles = person is null ? [] : await repo.GetRolesAsync(person.Id, communityId, ct);
        if (roles.Count == 0) throw new CommunityNotFoundException(communityId);
        if (!roles.Any(allowed.Contains)) throw new AccessDeniedException("Tu rol en esta comunidad no permite esta acción.");
        return community;
    }

    /// <summary>
    /// Crea la comunidad de desarrollo con la persona indicada como administradora, si aún no pertenece a ninguna.
    /// Solo se usa con la autenticación de desarrollo.
    /// </summary>
    public async Task EnsureDevelopmentCommunityAsync(Caller developer, string communityName, CancellationToken ct = default)
    {
        var profile = await GetProfileAsync(developer, ct);
        if (profile.Memberships.Count > 0) return;
        await CreateCommunityAsync(developer with { IsSuperAdmin = true }, communityName, developer.Email ?? "", ct);
    }

    /// <summary>La administración de la comunidad o la superadministración gestionan los miembros.</summary>
    private async Task RequireManagerAsync(Caller caller, string communityId, CancellationToken ct)
    {
        if (!caller.IsSuperAdmin)
        {
            await RequireRoleAsync(caller, communityId, [CommunityRole.Administrator], ct);
            return;
        }
        if (await repo.GetCommunityAsync(communityId, ct) is null) throw new CommunityNotFoundException(communityId);
    }

    private async Task<Person> FindOrInviteAsync(string email, string? displayName, CancellationToken ct)
    {
        var normalized = Person.NormalizeEmail(email) ?? throw new DomainException("El correo no es válido.");
        var existing = await repo.FindPersonByEmailAsync(normalized, ct);
        if (existing is not null) return existing;
        var person = Person.Invite(normalized, displayName, clock.GetUtcNow());
        await repo.AddPersonAsync(person, ct);
        return person;
    }

    /// <summary>
    /// Busca a la persona por su cuenta externa. Si no está vinculada, la vincula con la invitación pendiente
    /// de su correo. Una invitación ya vinculada a otra cuenta no se reasigna.
    /// </summary>
    private async Task<Person?> ResolvePersonAsync(Caller caller, CancellationToken ct)
    {
        var person = await repo.FindPersonByExternalIdAsync(caller.ExternalId, ct);
        if (person is not null) return person;

        var email = Person.NormalizeEmail(caller.Email);
        if (email is null) return null;
        var invited = await repo.FindPersonByEmailAsync(email, ct);
        if (invited is null || invited.IsLinked) return null;

        await repo.TryLinkAsync(invited.Id, caller.ExternalId, Person.CleanName(caller.DisplayName), ct);
        return await repo.FindPersonByExternalIdAsync(caller.ExternalId, ct);
    }
}
