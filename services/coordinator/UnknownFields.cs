using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Bendmark.Coordinator;

// Ищет в сценарии поля, которых нет в модели (обычно опечатки: target_rsp, urll).
// Библиотеки сообщают только о первом таком поле и без пути к нему, поэтому
// проверяем дерево документа сами и отдаём все ошибки с полным путём, как у остальных полей.
// Имена полей берутся из модели в snake_case, так же их видят JSON и YAML.
public static class UnknownFields
{
    private const string Message = "Неизвестное поле. Допустимые: ";

    public static Dictionary<string, string[]> Find(JsonElement root)
    {
        var errors = new Dictionary<string, string[]>();
        Visit(root, typeof(Scenario), "", errors);
        return errors;
    }

    public static Dictionary<string, string[]> Find(YamlNode root)
    {
        var errors = new Dictionary<string, string[]>();
        Visit(root, typeof(Scenario), "", errors);
        return errors;
    }

    private static void Visit(JsonElement element, Type type, string path, Dictionary<string, string[]> errors)
    {
        if (ItemType(type) is { } itemType)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return;
            var index = 0;
            foreach (var item in element.EnumerateArray())
                Visit(item, itemType, $"{path}[{index++}]", errors);
            return;
        }

        if (!IsModel(type) || element.ValueKind != JsonValueKind.Object)
            return;

        var fields = FieldsOf(type);
        foreach (var property in element.EnumerateObject())
        {
            var field = Join(path, property.Name);
            if (fields.TryGetValue(property.Name, out var fieldType))
                Visit(property.Value, fieldType, field, errors);
            else
                errors[field] = [Message + string.Join(", ", fields.Keys)];
        }
    }

    private static void Visit(YamlNode node, Type type, string path, Dictionary<string, string[]> errors)
    {
        if (ItemType(type) is { } itemType)
        {
            if (node is not YamlSequenceNode sequence)
                return;
            for (var i = 0; i < sequence.Children.Count; i++)
                Visit(sequence.Children[i], itemType, $"{path}[{i}]", errors);
            return;
        }

        if (!IsModel(type) || node is not YamlMappingNode mapping)
            return;

        var fields = FieldsOf(type);
        foreach (var entry in mapping.Children)
        {
            // Ключ-не-строку (список, словарь) разберёт и отклонит десериализатор
            if (entry.Key is not YamlScalarNode { Value: { } name })
                continue;
            var field = Join(path, name);
            if (fields.TryGetValue(name, out var fieldType))
                Visit(entry.Value, fieldType, field, errors);
            else
                errors[field] = [Message + string.Join(", ", fields.Keys)];
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    // Классы модели сценария: Scenario, Target, Step
    private static bool IsModel(Type type) => type.IsClass && type.Namespace == typeof(Scenario).Namespace;

    private static Type? ItemType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;

    private static Dictionary<string, Type> FieldsOf(Type type) =>
        type.GetProperties().ToDictionary(
            property => JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name),
            property => property.PropertyType);
}
