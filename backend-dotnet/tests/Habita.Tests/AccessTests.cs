using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Habita.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Habita.Tests;

/// <summary>Autenticación con Entra External ID, comunidades, miembros y roles.</summary>
public sealed class AccessTests
{
    private static readonly TestUser Owner = new("oid-owner", "duena302@olivos.test", "Propietaria 302");
    private static readonly TestUser Stranger = new("oid-stranger", "otra@persona.test", "Persona ajena");

    private static async Task<JsonObject> MeAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonObject>("/api/me"))!;

    private static async Task<string> DetailAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>();

    private static Task<HttpResponseMessage> StartRawAsync(HttpClient c, string communityId) =>
        c.PostAsJsonAsync("/api/recordings", new { community_id = communityId, meeting_id = 1, title = "Asamblea", consent_by = "Presidente" });

    private static async Task<string> StartRecordingAsync(HttpClient c, string communityId)
    {
        var r = await StartRawAsync(c, communityId);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
    }

    private static Task<HttpResponseMessage> AddMemberAsync(HttpClient c, string communityId, object body) =>
        c.PostAsJsonAsync($"/api/communities/{communityId}/members", body);

    [Fact]
    public async Task Requests_without_a_valid_token_are_rejected()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/recordings/abc")).StatusCode);

        string[] invalid =
        [
            HabitaApiFactory.Token(TestUser.Admin, audience: "otra-api"),
            HabitaApiFactory.Token(TestUser.Admin, issuer: "https://otro-tenant.ciamlogin.com/otro/v2.0"),
            HabitaApiFactory.Token(TestUser.Admin, key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))),
        ];
        foreach (var token in invalid)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task Audience_with_the_api_prefix_is_accepted()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", HabitaApiFactory.Token(TestUser.Admin, audience: "api://" + HabitaApiFactory.Audience));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Only_the_super_admin_creates_communities()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        await app.CreateCommunityAsync();

        var r = await app.ClientFor(TestUser.Admin).PostAsJsonAsync("/api/communities", new { name = "Otra", admin_email = "otra@admin.test" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("superadministración", await DetailAsync(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invitation_is_linked_on_first_sign_in_and_not_reassigned()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var community = await app.CreateCommunityAsync();

        var me = await MeAsync(app.ClientFor(TestUser.Admin));
        Assert.NotNull(me["person_id"]);
        Assert.Equal("Administración Los Olivos", me["display_name"]!.GetValue<string>()); // el nombre del token reemplaza al correo
        var membership = me["memberships"]!.AsArray().Single()!;
        Assert.Equal(community, membership["community_id"]!.GetValue<string>());
        Assert.Equal("Los Olivos", membership["community_name"]!.GetValue<string>());
        Assert.Equal("""["administrator"]""", membership["roles"]!.ToJsonString());

        // La misma cuenta, aunque cambie de correo, sigue vinculada por su oid.
        var renamed = await MeAsync(app.ClientFor(TestUser.Admin with { Email = "nuevo@correo.test" }));
        Assert.Single(renamed["memberships"]!.AsArray());

        // Otra cuenta con el correo de una invitación ya vinculada no hereda sus roles.
        var other = await MeAsync(app.ClientFor(new TestUser("oid-otra-cuenta", TestUser.Admin.Email)));
        Assert.Null(other["person_id"]);
        Assert.Empty(other["memberships"]!.AsArray());
    }

    [Fact]
    public async Task People_outside_the_community_do_not_see_it()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var community = await app.CreateCommunityAsync();
        var recording = await StartRecordingAsync(app.ClientFor(TestUser.Admin), community);

        var stranger = app.ClientFor(Stranger);
        Assert.Empty((await MeAsync(stranger))["memberships"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/communities/{community}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/recordings/{recording}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await StartRawAsync(stranger, community)).StatusCode);

        // La superadministración gestiona miembros, pero no ve grabaciones.
        var super = app.ClientFor(TestUser.Super);
        Assert.Equal(HttpStatusCode.OK, (await super.GetAsync($"/api/communities/{community}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await super.GetAsync($"/api/recordings/{recording}")).StatusCode);
    }

    [Fact]
    public async Task Roles_without_permission_get_403()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var community = await app.CreateCommunityAsync();
        var admin = app.ClientFor(TestUser.Admin);
        var recording = await StartRecordingAsync(admin, community);
        Assert.Equal(HttpStatusCode.OK, (await AddMemberAsync(admin, community, new { email = Owner.Email, role = "owner" })).StatusCode);

        var owner = app.ClientFor(Owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/recordings/{recording}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"/api/recordings/{recording}/audio")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartRawAsync(owner, community)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/communities/{community}/members")).StatusCode);
    }

    [Fact]
    public async Task Members_are_managed_but_the_last_administrator_stays()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var community = await app.CreateCommunityAsync();
        var admin = app.ClientFor(TestUser.Admin);
        var adminId = (await MeAsync(admin))["person_id"]!.GetValue<string>();

        var council = await (await AddMemberAsync(admin, community, new { email = " Consejo@Olivos.test ", role = "council", display_name = "Consejo" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("consejo@olivos.test", council!["email"]!.GetValue<string>());
        Assert.False(council["linked"]!.GetValue<bool>());
        var councilId = council["person_id"]!.GetValue<string>();

        var twoRoles = await (await AddMemberAsync(admin, community, new { email = "consejo@olivos.test", role = "owner" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("""["council","owner"]""", twoRoles!["roles"]!.ToJsonString());
        Assert.Equal(2, (await admin.GetFromJsonAsync<JsonArray>($"/api/communities/{community}/members"))!.Count);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/communities/{community}/members/{councilId}/roles/council")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.DeleteAsync($"/api/communities/{community}/members/{councilId}/roles/council")).StatusCode);

        var last = await admin.DeleteAsync($"/api/communities/{community}/members/{adminId}/roles/administrator");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, last.StatusCode);
        Assert.Contains("al menos una persona", await DetailAsync(last), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddMemberAsync(admin, community, new { email = "a@b.co", role = "rey" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await AddMemberAsync(admin, community, new { email = "no-es-correo", role = "owner" })).StatusCode);
    }

    [Fact]
    public async Task Development_mode_signs_in_the_configured_user_with_a_seeded_community()
    {
        using var app = new HabitaApiFactory(new FakeAi(), development: true);
        var c = app.CreateClient(); // sin token

        var me = await MeAsync(c);
        Assert.True(me["is_super_admin"]!.GetValue<bool>());
        var membership = me["memberships"]!.AsArray().Single()!;
        Assert.Equal("Conjunto Los Olivos", membership["community_name"]!.GetValue<string>());

        var recording = await StartRecordingAsync(c, membership["community_id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/recordings/{recording}")).StatusCode);
    }

    [Theory]
    [InlineData("Development", "Production")]
    [InlineData("EntraExternalId", "Production")]
    [InlineData("Otro", "Development")]
    public void Invalid_authentication_configuration_stops_the_api(string mode, string environment)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:Mode"] = mode }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHabitaAuthentication(config, new TestEnvironment(environment)));
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Habita.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
