using System.Text.Json;
using System.Text.Json.Serialization;
using Bendmark.Coordinator;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services.AddSingleton<RunStore>();

var app = builder.Build();

app.MapPost("/runs", async (HttpRequest request, RunStore store) =>
{
    var (scenario, error) = await ScenarioReader.ReadAsync(request);
    if (error is not null)
        return error;

    var errors = ScenarioValidator.Validate(scenario);
    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var run = store.Create(scenario!);
    return Results.Created($"/runs/{run.Id}", new { run.Id, run.Status });
});

app.MapGet("/runs/{id:guid}", (Guid id, RunStore store) =>
    store.Get(id) is { } run ? Results.Ok(run) : Results.NotFound());

app.Run();

// Entry point accessible to WebApplicationFactory in integration tests.
public partial class Program { }

