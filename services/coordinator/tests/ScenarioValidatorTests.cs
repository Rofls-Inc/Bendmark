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
    [InlineData("Get")]
    [InlineData("POST")]
    [InlineData(" GET")]
    public void Method_must_be_exactly_GET(string? method)
    {
        var scenario = Valid() with { Target = new Target { Url = "http://abstock:8080/", Method = method } };
        Assert.Equal(new[] { "target.method" }, Validate(scenario).Keys);
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
