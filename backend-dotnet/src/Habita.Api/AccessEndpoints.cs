using System.Security.Claims;
using Habita.Application;

namespace Habita.Api;

/// <summary>Adaptador de entrada HTTP: perfil, comunidades y miembros.</summary>
public static class AccessEndpoints
{
    public static IEndpointRouteBuilder MapAccessApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Acceso");

        api.MapGet("/me", async (ClaimsPrincipal user, AccessService access, CancellationToken ct) =>
            MeResponse.From(await access.GetProfileAsync(user.ToCaller(), ct)));

        var communities = api.MapGroup("/communities");

        communities.MapPost("", async (CreateCommunityRequest body, ClaimsPrincipal user, AccessService access, CancellationToken ct) =>
        {
            var c = await access.CreateCommunityAsync(user.ToCaller(), body.Name, body.AdminEmail, ct);
            return Results.Created($"/api/communities/{c.Id}", new CommunityResponse(c.Id, c.Name));
        });

        communities.MapGet("/{id}/members", async (string id, ClaimsPrincipal user, AccessService access, CancellationToken ct) =>
            (await access.ListMembersAsync(user.ToCaller(), id, ct)).Select(MemberResponse.From).ToList());

        communities.MapPost("/{id}/members", async (string id, AddMemberRequest body, ClaimsPrincipal user, AccessService access, CancellationToken ct) =>
            MemberResponse.From(await access.AddMemberAsync(user.ToCaller(), id, body.Email, RoleNames.Parse(body.Role), body.DisplayName, ct)));

        communities.MapDelete("/{id}/members/{personId}/roles/{role}", async (string id, string personId, string role, ClaimsPrincipal user, AccessService access, CancellationToken ct) =>
        {
            await access.RemoveRoleAsync(user.ToCaller(), id, personId, RoleNames.Parse(role), ct);
            return Results.NoContent();
        });

        return app;
    }
}
