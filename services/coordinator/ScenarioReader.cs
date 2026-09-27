using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bendmark.Coordinator;

// Читает сценарий из тела запроса
public static class ScenarioReader
{
    private static readonly string[] YamlContentTypes = ["application/yaml", "application/x-yaml", "text/yaml"];

    public static async Task<(Scenario? Scenario, IResult? Error)> ReadAsync(HttpRequest request)
    {
        if (request.HasJsonContentType())
        {
            try
            {
                return (await request.ReadFromJsonAsync<Scenario>(), null);
            }
            catch (JsonException e)
            {
                return (null, BodyError($"Некорректный JSON: {e.Message}"));
            }
        }

        if (IsYaml(request.ContentType))
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync();

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            try
            {
                return (deserializer.Deserialize<Scenario>(text), null);
            }
            catch (YamlException e)
            {
                return (null, BodyError($"Некорректный YAML: {e.Message}"));
            }
        }

        return (null, Results.Problem(
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            detail: "Ожидается Content-Type application/json или application/yaml"));
    }

    private static bool IsYaml(string? contentType)
    {
        var mediaType = contentType?.Split(';')[0].Trim();
        return mediaType is not null
            && YamlContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
    }

    private static IResult BodyError(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = [message] });
}
