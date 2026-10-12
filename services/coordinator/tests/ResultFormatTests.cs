using System.Net;
using System.Text.Json;
using Xunit;

namespace Bendmark.Coordinator.Tests;

// Ступени координатора должны совпадать по форме с result.json, который читает анализатор
public class ResultFormatTests
{
    [Fact]
    public async Task Result_matches_limit_found_fixture()
    {
        using var expectedDocument = JsonDocument.Parse(TestData.Read("results", "limit-found.json"));
        var expected = expectedDocument.RootElement;

        await using var factory = new CoordinatorFactory();
        var client = factory.CreateClient();
        var id = await Api.CreateRunAsync(client, "ramp.json");
        await factory.Agent.WaitStartedAsync();
        foreach (var step in expected.GetProperty("steps").EnumerateArray())
            factory.Agent.SendStep(TestData.StepFromJson(step));
        factory.Agent.Finish();
        var run = await Api.WaitForStatusAsync(client, id, "completed");

        using var response = await client.GetAsync($"/runs/{id}/result");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Api.ReadJsonAsync(response);

        AssertSameShape(expected, result, "$");
        // В GET /runs/{id} ступени в том же формате
        AssertSameShape(expected.GetProperty("steps"), run.GetProperty("steps"), "$.steps");

        foreach (var step in result.GetProperty("steps").EnumerateArray())
        {
            // Поле новее файла: в результате координатора оно есть всегда, явным нулём
            Assert.Equal(0UL, step.GetProperty("skipped_count").GetUInt64());
            // Счётчики - целые числа, не строки ProtoJSON
            Assert.Equal(JsonValueKind.Number, step.GetProperty("request_count").ValueKind);
            Assert.True(step.GetProperty("request_count").TryGetUInt64(out _));
        }
    }

    [Fact]
    public void Error_rate_is_rounded_and_zero_without_requests()
    {
        Assert.Equal(0.0, StepResultDto.RatePercent(0, 0));
        Assert.Equal(0.0, StepResultDto.RatePercent(5, 0));
        Assert.Equal(33.3, StepResultDto.RatePercent(1, 3));
        Assert.Equal(2.4, StepResultDto.RatePercent(368, 15345));
        Assert.Equal(100.0, StepResultDto.RatePercent(10, 10));
    }

    [Fact]
    public void Step_without_requests_serializes_explicit_zeros()
    {
        var step = StepResultDto.Create(1, 100, 10, 0, 0,
            new LatencyDto { P50 = 0, P90 = 0, P99 = 0 }, 0, 25);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(step));
        var json = document.RootElement;
        Assert.Equal(
            new[] { "index", "target_rps", "duration_seconds", "request_count", "throughput_rps",
                "latency_ms", "errors", "skipped_count" },
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal(0UL, json.GetProperty("request_count").GetUInt64());
        Assert.Equal(0.0, json.GetProperty("errors").GetProperty("rate_percent").GetDouble());
        Assert.Equal(25UL, json.GetProperty("skipped_count").GetUInt64());
    }

    // Все поля эталона есть с тем же значением; лишним может быть только skipped_count,
    // которого нет в старом файле limit-found.json
    private static void AssertSameShape(JsonElement expected, JsonElement actual, string path)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.Equal(JsonValueKind.Object, actual.ValueKind);
                var extra = actual.EnumerateObject().Select(p => p.Name).ToHashSet();
                foreach (var property in expected.EnumerateObject())
                {
                    Assert.True(actual.TryGetProperty(property.Name, out var value),
                        $"Нет поля {path}.{property.Name}");
                    AssertSameShape(property.Value, value, $"{path}.{property.Name}");
                    extra.Remove(property.Name);
                }
                extra.Remove("skipped_count");
                Assert.True(extra.Count == 0, $"Лишние поля в {path}: {string.Join(", ", extra)}");
                break;
            case JsonValueKind.Array:
                Assert.Equal(JsonValueKind.Array, actual.ValueKind);
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var i = 0; i < expected.GetArrayLength(); i++)
                    AssertSameShape(expected[i], actual[i], $"{path}[{i}]");
                break;
            case JsonValueKind.Number:
                Assert.Equal(JsonValueKind.Number, actual.ValueKind);
                Assert.True(expected.GetDouble() == actual.GetDouble(),
                    $"{path}: ожидалось {expected}, получено {actual}");
                break;
            default:
                Assert.Equal(expected.ValueKind, actual.ValueKind);
                Assert.Equal(expected.ToString(), actual.ToString());
                break;
        }
    }
}
