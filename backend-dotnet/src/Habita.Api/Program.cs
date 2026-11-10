using System.Text.Json;
using Habita.Api;
using Habita.Application;
using Habita.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});
// Los errores de formato (400) también pasan por ApiExceptionHandler, para que siempre lleven "detail".
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHabitaApplication(builder.Configuration).AddHabitaInfrastructure();

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment()) app.MapOpenApi(); // /openapi/v1.json
app.MapHabitaApi();

await app.Services.InitializeHabitaDatabaseAsync();
await app.RunAsync();

/// <summary>Punto de entrada; parcial para WebApplicationFactory en las pruebas.</summary>
public partial class Program;
