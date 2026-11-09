using System.Globalization;
using System.Text;

namespace Bendmark.Coordinator;

public static class ScenarioValidator
{
    public static Dictionary<string, string[]> Validate(Scenario? scenario)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) => errors[field] = [message];

        if (scenario is null)
        {
            Add("body", "Нужен сценарий в теле запроса");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(scenario.Name))
            Add("name", "Имя сценария не задано");

        var target = scenario.Target;
        if (target is null)
        {
            Add("target", "Цель не задана");
        }
        else
        {
            if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                Add("target.url", "Нужен абсолютный URL с http или https");

            if (target.Method is null || !Methods.Contains(target.Method))
                Add("target.method", "Поддерживаются GET, POST, PUT, PATCH и DELETE в верхнем регистре");

            ValidateBody(target, Add);
            ValidateHeaders(target, Add);
        }

        if (scenario.Steps is null || scenario.Steps.Count == 0)
        {
            Add("steps", "Нужна хотя бы одна ступень");
        }
        else
        {
            for (var i = 0; i < scenario.Steps.Count; i++)
            {
                var step = scenario.Steps[i];
                if (step is null)
                {
                    Add($"steps[{i}]", "Пустая ступень");
                    continue;
                }
                if (!IsPositive(step.TargetRps))
                    Add($"steps[{i}].target_rps", "Должно быть конечным числом больше нуля");
                if (!IsPositive(step.DurationSeconds))
                    Add($"steps[{i}].duration_seconds", "Должно быть конечным числом больше нуля");
            }

            // Анализатору нужна рампа: target_rps строго растёт от ступени к ступени.
            // Сравниваем только соседей, у которых нагрузка уже прошла проверку выше
            for (var i = 1; i < scenario.Steps.Count; i++)
            {
                var previous = scenario.Steps[i - 1];
                var current = scenario.Steps[i];
                if (previous is null || current is null
                    || !IsPositive(previous.TargetRps) || !IsPositive(current.TargetRps))
                    continue;
                if (current.TargetRps <= previous.TargetRps)
                    Add($"steps[{i}].target_rps",
                        $"Нагрузка должна расти: {Format(current.TargetRps)} после {Format(previous.TargetRps)}");
            }
        }

        return errors;
    }

    // Пределы совпадают с агентом (services/agent/include/sender.hpp) и proto/README.md
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxHeaders = 32;
    public const int MaxHeaderBytes = 8 * 1024;

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    // Их агент выставляет сам по телу запроса
    private static readonly string[] ReservedHeaders = ["Content-Length", "Transfer-Encoding"];

    private static void ValidateBody(Target target, Action<string, string> add)
    {
        if (string.IsNullOrEmpty(target.Body))
            return;

        if (target.Method is "GET" or "DELETE")
        {
            add("target.body", $"У {target.Method} не бывает тела: уберите его или смените метод");
            return;
        }

        if (Encoding.UTF8.GetByteCount(target.Body) is var size and > MaxBodyBytes)
            add("target.body", $"Тело больше {MaxBodyBytes / 1024} КиБ: {size} байт");

        if (target.Headers?.Keys.Any(IsContentType) != true)
            add("target.headers", "Для тела нужен заголовок Content-Type, например application/json");
    }

    private static void ValidateHeaders(Target target, Action<string, string> add)
    {
        var headers = target.Headers;
        if (headers is null || headers.Count == 0)
            return;

        if (headers.Count > MaxHeaders)
        {
            add("target.headers", $"Больше {MaxHeaders} заголовков: {headers.Count}");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bytes = 0;
        foreach (var (name, value) in headers)
        {
            var field = $"target.headers.{name}";
            if (!IsToken(name))
                add(field, "Имя заголовка - латиница, цифры и !#$%&'*+-.^_`|~, без пробелов и двоеточия");
            else if (ReservedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
                add(field, "Этот заголовок агент выставляет сам по телу запроса");
            else if (!seen.Add(name))
                add(field, "Заголовок повторяется: имена заголовков не зависят от регистра");
            else if (value is null)
                add(field, "Значение не задано; для пустого значения укажите \"\"");
            else if (!IsHeaderValue(value))
                add(field, "Значение не должно содержать переводов строк и других управляющих символов");

            bytes += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value ?? "");
        }

        if (bytes > MaxHeaderBytes)
            add("target.headers", $"Заголовки больше {MaxHeaderBytes / 1024} КиБ: {bytes} байт");
    }

    private static bool IsContentType(string name) => string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase);

    // tchar из RFC 9110
    private static bool IsToken(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    // CR и LF позволили бы дописать в запрос чужие заголовки
    private static bool IsHeaderValue(string value) =>
        value.All(c => c == '\t' || (c >= ' ' && c != '\u007f'));

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
