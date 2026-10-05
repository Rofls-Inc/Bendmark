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

            if (target.Method != "GET")
                Add("target.method", "В v1 поддерживается только GET в верхнем регистре");
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
        }

        return errors;
    }
    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
}
