using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Bendmark.Coordinator.Tests;

public class ScenarioFixtureTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client = factory.CreateClient();

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "scenarios", name));

    private async Task<HttpResponseMessage> PostFixture(string name, string contentType) =>
        await client.PostAsync("/runs", new StringContent(ReadFixture(name), Encoding.UTF8, contentType));

    [Theory]
    [InlineData("ramp.json", "application/json")]
    [InlineData("ramp.yaml", "application/yaml")]
    public async Task Valid_ramp_is_accepted_and_preserved(string name, string contentType)
    {
        using var response = await PostFixture(name, contentType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal($"/runs/{id}", response.Headers.Location?.OriginalString);

        using var fetched = await client.GetAsync($"/runs/{id}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using var run = JsonDocument.Parse(await fetched.Content.ReadAsStringAsync());
        using var expectedDocument = JsonDocument.Parse(ReadFixture("ramp.json"));
        var expected = expectedDocument.RootElement;
        var actual = run.RootElement.GetProperty("scenario");
        Assert.Equal(expected.GetProperty("name").GetString(), actual.GetProperty("name").GetString());
        foreach (var field in new[] { "url", "method" })
            Assert.Equal(expected.GetProperty("target").GetProperty(field).GetString(),
                actual.GetProperty("target").GetProperty(field).GetString());

        var expectedSteps = expected.GetProperty("steps");
        var actualSteps = actual.GetProperty("steps");
        Assert.Equal(expectedSteps.GetArrayLength(), actualSteps.GetArrayLength());
        for (var i = 0; i < expectedSteps.GetArrayLength(); ++i)
            foreach (var field in new[] { "target_rps", "duration_seconds" })
                Assert.Equal(expectedSteps[i].GetProperty(field).GetDouble(),
                    actualSteps[i].GetProperty(field).GetDouble());
    }

    [Fact]
    public async Task Invalid_values_report_field_errors()
    {
        using var response = await PostFixture("invalid-scenario.json", "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = body.RootElement.GetProperty("errors");
        foreach (var field in new[] { "name", "target.url", "target.method",
                     "steps[1].target_rps", "steps[1].duration_seconds" })
            Assert.True(errors.TryGetProperty(field, out _), $"Missing validation error: {field}");
    }

    [Theory]
    [InlineData("wrong-type-scenario.json", "application/json", "steps[1].target_rps")]
    [InlineData("malformed.json", "application/json", "body")]
    [InlineData("malformed.yaml", "application/yaml", "body")]
    public async Task Invalid_input_is_400_with_a_field_error(string name, string contentType, string field)
    {
        using var response = await PostFixture(name, contentType);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Unsupported_content_type_is_415()
    {
        using var response = await PostFixture("ramp.json", "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
