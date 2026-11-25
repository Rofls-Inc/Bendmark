using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Bendmark.Coordinator.Tests;

public sealed class EmulatorLabTests
{
    [Fact]
    public async Task Settings_change_only_between_emulator_runs()
    {
        var emulator = WebApplication.CreateSlimBuilder();
        emulator.WebHost.UseUrls("http://127.0.0.1:0");
        await using var control = emulator.Build();
        var mode = "healthy";
        control.MapGet("/config", () => Results.Ok(new { settings = new { mode }, capacity_rps = 12 }));
        control.MapPut("/config", (LabSettings settings) =>
        {
            mode = settings.Mode;
            return Results.Ok(new { settings, capacity_rps = 12 });
        });
        await control.StartAsync();

        await using var factory = new CoordinatorFactory();
        var origin = control.Urls.Single();
        factory.Settings["Emulator:Origin"] = origin;
        using var client = factory.CreateClient();

        var current = await client.GetFromJsonAsync<LabResponse>("/emulator/config");
        Assert.NotNull(current);
        Assert.Equal(origin + "/work", current.TargetUrl);
        Assert.Equal("healthy", current.Settings.Mode);

        var abstock = await Api.CreateRunAsync(client);
        Assert.Equal(abstock, await factory.Agent.WaitStartedAsync());
        using var otherTarget = await client.PutAsJsonAsync("/emulator/config", new LabSettings("reject"));
        Assert.Equal(HttpStatusCode.OK, otherTarget.StatusCode);

        using var created = await client.PostAsJsonAsync("/runs", new
        {
            name = "Эмулятор",
            target = new { url = current.TargetUrl, method = "GET" },
            steps = new[] { new { target_rps = 10, duration_seconds = 5 } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var run = (await Api.ReadJsonAsync(created)).GetProperty("id").GetGuid();
        using var queued = await client.PutAsJsonAsync("/emulator/config", new LabSettings("slow"));
        Assert.Equal(HttpStatusCode.Conflict, queued.StatusCode);

        factory.Agent.SendStep(TestData.Step(1));
        factory.Agent.Finish();
        Assert.Equal(run, await factory.Agent.WaitStartedAsync());
        using var active = await client.PutAsJsonAsync("/emulator/config", new LabSettings("slow"));
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        Assert.Equal("reject", mode);

        factory.Agent.SendStep(TestData.Step(1, targetRps: 10, requestCount: 50) with
        {
            DurationSeconds = 5,
            ThroughputRps = 10,
        });
        factory.Agent.Finish();
        await Api.WaitForStatusAsync(client, run, "completed");
        using var after = await client.PutAsJsonAsync("/emulator/config", new LabSettings("slow"));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal("slow", (await Api.ReadJsonAsync(after)).GetProperty("settings").GetProperty("mode").GetString());
    }

    private sealed record LabResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("target_url")] string TargetUrl,
        LabSettings Settings);

    private sealed record LabSettings(string Mode);
}
