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

builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHabitaApplication(builder.Configuration).AddHabitaInfrastructure();

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Habita API v1"));
}
app.MapHabitaApi();

await app.Services.InitializeHabitaDatabaseAsync();
await app.RunAsync();

/// <summary>Punto de entrada; parcial para WebApplicationFactory en las pruebas.</summary>
public partial class Program
{
    protected Program() { }
}
