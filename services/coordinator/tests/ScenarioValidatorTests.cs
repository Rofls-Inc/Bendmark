using Xunit;

namespace Bendmark.Coordinator.Tests;

// Все ветки ScenarioValidator без HTTP
public class ScenarioValidatorTests
{
    private static Scenario Valid(params double[] rps)
    {
        var values = rps.Length == 0 ? new[] { 100.0, 150, 200 } : rps;
        return new Scenario
        {
            Name = "ramp",
            Target = new Target { Url = "http://abstock:8080/", Method = "GET" },
            Steps = values.Select(value => new Step { TargetRps = value, DurationSeconds = 45 }).ToList(),
        };
    }

    private static Dictionary<string, string[]> Validate(Scenario? scenario) => ScenarioValidator.Validate(scenario);

    [Fact]
    public void Valid_ramp_has_no_errors()
    {
        Assert.Empty(Validate(Valid()));
        Assert.Empty(Validate(Valid(100)));
        Assert.Empty(Validate(Valid(0.5, 1, 1.5)));
    }

    [Fact]
    public void Missing_scenario_is_body_error()
    {
        var errors = Validate(null);
        Assert.Equal(new[] { "body" }, errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_name_is_error(string? name)
    {
        var errors = Validate(Valid() with { Name = name });
        Assert.Equal(new[] { "name" }, errors.Keys);
    }

    [Fact]
    public void Missing_target_is_error()
    {
        var errors = Validate(Valid() with { Target = null });
        Assert.Equal(new[] { "target" }, errors.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://abstock/")]
    [InlineData("file:///etc/passwd")]
    public void Url_must_be_absolute_http(string? url)
    {
        var scenario = Valid() with { Target = new Target { Url = url, Method = "GET" } };
        Assert.Equal(new[] { "target.url" }, Validate(scenario).Keys);
    }

    [Theory]
    [InlineData("http://abstock:8080/")]
    [InlineData("https://example.com/path?q=1")]
    public void Http_and_https_urls_are_accepted(string url)
    {
        var scenario = Valid() with { Target = new Target { Url = url, Method = "GET" } };
        Assert.Empty(Validate(scenario));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("get")]
    [InlineData("Post")]
    [InlineData(" GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("FETCH")]
    public void Method_must_be_a_supported_uppercase_method(string? method)
    {
        var scenario = Valid() with { Target = new Target { Url = "http://abstock:8080/", Method = method } };
        Assert.Equal(new[] { "target.method" }, Validate(scenario).Keys);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Supported_methods_without_body_are_accepted(string method)
    {
        var scenario = Valid() with { Target = new Target { Url = "http://abstock:8080/", Method = method } };
        Assert.Empty(Validate(scenario));
    }

    private static Scenario WithTarget(string method, Dictionary<string, string?>? headers = null, string? body = null) =>
        Valid() with { Target = new Target { Url = "http://abstock:8080/orders", Method = method, Headers = headers, Body = body } };

    private static Dictionary<string, string?> Json => new() { ["Content-Type"] = "application/json" };

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public void Body_with_headers_is_accepted(string method)
    {
        var headers = new Dictionary<string, string?>
        {
            ["content-type"] = "application/json; charset=utf-8",
            ["Authorization"] = "Bearer abc.def",
            ["X-Trace"] = "a\tb ;=,\"q\"",
            ["X-Empty"] = "",
        };
        Assert.Empty(Validate(WithTarget(method, headers, "{\"qty\": 5, \"note\": \"привет\"}")));
    }

    [Fact]
    public void Empty_body_is_no_body()
    {
        Assert.Empty(Validate(WithTarget("GET", body: "")));
        Assert.Empty(Validate(WithTarget("POST", body: "")));
        Assert.Empty(Validate(WithTarget("DELETE", Json)));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public void Body_is_not_allowed_with_get_and_delete(string method)
    {
        var errors = Validate(WithTarget(method, Json, "{}"));
        Assert.Equal(new[] { "target.body" }, errors.Keys);
        Assert.Contains(method, Assert.Single(errors["target.body"]));

        // Без Content-Type ошибка та же одна: требовать заголовок для лишнего тела бессмысленно
        Assert.Equal(new[] { "target.body" }, Validate(WithTarget(method, body: "{}")).Keys);
    }

    [Fact]
    public void Body_requires_content_type()
    {
        var errors = Validate(WithTarget("POST", new() { ["Accept"] = "*/*" }, "a=1"));
        Assert.Equal(new[] { "target.headers" }, errors.Keys);
        Assert.Contains("Content-Type", Assert.Single(errors["target.headers"]));
    }

    [Fact]
    public void Body_size_is_limited_in_utf8_bytes()
    {
        Assert.Empty(Validate(WithTarget("POST", Json, new string('x', ScenarioValidator.MaxBodyBytes))));

        var errors = Validate(WithTarget("POST", Json, new string('x', ScenarioValidator.MaxBodyBytes + 1)));
        Assert.Equal(new[] { "target.body" }, errors.Keys);

        // Кириллица - два байта на символ: половина предела в символах уже на пределе в байтах
        var cyrillic = new string('я', ScenarioValidator.MaxBodyBytes / 2 + 1);
        Assert.Equal(new[] { "target.body" }, Validate(WithTarget("POST", Json, cyrillic)).Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad Name")]
    [InlineData("X:Y")]
    [InlineData("Заголовок")]
    [InlineData("X\r\nInjected")]
    public void Header_name_must_be_a_token(string name)
    {
        var errors = Validate(WithTarget("GET", new() { [name] = "v" }));
        Assert.Equal(new[] { $"target.headers.{name}" }, errors.Keys);
    }

    [Theory]
    [InlineData("a\r\nX-Injected: 1")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\u007f")]
    public void Header_value_has_no_control_characters(string value)
    {
        var errors = Validate(WithTarget("GET", new() { ["X-Value"] = value }));
        Assert.Equal(new[] { "target.headers.X-Value" }, errors.Keys);
    }

    [Fact]
    public void Null_header_value_is_error()
    {
        var errors = Validate(WithTarget("GET", new() { ["X-Value"] = null }));
        Assert.Equal(new[] { "target.headers.X-Value" }, errors.Keys);
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("transfer-encoding")]
    public void Headers_set_by_agent_are_rejected(string name)
    {
        var errors = Validate(WithTarget("GET", new() { [name] = "5" }));
        Assert.Equal(new[] { $"target.headers.{name}" }, errors.Keys);
    }

    [Fact]
    public void Header_names_are_unique_ignoring_case()
    {
        var errors = Validate(WithTarget("GET", new() { ["Accept"] = "a", ["ACCEPT"] = "b" }));
        Assert.Single(errors);
        Assert.StartsWith("target.headers.", errors.Keys.Single());
    }

    [Fact]
    public void Header_count_and_size_are_limited()
    {
        var many = Enumerable.Range(0, ScenarioValidator.MaxHeaders + 1)
            .ToDictionary(i => $"X-H{i}", _ => (string?)"v");
        Assert.Equal(new[] { "target.headers" }, Validate(WithTarget("GET", many)).Keys);

        var enough = many.Take(ScenarioValidator.MaxHeaders).ToDictionary();
        Assert.Empty(Validate(WithTarget("GET", enough)));

        var big = new Dictionary<string, string?> { ["X-Big"] = new string('v', ScenarioValidator.MaxHeaderBytes) };
        Assert.Equal(new[] { "target.headers" }, Validate(WithTarget("GET", big)).Keys);
    }

    [Fact]
    public void Missing_or_empty_steps_are_error()
    {
        Assert.Equal(new[] { "steps" }, Validate(Valid() with { Steps = null }).Keys);
        Assert.Equal(new[] { "steps" }, Validate(Valid() with { Steps = [] }).Keys);
    }

    [Fact]
    public void Null_step_is_error()
    {
        var scenario = Valid() with { Steps = [new Step { TargetRps = 100, DurationSeconds = 45 }, null!] };
        Assert.Equal(new[] { "steps[1]" }, Validate(scenario).Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Step_values_must_be_finite_and_positive(double value)
    {
        var rps = Valid() with { Steps = [new Step { TargetRps = value, DurationSeconds = 45 }] };
        Assert.Equal(new[] { "steps[0].target_rps" }, Validate(rps).Keys);

        var duration = Valid() with { Steps = [new Step { TargetRps = 100, DurationSeconds = value }] };
        Assert.Equal(new[] { "steps[0].duration_seconds" }, Validate(duration).Keys);
    }

    [Fact]
    public void Decreasing_load_is_error_with_both_values()
    {
        var errors = Validate(Valid(200, 100));
        Assert.Equal(new[] { "steps[1].target_rps" }, errors.Keys);
        Assert.Equal("Нагрузка должна расти: 100 после 200", Assert.Single(errors["steps[1].target_rps"]));
    }

    [Fact]
    public void Constant_load_is_error()
    {
        var errors = Validate(Valid(100, 150, 150));
        Assert.Equal(new[] { "steps[2].target_rps" }, errors.Keys);
        Assert.Equal("Нагрузка должна расти: 150 после 150", Assert.Single(errors["steps[2].target_rps"]));
    }

    [Fact]
    public void Fractional_values_use_invariant_format()
    {
        var errors = Validate(Valid(10.5, 10.25));
        Assert.Equal("Нагрузка должна расти: 10.25 после 10.5", Assert.Single(errors["steps[1].target_rps"]));
    }

    [Fact]
    public void Every_violation_is_reported_against_its_own_step()
    {
        var errors = Validate(Valid(100, 200, 150, 300, 300));
        Assert.Equal(new[] { "steps[2].target_rps", "steps[4].target_rps" }, errors.Keys.Order());
    }

    [Fact]
    public void Growth_is_not_checked_next_to_invalid_values()
    {
        // Ступень 1 уже с ошибкой положительности; сравнение с ней не добавляет второе сообщение
        var errors = Validate(Valid(100, 0, 50));
        Assert.Equal(new[] { "steps[1].target_rps" }, errors.Keys);
        Assert.Equal("Должно быть конечным числом больше нуля", Assert.Single(errors["steps[1].target_rps"]));

        var withNull = Valid() with
        {
            Steps = [new Step { TargetRps = 200, DurationSeconds = 45 }, null!, new Step { TargetRps = 100, DurationSeconds = 45 }],
        };
        Assert.Equal(new[] { "steps[1]" }, Validate(withNull).Keys);
    }

    [Fact]
    public void Invalid_duration_does_not_block_growth_check()
    {
        var scenario = Valid() with
        {
            Steps = [new Step { TargetRps = 200, DurationSeconds = 45 }, new Step { TargetRps = 100, DurationSeconds = 0 }],
        };
        Assert.Equal(new[] { "steps[1].duration_seconds", "steps[1].target_rps" }, Validate(scenario).Keys.Order());
    }
}
