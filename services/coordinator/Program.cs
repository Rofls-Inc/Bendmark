using System.Text.Json;
using System.Text.Json.Serialization;
using Bendmark.Coordinator;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});

builder.Services.AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.Address, UriKind.Absolute, out _),
        "Agent:Address должен быть абсолютным URL, например http://agent:50051")
    .Validate(o => double.IsFinite(o.RequestTimeoutSeconds) && o.RequestTimeoutSeconds > 0,
        "Agent:RequestTimeoutSeconds должен быть конечным числом больше нуля")
    .ValidateOnStart();

builder.Services.AddSingleton<RunStore>();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<RunQueue>();
builder.Services.AddSingleton<RunExecutor>();
builder.Services.AddHostedService<RunWorker>();

builder.Services.AddKeyedSingleton<GrpcChannel>(AgentOptions.SectionName, (services, _) =>
    GrpcChannel.ForAddress(services.GetRequiredService<IOptions<AgentOptions>>().Value.Address));
builder.Services.AddSingleton<IAgentRunner>(services => new GrpcAgentRunner(
    services.GetRequiredKeyedService<GrpcChannel>(AgentOptions.SectionName),
    services.GetRequiredService<IOptions<AgentOptions>>()));

var app = builder.Build();

// Проверка живости для healthcheck в docker-compose.yml
app.MapHealthChecks("/health");

app.MapPost("/runs", async (HttpRequest request, RunStore store, RunQueue queue) =>
{
    var (scenario, error) = await ScenarioReader.ReadAsync(request);
    if (error is not null)
        return error;

    var errors = ScenarioValidator.Validate(scenario);
    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var run = store.Create(scenario!);
    queue.Enqueue(run.Id);
    return Results.Created($"/runs/{run.Id}", new { run.Id, run.Status });
});

app.MapGet("/runs/{id:guid}", (Guid id, RunStore store) =>
    store.Get(id) is { } run ? Results.Ok(run) : Results.NotFound());

// result.json для запуска анализатора из командной строки
app.MapGet("/runs/{id:guid}/result", (Guid id, RunStore store) => store.Get(id) switch
{
    null => Results.NotFound(),
    { } run when RunResultDto.CanExport(run) => Results.Ok(RunResultDto.From(run)),
    { } run => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        detail: $"Результат есть только у прогона в статусе completed или aborted, сейчас {StatusName(run.Status)}"),
});

app.MapPost("/runs/{id:guid}/stop", async (Guid id, RunExecutor executor, CancellationToken cancellationToken) =>
{
    var (outcome, run) = await executor.StopAsync(id, cancellationToken);
    return outcome switch
    {
        StopOutcome.NotFound => Results.NotFound(),
        StopOutcome.AlreadyFinished => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            detail: $"Прогон уже завершён со статусом {StatusName(run!.Status)}, остановка не нужна"),
        StopOutcome.Pending => Results.Accepted($"/runs/{id}", run),
        _ => Results.Ok(run),
    };
});

app.Run();

static string StatusName(RunStatus status) => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
