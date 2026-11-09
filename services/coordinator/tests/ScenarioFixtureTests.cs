using System.Net;
using System.Text.Json;
using Xunit;

namespace Bendmark.Coordinator.Tests;

// Фабрика подменяет агента фейком: тестам разбора сценария агент не нужен
public class ScenarioFixtureTests(CoordinatorFactory factory) : IClassFixture<CoordinatorFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    private static string ReadFixture(string name) => TestData.Read("scenarios", name);

    private async Task<HttpResponseMessage> PostFixture(string name, string contentType) =>
        await Api.PostScenarioAsync(client, name, contentType);

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

    [Theory]
    [InlineData("post-json.json", "application/json")]
    [InlineData("post-json.yaml", "application/yaml")]
    public async Task Post_with_headers_and_body_is_accepted_and_preserved(string name, string contentType)
    {
        using var response = await PostFixture(name, contentType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await Api.ReadJsonAsync(response)).GetProperty("id").GetGuid();

        var actual = (await Api.GetRunAsync(client, id)).GetProperty("scenario").GetProperty("target");
        using var expectedDocument = JsonDocument.Parse(ReadFixture("post-json.json"));
        var expected = expectedDocument.RootElement.GetProperty("target");

        Assert.Equal("POST", actual.GetProperty("method").GetString());
        Assert.Equal(expected.GetProperty("body").GetString(), actual.GetProperty("body").GetString());
        // Имена заголовков не меняются: ни snake_case, ни другой регистр
        var expectedHeaders = expected.GetProperty("headers").EnumerateObject()
            .Select(h => (h.Name, h.Value.GetString())).Order();
        var actualHeaders = actual.GetProperty("headers").EnumerateObject()
            .Select(h => (h.Name, h.Value.GetString())).Order();
        Assert.Equal(expectedHeaders, actualHeaders);
    }

    [Fact]
    public async Task Invalid_request_reports_method_body_and_header_errors()
    {
        using var response = await PostFixture("invalid-request.json", "application/json");
        await AssertFieldErrors(response,
            "target.body", "target.headers.Content-Length", "target.headers.Bad Header", "target.headers.X-Injected");
    }

    [Fact]
    public async Task Header_value_of_wrong_type_is_field_error()
    {
        const string json = """
            {"name": "x", "target": {"url": "http://abstock:8080/", "method": "GET", "headers": {"X-Count": 5}},
             "steps": [{"target_rps": 100, "duration_seconds": 45}]}
            """;
        using var response = await PostRaw(json, "application/json");
        // Значение заголовка - строка: число не приводится молча
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await Api.ReadJsonAsync(response)).GetProperty("errors").EnumerateObject().ToList();
        Assert.StartsWith("target.headers", Assert.Single(errors).Name);
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

    // строгий разбор. Полные имена типов - чтобы не зависеть от using в начале файла

    private async Task<HttpResponseMessage> PostRaw(string body, string? contentType)
    {
        var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body));
        if (contentType is not null)
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return await client.PostAsync("/runs", content);
    }

    // Ответ 400, и ошибки ровно у перечисленных полей
    private static async Task AssertFieldErrors(HttpResponseMessage response, params string[] fields)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var actual = body.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name);
        Assert.Equal(fields.Order(), actual.Order());
    }

    [Theory]
    [InlineData("unknown-field.json", "application/json")]
    [InlineData("unknown-field.yaml", "application/yaml")]
    public async Task Typo_in_field_name_is_400_with_its_path(string name, string contentType)
    {
        using var response = await PostFixture(name, contentType);
        await AssertFieldErrors(response, "steps[1].target_rsp");
    }

    [Fact]
    public async Task Unknown_fields_are_reported_at_every_level_in_json()
    {
        const string json = """
            {
              "name": "x", "nmae": "y",
              "target": { "url": "http://abstock:8080/", "method": "GET", "urll": "z" },
              "steps": [ { "target_rps": 100, "duration_seconds": 45, "duration": 1 } ]
            }
            """;
        using var response = await PostRaw(json, "application/json");
        await AssertFieldErrors(response, "nmae", "target.urll", "steps[0].duration");
    }

    [Fact]
    public async Task Unknown_fields_are_reported_at_every_level_in_yaml()
    {
        const string yaml = """
            name: x
            nmae: y
            target:
              url: http://abstock:8080/
              method: GET
              urll: z
            steps:
              - target_rps: 100
                duration_seconds: 45
                duration: 1
            """;
        using var response = await PostRaw(yaml, "application/yaml");
        await AssertFieldErrors(response, "nmae", "target.urll", "steps[0].duration");
    }

    [Fact]
    public async Task Unknown_field_error_lists_allowed_fields()
    {
        using var response = await PostRaw("""{"name": "x", "targett": {}}""", "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = body.RootElement.GetProperty("errors").GetProperty("targett")[0].GetString();
        Assert.Equal("Неизвестное поле. Допустимые: name, target, steps", message);
    }

    [Fact]
    public async Task Field_names_are_case_sensitive()
    {
        using var response = await PostRaw(ReadFixture("ramp.json").Replace("\"name\"", "\"Name\""), "application/json");
        await AssertFieldErrors(response, "Name");
    }

    [Fact]
    public async Task Decreasing_load_is_400()
    {
        using var response = await PostFixture("decreasing-rps.json", "application/json");
        await AssertFieldErrors(response, "steps[1].target_rps");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"ramp\"")]
    public async Task Json_without_scenario_object_is_body_error(string json)
    {
        using var response = await PostRaw(json, "application/json");
        await AssertFieldErrors(response, "body");
    }

    [Fact]
    public async Task Json_null_step_is_step_error()
    {
        const string json = """
            {"name": "x", "target": {"url": "http://abstock:8080/", "method": "GET"}, "steps": [null]}
            """;
        using var response = await PostRaw(json, "application/json");
        await AssertFieldErrors(response, "steps[0]");
    }

    [Theory]
    [InlineData("")]
    [InlineData("# только комментарий")]
    public async Task Empty_yaml_is_body_error(string yaml)
    {
        using var response = await PostRaw(yaml, "application/yaml");
        await AssertFieldErrors(response, "body");
    }

    [Fact]
    public async Task Yaml_wrong_type_is_body_error_with_position()
    {
        var yaml = ReadFixture("ramp.yaml").Replace("target_rps: 150", "target_rps: fast");
        using var response = await PostRaw(yaml, "application/yaml");
        await AssertFieldErrors(response, "body");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("строка", body.RootElement.GetProperty("errors").GetProperty("body")[0].GetString());
    }

    [Theory]
    [InlineData("application/json; charset=utf-8", "ramp.json")]
    [InlineData("application/yaml", "ramp.yaml")]
    [InlineData("application/x-yaml", "ramp.yaml")]
    [InlineData("text/yaml", "ramp.yaml")]
    [InlineData("APPLICATION/YAML; charset=utf-8", "ramp.yaml")]
    public async Task Supported_content_types_are_accepted(string contentType, string name)
    {
        using var response = await PostRaw(ReadFixture(name), contentType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData(null)]
    public async Task Other_content_types_are_415(string? contentType)
    {
        using var response = await PostRaw(ReadFixture("ramp.json"), contentType);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
