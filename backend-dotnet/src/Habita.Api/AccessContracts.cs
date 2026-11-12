using Habita.Application;
using Habita.Domain;

namespace Habita.Api;

// Contrato HTTP de acceso: perfil, comunidades y miembros (JSON en snake_case).

public sealed record MembershipResponse(string CommunityId, string CommunityName, IReadOnlyList<string> Roles);

public sealed record MeResponse(string? PersonId, string? Email, string? DisplayName, bool IsSuperAdmin, IReadOnlyList<MembershipResponse> Memberships)
{
    public static MeResponse From(Profile p) => new(
        p.PersonId,
        p.Email,
        p.DisplayName,
        p.IsSuperAdmin,
        [.. p.Memberships.Select(m => new MembershipResponse(m.CommunityId, m.CommunityName, [.. m.Roles.Select(RoleNames.Of)]))]);
}

public sealed record CreateCommunityRequest(string Name, string AdminEmail);

public sealed record CommunityResponse(string Id, string Name);

public sealed record AddMemberRequest(string Email, string Role, string? DisplayName = null);

public sealed record MemberResponse(string PersonId, string? Email, string DisplayName, bool Linked, IReadOnlyList<string> Roles)
{
    public static MemberResponse From(Member m) =>
        new(m.Person.Id, m.Person.Email, m.Person.DisplayName, m.Person.IsLinked, [.. m.Roles.Select(RoleNames.Of)]);
}

/// <summary>Nombres de los roles en el contrato HTTP.</summary>
public static class RoleNames
{
    private static readonly Dictionary<CommunityRole, string> Names = new()
    {
        [CommunityRole.Administrator] = "administrator",
        [CommunityRole.RegistrationStaff] = "registration_staff",
        [CommunityRole.Council] = "council",
        [CommunityRole.FiscalAuditor] = "fiscal_auditor",
        [CommunityRole.Owner] = "owner",
    };

    public static string Of(CommunityRole role) => Names[role];

    public static CommunityRole Parse(string name)
    {
        foreach (var (role, value) in Names)
            if (value == name) return role;
        throw new DomainException($"role debe ser uno de: {string.Join(", ", Names.Values)}.");
    }
}
