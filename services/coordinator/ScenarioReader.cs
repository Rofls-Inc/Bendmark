using System.Text.Json;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Bendmark.Coordinator;

// Читает сценарий из тела запроса
public static class ScenarioReader
{
    private static readonly string[] YamlContentTypes = ["application/yaml", "application/x-yaml", "text/yaml"];

    public static async Task<(Scenario? Scenario, IResult? Error)> ReadAsync(HttpRequest request)
    {
        if (request.HasJsonContentType())
            return await ReadJsonAsync(request);

        if (IsYaml(request.ContentType))
            return await ReadYamlAsync(request);

        return (null, Results.Problem(
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            detail: "Ожидается Content-Type application/json или application/yaml"));
    }

    private static async Task<(Scenario? Scenario, IResult? Error)> ReadJsonAsync(HttpRequest request)
    {
        var options = request.HttpContext.RequestServices
            .GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body);
        }
        catch (JsonException e)
        {
            return (null, FieldError("body", $"Некорректный JSON: {e.Message}"));
        }

        using (document)
        {
            var unknown = UnknownFields.Find(document.RootElement);
            if (unknown.Count > 0)
                return (null, Results.ValidationProblem(unknown));

            try
            {
                return (document.Deserialize<Scenario>(options), null);
            }
            catch (JsonException e)
            {
                return (null, FieldError(ToFieldName(e.Path), "Неверный тип значения"));
            }
        }
    }

    private static async Task<(Scenario? Scenario, IResult? Error)> ReadYamlAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();

        // Неизвестные поля без IgnoreUnmatchedProperties тоже дают YamlException,
        // но UnknownFields сообщает о них раньше и с путём к полю
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count > 0)
            {
                var unknown = UnknownFields.Find(stream.Documents[0].RootNode);
                if (unknown.Count > 0)
                    return (null, Results.ValidationProblem(unknown));
            }

            return (deserializer.Deserialize<Scenario>(text), null);
        }
        catch (YamlException e)
        {
            var reason = e.InnerException?.Message ?? e.Message;
            return (null, FieldError(
                "body",
                $"Некорректный YAML (строка {e.Start.Line}, столбец {e.Start.Column}): {reason}"));
        }
    }

    private static bool IsYaml(string? contentType)
    {
        var mediaType = contentType?.Split(';')[0].Trim();
        return mediaType is not null
            && YamlContentTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
    }

    private static string ToFieldName(string? path) =>
        path is null || path == "$" ? "body"
        : path.StartsWith("$.") ? path[2..]
        : path;

    private static IResult FieldError(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
