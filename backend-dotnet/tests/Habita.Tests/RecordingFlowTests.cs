using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Habita.Tests;

/// <summary>Flujo HTTP completo, equivalente a backend/tests/test_flow.py del prototipo.</summary>
public sealed class RecordingFlowTests
{
    private static async Task<string> StartAsync(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/recordings", new
        {
            meeting_id = 1, title = "Asamblea", community = "Los Olivos", consent_by = "Presidente",
            context = new
            {
                date = "2026-11-09",
                attendance = new { present = 10, total = 20, percent = 50 },
                polls = new[] { new { title = "Presupuesto", yes = 8, no = 1, abstain = 1 } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, string id, int seq, string query = "", string mime = "audio/webm")
    {
        var content = new ByteArrayContent("abc"u8.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
        return await c.PutAsync($"/api/recordings/{id}/chunks/{seq}{query}", content);
    }

    private static Task<HttpResponseMessage> FinishAsync(HttpClient c, string id, params object[] markers) =>
        c.PostAsJsonAsync($"/api/recordings/{id}/finish", new { markers });

    private static async Task<JsonObject> WaitDoneAsync(HttpClient c, string id)
    {
        JsonObject? j = null;
        for (var i = 0; i < 100; i++)
        {
            j = (await c.GetFromJsonAsync<JsonObject>($"/api/recordings/{id}"))!;
            if (j["status"]!.GetValue<string>() is "ready" or "failed") return j;
            await Task.Delay(50);
        }
        throw new TimeoutException(j?.ToJsonString());
    }

    [Fact]
    public async Task Consent_is_required()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var c = app.CreateClient();
        var r = await c.PostAsJsonAsync("/api/recordings", new { meeting_id = 1, title = "x", community = "y", consent_by = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Contains("consentimiento", (await r.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_flow_drafts_one_section_per_agenda_marker()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var c = app.CreateClient();
        var id = await StartAsync(c);
        for (var seq = 0; seq < 3; seq++)
            Assert.Equal(HttpStatusCode.NoContent, (await UploadAsync(c, id, seq, $"?start_ms={seq * 60000}&duration_ms=60000", "audio/webm;codecs=opus")).StatusCode);

        await FinishAsync(c, id,
            new { t_ms = 0, kind = "agenda", label = "Quórum" },
            new { t_ms = 70000, kind = "agenda", label = "Presupuesto" },
            new { t_ms = 100000, kind = "vote", label = "Votación abierta" });
        var j = await WaitDoneAsync(c, id);

        Assert.Equal("ready", j["status"]!.GetValue<string>());
        Assert.Equal("""{"chunks":3,"transcribed":3,"failed":0}""", j["progress"]!.ToJsonString());
        var draft = j["draft"]!.GetValue<string>();
        Assert.Contains("### 1. Quórum", draft, StringComparison.Ordinal);
        Assert.Contains("### 2. Presupuesto", draft, StringComparison.Ordinal);
        Assert.Contains("| Presupuesto | 8 | 1 | 1 |", draft, StringComparison.Ordinal);
        Assert.Contains("Asistieron 10 de 20 unidades privadas (50 %)", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("[EVENTO · votación]", draft, StringComparison.Ordinal); // el evento va al modelo, no al borrador
        Assert.Equal(3, j["markers"]!.AsArray().Count);
        Assert.Equal("00:01:00", j["transcript"]![1]!["ts"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chunks_go_to_the_section_where_their_midpoint_falls()
    {
        var ai = new FakeAi();
        using var app = new HabitaApiFactory(ai);
        var c = app.CreateClient();
        var id = await StartAsync(c);
        for (var seq = 0; seq < 3; seq++) await UploadAsync(c, id, seq, $"?start_ms={seq * 60000}&duration_ms=60000");
        await FinishAsync(c, id, new { t_ms = 0, kind = "agenda", label = "A" }, new { t_ms = 90000, kind = "agenda", label = "B" });
        await WaitDoneAsync(c, id);

        var a = ai.DraftPrompts.Single(u => u.Contains("Punto del orden del día: A", StringComparison.Ordinal));
        var b = ai.DraftPrompts.Single(u => u.Contains("Punto del orden del día: B", StringComparison.Ordinal));
        Assert.Contains("-0.webm", a, StringComparison.Ordinal);
        Assert.DoesNotContain("-1.webm", a, StringComparison.Ordinal); // punto medio del fragmento 0 = 30 s
        Assert.Contains("-1.webm", b, StringComparison.Ordinal);       // punto medio del fragmento 1 = 90 s: ya es B
        Assert.Contains("-2.webm", b, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_failure_still_produces_a_draft()
    {
        using var app = new HabitaApiFactory(new FakeAi("-1."));
        var c = app.CreateClient();
        var id = await StartAsync(c);
        for (var seq = 0; seq < 2; seq++) await UploadAsync(c, id, seq, $"?start_ms={seq * 60000}");
        await FinishAsync(c, id);
        var j = await WaitDoneAsync(c, id);

        Assert.Equal("ready", j["status"]!.GetValue<string>());
        Assert.Equal(1, j["progress"]!["failed"]!.GetValue<int>());
        Assert.Contains(j["transcript"]!.AsArray(), l => l!["text"]!.GetValue<string>() == "[AUDIO NO TRANSCRITO]");
    }

    [Fact]
    public async Task All_chunks_failing_marks_the_recording_failed()
    {
        using var app = new HabitaApiFactory(new FakeAi("-"));
        var c = app.CreateClient();
        var id = await StartAsync(c);
        await UploadAsync(c, id, 0);
        await FinishAsync(c, id);
        var j = await WaitDoneAsync(c, id);

        Assert.Equal("failed", j["status"]!.GetValue<string>());
        Assert.Contains("boom", j["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_can_be_edited_and_audio_deleted()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var c = app.CreateClient();
        var id = await StartAsync(c);
        await UploadAsync(c, id, 0);
        await FinishAsync(c, id);
        await WaitDoneAsync(c, id);

        var edited = await (await c.PutAsJsonAsync($"/api/recordings/{id}/draft", new { draft = "editado" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("editado", edited!["draft"]!.GetValue<string>());

        var deleted = await (await c.DeleteAsync($"/api/recordings/{id}/audio")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(deleted!["audio_deleted"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(c, id, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/recordings/nope")).StatusCode);
    }

    [Fact]
    public async Task Health_reports_the_ai_provider_and_bad_markers_are_rejected()
    {
        using var app = new HabitaApiFactory(new FakeAi());
        var c = app.CreateClient();
        var health = await c.GetFromJsonAsync<JsonObject>("/api/health");
        Assert.Equal("""{"ok":true,"ai_provider":"fake"}""", health!.ToJsonString());

        var id = await StartAsync(c);
        var r = await c.PostAsJsonAsync($"/api/recordings/{id}/finish", new { markers = new[] { new { t_ms = 0, kind = "otro", label = "x" } } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);

        var missing = await c.PostAsync("/api/recordings", JsonContent.Create(new { meeting_id = 1 }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(JsonValueKind.String, (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").ValueKind);
    }
}
