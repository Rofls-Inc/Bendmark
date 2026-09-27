namespace Bendmark.Coordinator;
public record Target
{
    public string? Url { get; init; }
    public string? Method { get; init; }
}

public record Step
{
    public int TargetRps { get; init; }
    public int DurationSeconds { get; init; }
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

public class Run
{
    public required Guid Id { get; init; }
    public required Scenario Scenario { get; init; }
    public RunStatus Status { get; set; } = RunStatus.Created;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
