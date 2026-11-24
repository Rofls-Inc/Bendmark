using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bendmark.Coordinator.Tests;

public sealed class RunMlReportTests
{
    [Theory]
    [InlineData("http://emulator:8080/work", true, false, true)]
    [InlineData("http://emulator:8080/work", false, false, false)]
    [InlineData("http://emulator:8080/work", true, true, false)]
    [InlineData("http://abstock:8080/", true, false, false)]
    public async Task Report_classifies_only_emulator_runs_with_complete_telemetry(
        string target, bool telemetryEnabled, bool restarted, bool expected)
    {
        await using var factory = new CoordinatorFactory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<RunStore>();
        var run = store.Create(new Scenario
        {
            Name = "ml-saturation",
            Target = new Target { Url = target, Method = "GET" },
            Steps = [new Step { TargetRps = 20, DurationSeconds = 3 }],
        });
        Assert.NotNull(store.TryStart(run.Id, telemetryEnabled));
        var step = TestData.Step(1, targetRps: 20, requestCount: 60) with
        {
            DurationSeconds = 3,
            ThroughputRps = 20,
            LatencyMs = new LatencyDto { P50 = 800, P90 = 1400, P99 = 1667.769 },
        };
        Assert.True(store.AddStep(run.Id, step));
        var started = DateTime.UtcNow;
        var first = new TelemetrySnapshot(1, started, started, 0, 2, 0, 128, 100, 5);
        var last = first with
        {
            CapturedAt = started.AddSeconds(2),
            StartedAt = restarted ? started.AddSeconds(1) : started,
            WorkersBusy = 2,
            QueueLength = 20,
            CompletedTotal = 130,
        };
        store.AddTelemetryPoll(run.Id, first.CapturedAt, first);
        store.AddTelemetryPoll(run.Id, last.CapturedAt, last);

        var running = await Api.GetRunAsync(client, run.Id);
        Assert.False(running.TryGetProperty("ml_diagnosis", out _));
        Assert.True(store.TryFinish(run.Id, RunStatus.Completed, error: null));

        var report = await Api.GetRunAsync(client, run.Id);
        Assert.Equal(expected, report.TryGetProperty("ml_diagnosis", out var diagnosis));
        if (expected)
        {
            Assert.Equal("saturation", diagnosis.GetProperty("label").GetString());
            var measurements = diagnosis.GetProperty("measurements");
            Assert.Equal(1667.769, measurements.GetProperty("peak_p99_ms").GetDouble());
            Assert.Equal(0, measurements.GetProperty("error_fraction").GetDouble());
            Assert.Equal(20, measurements.GetProperty("peak_queue_length").GetInt64());
            Assert.Equal(0, measurements.GetProperty("rejected_fraction").GetDouble());
        }

        using var response = await client.GetAsync($"/runs/{run.Id}/result");
        response.EnsureSuccessStatusCode();
        var export = await Api.ReadJsonAsync(response);
        Assert.False(export.TryGetProperty("ml_diagnosis", out _));
    }
}
