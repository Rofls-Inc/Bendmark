using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bendmark.Coordinator.Tests;

public sealed class RunStoreTests : IDisposable
{
    private readonly string _path = TestData.NewDatabasePath();

    public void Dispose() => TestData.DeleteDatabase(_path);

    private RunStore Open() => new(Options.Create(new StorageOptions { Path = _path }));

    private static Scenario Scenario(string name = "ramp") => new()
    {
        Name = name,
        Target = new Target { Url = "http://abstock:8080/", Method = "GET" },
        Steps = [new Step { TargetRps = 100, DurationSeconds = 45 }, new Step { TargetRps = 150, DurationSeconds = 45 }],
    };

    private static Analysis Found() => new()
    {
        Status = AnalysisStatus.Found,
        LimitRps = 100,
        FailedStepIndex = 2,
        Reasons = ["p99 выше порога"],
        AnalyzedSteps = 2,
        Message = "Предел: 100 запр/с",
    };

    [Fact]
    public void Run_is_read_back_after_store_is_recreated()
    {
        var store = Open();
        var id = store.Create(Scenario()).Id;
        store.TryStart(id);
        store.AddStep(id, TestData.Step(1, 100));
        store.AddStep(id, TestData.Step(2, 150, errorCount: 27));
        store.TryFinish(id, RunStatus.Completed, null);
        store.SetAnalysis(id, Found());
        var before = store.Get(id)!;

        var run = Open().Get(id)!;

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(before.CreatedAt, run.CreatedAt);
        Assert.Equal(before.StartedAt, run.StartedAt);
        Assert.Equal(before.FinishedAt, run.FinishedAt);
        Assert.Equal("ramp", run.Scenario.Name);
        Assert.Equal("http://abstock:8080/", run.Scenario.Target!.Url);
        Assert.Equal([100.0, 150.0], run.Scenario.Steps!.Select(s => s.TargetRps));
        Assert.Equal(before.Steps, run.Steps);
        Assert.Equal(27UL, run.Steps[1].Errors.Count);
        Assert.Equal(AnalysisStatus.Found, run.Analysis!.Status);
        Assert.Equal(100, run.Analysis.LimitRps);
        Assert.Equal(2u, run.Analysis.FailedStepIndex);
        Assert.Equal(["p99 выше порога"], run.Analysis.Reasons);
    }

    [Fact]
    public void Steps_are_saved_as_they_arrive()
    {
        var store = Open();
        var id = store.Create(Scenario()).Id;
        store.TryStart(id);
        store.AddStep(id, TestData.Step(1));

        Assert.Single(Open().Get(id)!.Steps);
    }

    [Fact]
    public void Running_run_becomes_aborted_after_restart_and_keeps_steps()
    {
        var store = Open();
        var id = store.Create(Scenario()).Id;
        store.TryStart(id);
        store.AddStep(id, TestData.Step(1));

        var run = Open().Get(id)!;

        Assert.Equal(RunStatus.Aborted, run.Status);
        Assert.Equal(RunStore.RestartedWhileRunning, run.Error);
        Assert.NotNull(run.FinishedAt);
        Assert.Single(run.Steps);
        // Веб опрашивает прогон, пока анализ pending, поэтому анализ сразу закрыт ошибкой
        Assert.Equal(AnalysisStatus.Error, run.Analysis!.Status);
    }

    [Fact]
    public void Queued_run_becomes_aborted_after_restart()
    {
        var id = Open().Create(Scenario()).Id;

        var run = Open().Get(id)!;

        Assert.Equal(RunStatus.Aborted, run.Status);
        Assert.Equal(RunStore.RestartedInQueue, run.Error);
        Assert.Null(run.StartedAt);
        Assert.Null(run.Analysis);
    }

    [Fact]
    public void Pending_analysis_becomes_error_after_restart()
    {
        var store = Open();
        var id = store.Create(Scenario()).Id;
        store.TryStart(id);
        store.AddStep(id, TestData.Step(1));
        store.TryFinish(id, RunStatus.Completed, null,
            AnalysisPlan.For(RunStatus.Completed, store.Get(id)!.Steps)!.Initial());

        var run = Open().Get(id)!;

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Null(run.Error);
        Assert.Equal(AnalysisStatus.Error, run.Analysis!.Status);
        Assert.Contains("перезапущен", run.Analysis.Message);
    }

    [Fact]
    public void Finished_runs_are_not_touched_by_restart()
    {
        var store = Open();
        var id = store.Create(Scenario()).Id;
        store.TryStart(id);
        store.TryFinish(id, RunStatus.Failed, "Агент недоступен");
        var before = store.Get(id)!;

        var run = Open().Get(id)!;

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("Агент недоступен", run.Error);
        Assert.Equal(before.FinishedAt, run.FinishedAt);
        Assert.Null(run.Analysis);
    }

    [Fact]
    public void List_goes_from_newest_to_oldest_page_by_page()
    {
        var store = Open();
        var ids = Enumerable.Range(1, 5).Select(i => store.Create(Scenario($"run-{i}")).Id).ToList();

        var first = store.List(2, null)!;
        var second = store.List(2, first.NextBefore)!;
        var last = store.List(2, second.NextBefore)!;

        Assert.Equal([ids[4], ids[3]], first.Runs.Select(r => r.Id));
        Assert.Equal([ids[2], ids[1]], second.Runs.Select(r => r.Id));
        Assert.Equal([ids[0]], last.Runs.Select(r => r.Id));
        Assert.Null(last.NextBefore);
        Assert.Equal("run-5", first.Runs[0].ScenarioName);
    }

    [Fact]
    public void List_page_that_ends_exactly_has_no_next()
    {
        var store = Open();
        store.Create(Scenario());
        store.Create(Scenario());

        Assert.Null(store.List(2, null)!.NextBefore);
    }

    [Fact]
    public void List_with_unknown_before_returns_null()
    {
        Assert.Null(Open().List(10, Guid.NewGuid()));
    }

    [Fact]
    public void List_shows_status_and_limit()
    {
        var store = Open();
        var id = store.Create(Scenario()).Id;
        store.TryStart(id);
        store.TryFinish(id, RunStatus.Completed, null);
        store.SetAnalysis(id, Found());

        var summary = Assert.Single(store.List(10, null)!.Runs);

        Assert.Equal(RunStatus.Completed, summary.Status);
        Assert.NotNull(summary.StartedAt);
        Assert.NotNull(summary.FinishedAt);
        Assert.Equal(AnalysisStatus.Found, summary.AnalysisStatus);
        Assert.Equal(100, summary.LimitRps);
    }
}

public class RunListApiTests
{
    [Fact]
    public async Task Get_runs_lists_runs_without_steps()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client);
        await factory.Agent.WaitStartedAsync();
        factory.Agent.SendStep(TestData.Step(1));
        await Api.WaitForAsync(client, id, run => Api.StepCount(run) == 1);

        using var response = await client.GetAsync("/runs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await Api.ReadJsonAsync(response);

        var run = Assert.Single(page.GetProperty("runs").EnumerateArray());
        Assert.Equal(id, run.GetProperty("id").GetGuid());
        Assert.Equal("abstock-home-ramp", run.GetProperty("scenario_name").GetString());
        Assert.Equal("running", Api.Status(run));
        Assert.Equal(JsonValueKind.String, run.GetProperty("created_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("limit_rps").ValueKind);
        Assert.False(run.TryGetProperty("steps", out _));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("next_before").ValueKind);
    }

    [Fact]
    public async Task Get_runs_pages_with_limit_and_before()
    {
        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add(await Api.CreateRunAsync(client));

        var first = await GetPageAsync(client, "/runs?limit=2");
        var next = first.GetProperty("next_before").GetGuid();
        var second = await GetPageAsync(client, $"/runs?limit=2&before={next}");

        Assert.Equal([ids[2], ids[1]], Ids(first));
        Assert.Equal([ids[0]], Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next_before").ValueKind);
    }

    [Theory]
    [InlineData("/runs?limit=0", "limit")]
    [InlineData("/runs?limit=101", "limit")]
    [InlineData("/runs?before=00000000-0000-0000-0000-000000000000", "before")]
    public async Task Get_runs_rejects_bad_query(string url, string field)
    {
        await using var factory = new CoordinatorFactory();
        using var response = await factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Api.ReadJsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Run_survives_coordinator_restart()
    {
        var path = TestData.NewDatabasePath();
        try
        {
            Guid finished, running;
            await using (var factory = CoordinatorFactory.Reusing(path))
            {
                var client = factory.CreateClient();
                finished = await Api.CreateRunAsync(client);
                await factory.Agent.WaitStartedAsync();
                factory.Agent.SendStep(TestData.Step(1, 100));
                factory.Agent.SendStep(TestData.Step(2, 150));
                factory.Agent.Finish();
                // ramp.json: 7 ступеней, поэтому прогон из двух ступеней - failed, но со ступенями
                await Api.WaitForStatusAsync(client, finished, "failed");

                running = await Api.CreateRunAsync(client);
                await factory.Agent.WaitStartedAsync();
                factory.Agent.SendStep(TestData.Step(1));
                await Api.WaitForAsync(client, running, run => Api.StepCount(run) == 1);
            }

            await using (var factory = CoordinatorFactory.Reusing(path))
            {
                var client = factory.CreateClient();

                var old = await Api.GetRunAsync(client, finished);
                Assert.Equal("failed", Api.Status(old));
                Assert.Equal(2, Api.StepCount(old));

                // Штатная остановка хоста сама завершает идущий прогон; после падения это делает RunStore
                var interrupted = await Api.GetRunAsync(client, running);
                Assert.Equal("aborted", Api.Status(interrupted));
                Assert.Equal(JsonValueKind.String, interrupted.GetProperty("error").ValueKind);
                Assert.Equal(1, Api.StepCount(interrupted));
                Assert.NotEqual("pending", interrupted.GetProperty("analysis").GetProperty("status").GetString());

                Assert.Equal([running, finished], Ids(await GetPageAsync(client, "/runs")));
            }
        }
        finally
        {
            TestData.DeleteDatabase(path);
        }
    }

    private static async Task<JsonElement> GetPageAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Api.ReadJsonAsync(response);
    }

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("runs").EnumerateArray().Select(run => run.GetProperty("id").GetGuid()).ToList();
}
