namespace Bendmark.Coordinator;

public static class ScenarioValidator
{
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD" };

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

            if (string.IsNullOrWhiteSpace(target.Method) || !AllowedMethods.Contains(target.Method))
                Add("target.method", "Допустимы GET, POST, PUT, PATCH, DELETE, HEAD");
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
                if (step.TargetRps <= 0)
                    Add($"steps[{i}].target_rps", "Должно быть больше нуля");
                if (step.DurationSeconds <= 0)
                    Add($"steps[{i}].duration_seconds", "Должно быть больше нуля");
            }
        }

        return errors;
    }
}
