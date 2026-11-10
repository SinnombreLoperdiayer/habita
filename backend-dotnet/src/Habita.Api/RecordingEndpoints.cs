using Habita.Application;
using Habita.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Habita.Api;

/// <summary>Adaptador de entrada HTTP: rutas /api/*.</summary>
public static class RecordingEndpoints
{
    public static IEndpointRouteBuilder MapHabitaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (RecordingService svc) => new HealthResponse(true, svc.ProviderName));

        var rec = api.MapGroup("/recordings").WithTags("Grabaciones");

        rec.MapPost("", async (StartRecordingRequest body, RecordingService svc, CancellationToken ct) =>
        {
            var r = await svc.StartAsync(body.MeetingId, body.Title, body.Community, body.ConsentBy, body.Context, ct);
            return Results.Created($"/api/recordings/{r.Id}", RecordingResponse.From(r, []));
        });

        rec.MapPut("/{id}/chunks/{seq:int}", async (
            string id, int seq, HttpRequest request, RecordingService svc, CancellationToken ct,
            [FromQuery(Name = "start_ms")] long startMs = 0,
            [FromQuery(Name = "duration_ms")] long durationMs = 60000) =>
        {
            if (request.ContentLength > svc.MaxChunkBytes) throw new DomainException("Fragmento demasiado grande.");
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, ct);
            await svc.UploadChunkAsync(id, seq, startMs, durationMs, request.ContentType ?? "audio/webm", body.GetBuffer().AsMemory(0, (int)body.Length), ct);
            return Results.NoContent();
        });

        rec.MapPost("/{id}/finish", async (string id, FinishRecordingRequest body, RecordingService svc, CancellationToken ct) =>
        {
            var markers = (body.Markers ?? []).Select(RecordingResponse.ToMarker).ToList();
            var r = await svc.FinishAsync(id, markers, body.Context, ct);
            return RecordingResponse.From(r, await svc.ChunksAsync(id, ct));
        });

        rec.MapGet("/{id}", async (string id, RecordingService svc, CancellationToken ct) =>
        {
            var (r, chunks) = await svc.ViewAsync(id, ct);
            return RecordingResponse.From(r, chunks);
        });

        rec.MapPost("/{id}/regenerate", async (string id, RecordingService svc, CancellationToken ct) =>
            RecordingResponse.From(await svc.RegenerateAsync(id, ct), await svc.ChunksAsync(id, ct)));

        rec.MapPut("/{id}/draft", async (string id, DraftRequest body, RecordingService svc, CancellationToken ct) =>
            RecordingResponse.From(await svc.SaveDraftAsync(id, body.Draft, ct), await svc.ChunksAsync(id, ct)));

        rec.MapDelete("/{id}/audio", async (string id, RecordingService svc, CancellationToken ct) =>
            RecordingResponse.From(await svc.DeleteAudioAsync(id, ct), await svc.ChunksAsync(id, ct)));

        return app;
    }
}

/// <summary>Traduce errores de negocio a ProblemDetails; el frontend muestra el campo "detail".</summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        (int Status, string Detail)? mapped = exception switch
        {
            DomainException e => (StatusCodes.Status422UnprocessableEntity, e.Message),
            RecordingNotFoundException => (StatusCodes.Status404NotFound, "Grabación no encontrada"),
            BadHttpRequestException e => (e.StatusCode, "Solicitud inválida: revisa el formato del cuerpo y de los parámetros."),
            _ => null,
        };
        if (mapped is not { } m) return false;

        httpContext.Response.StatusCode = m.Status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = m.Status, Detail = m.Detail },
        });
    }
}
