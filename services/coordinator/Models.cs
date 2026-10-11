namespace Bendmark.Coordinator;

public record Target
{
    public string? Url { get; init; }
    public string? Method { get; init; }
}

public record Step
{
    public double TargetRps { get; init; }
    public double DurationSeconds { get; init; }
}

public record Scenario
{
    public string? Name { get; init; }
    public Target? Target { get; init; }
    public List<Step>? Steps { get; init; }
}

public enum RunStatus
{
    Created,
    Running,
    Completed,
    Failed,
    Aborted,
}

public sealed record Run
{
    public required Guid Id { get; init; }
    public RunStatus Status { get; init; } = RunStatus.Created;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Error { get; init; }
    public required Scenario Scenario { get; init; }
    public IReadOnlyList<StepResultDto> Steps { get; init; } = [];
}
