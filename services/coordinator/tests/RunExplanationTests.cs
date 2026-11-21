using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bendmark.Coordinator.Tests;

public sealed class RunExplanationTests
{
    [Fact]
    public async Task Generated_facts_resolve_to_saved_measurements_and_the_request_excludes_target_secrets()
    {
        using var provider = new OpenAiStub(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());

            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("gpt-4.1-mini", body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.True(body.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
            var input = body.GetProperty("input").GetString()!;
            Assert.DoesNotContain("private", input);
            using var document = JsonDocument.Parse(input);
            var data = document.RootElement;
            Assert.Equal("POST", data.GetProperty("method").GetString());
            Assert.Equal(3, data.GetProperty("agent_count").GetInt32());
            Assert.Equal(7, data.GetProperty("planned_steps").GetInt32());
            Assert.Equal("completed", data.GetProperty("status").GetString());
            Assert.Equal(812, data.GetProperty("steps")[6].GetProperty("latency_ms").GetProperty("p99").GetDouble());
            Assert.Equal(0UL, data.GetProperty("steps")[6].GetProperty("skipped_count").GetUInt64());
            var analysis = data.GetProperty("analysis");
            Assert.Equal("found", analysis.GetProperty("status").GetString());
            Assert.False(analysis.GetProperty("limit_confirmed").GetBoolean());
            Assert.Equal("slow_responses", analysis.GetProperty("diagnosis").GetProperty("cause").GetString());
            return ModelAnswer("""{"step_index":7,"metric":"p99_ms"},{"step_index":7,"metric":"error_rate_percent"}""");
        });
        await using var factory = NewFactory();
        await using var host = WithProvider(factory, provider);
        using var client = host.CreateClient();
        var id = SaveReport(host.Services.GetRequiredService<RunStore>());

        Assert.Equal("found", (await Api.GetRunAsync(client, id)).GetProperty("analysis").GetProperty("status").GetString());
        Assert.Equal(0, provider.Calls);
        using var response = await Explain(client, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var explanation = await Api.ReadJsonAsync(response);
        Assert.Equal("Ответы замедляются под нагрузкой.", explanation.GetProperty("hypothesis").GetString());
        var facts = explanation.GetProperty("facts");
        Assert.Equal(2, facts.GetArrayLength());
        Assert.Equal(7, facts[0].GetProperty("step_index").GetInt32());
        Assert.Equal(400, facts[0].GetProperty("target_rps").GetDouble());
        Assert.Equal(812, facts[0].GetProperty("value").GetDouble());
        Assert.Equal(2.4, facts[1].GetProperty("value").GetDouble());
        Assert.Equal("Повторите ступень со сбором длины очереди.", explanation.GetProperty("experiment").GetString());
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData("""{"step_index":99,"metric":"p99_ms"}""")]
    [InlineData("""{"step_index":8,"metric":"p99_ms"}""")]
    public async Task Unverifiable_model_facts_do_not_replace_the_saved_report(string facts)
    {
        using var provider = new OpenAiStub(_ => Task.FromResult(ModelAnswer(facts)));
        await using var factory = NewFactory();
        await using var host = WithProvider(factory, provider);
        using var client = host.CreateClient();
        var id = SaveReport(host.Services.GetRequiredService<RunStore>(), addSkippedStep: true);

        using var response = await Explain(client, id);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var run = await Api.GetRunAsync(client, id);
        Assert.Equal("completed", Api.Status(run));
        Assert.Equal(8, Api.StepCount(run));
        Assert.Equal("found", run.GetProperty("analysis").GetProperty("status").GetString());
        using var result = await client.GetAsync($"/runs/{id}/result");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Theory]
    [InlineData("completed", "refusal")]
    [InlineData("incomplete", "output_text")]
    public async Task Unusable_openai_responses_are_reported_as_gateway_errors(string status, string type)
    {
        using var provider = new OpenAiStub(_ => Task.FromResult(ModelAnswer(
            """{"step_index":7,"metric":"p99_ms"}""", status, type)));
        await using var factory = NewFactory();
        await using var host = WithProvider(factory, provider);
        using var client = host.CreateClient();
        var id = SaveReport(host.Services.GetRequiredService<RunStore>());

        using var response = await Explain(client, id);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task OpenAi_is_not_called_without_configuration_or_before_the_report_is_ready()
    {
        using var provider = new OpenAiStub(_ => throw new InvalidOperationException("OpenAI must not be called"));
        await using var factory = NewFactory(key: "");
        await using var host = WithProvider(factory, provider);
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<RunStore>();
        var ready = SaveReport(store);
        var queued = store.Create(new Scenario { Name = "queued" }).Id;

        using var missingKey = await Explain(client, ready);
        using var noAnalysis = await Explain(client, queued);
        using var notFound = await Explain(client, Guid.NewGuid());
        using var noJson = await client.PostAsync($"/runs/{ready}/explanation", content: null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missingKey.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, noAnalysis.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, noJson.StatusCode);
        Assert.Equal("completed", Api.Status(await Api.GetRunAsync(client, ready)));
        Assert.Equal(0, provider.Calls);
    }

    private static CoordinatorFactory NewFactory(string key = "test-key") =>
        new() { Settings = { ["OPENAI_API_KEY"] = key, ["OPENAI_MODEL"] = "" } };

    private static WebApplicationFactory<Program> WithProvider(CoordinatorFactory factory, OpenAiStub provider) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<RunExplanationClient>().ConfigurePrimaryHttpMessageHandler(() => provider)));

    private static Task<HttpResponseMessage> Explain(HttpClient client, Guid id) =>
        client.PostAsJsonAsync($"/runs/{id}/explanation", new { });

    private static Guid SaveReport(RunStore store, bool addSkippedStep = false)
    {
        using var fixture = JsonDocument.Parse(TestData.Read("results", "limit-found.json"));
        var steps = fixture.RootElement.GetProperty("steps").EnumerateArray().Select(TestData.StepFromJson).ToList();
        if (addSkippedStep)
            steps.Add(TestData.Step(8, targetRps: 450, skippedCount: 50));

        var run = store.Create(new Scenario
        {
            Name = "private-name",
            Target = new Target
            {
                Url = "https://private-host/?token=private-query",
                Method = "POST",
                Headers = new() { ["Authorization"] = "private-header" },
                Body = "private-body",
            },
            Steps = steps.Select(step => new Step
            {
                TargetRps = step.TargetRps,
                DurationSeconds = step.DurationSeconds,
            }).ToList(),
        }, agentCount: 3);
        Assert.NotNull(store.TryStart(run.Id));
        foreach (var step in steps)
            Assert.True(store.AddStep(run.Id, step));
        Assert.True(store.TryFinish(run.Id, RunStatus.Completed, null, new Analysis
        {
            Status = AnalysisStatus.Found,
            LimitRps = 350,
            FailedStepIndex = 7,
            LimitConfirmed = false,
            AnalyzedSteps = 7,
            Reasons = ["На ступени 7 выросли p99 и доля ошибок"],
            Message = "Предел не подтверждён",
            Diagnosis = new Diagnosis
            {
                Status = DiagnosisStatus.Diagnosed,
                Cause = DiagnosisCause.SlowResponses,
                Confidence = DiagnosisConfidence.Low,
                Explanation = "Ответы замедляются под нагрузкой",
            },
        }));
        return run.Id;
    }

    private static HttpResponseMessage ModelAnswer(string facts, string status = "completed", string type = "output_text") =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                status,
                output = new[]
                {
                    new { type = "message", content = new[]
                    {
                        new
                        {
                            type,
                            text = $$"""
                                {"hypothesis":"Ответы замедляются под нагрузкой.","facts":[{{facts}}],"experiment":"Повторите ступень со сбором длины очереди."}
                                """,
                        },
                    } },
                },
            }),
        };

    private sealed class OpenAiStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request);
        }
    }
}
