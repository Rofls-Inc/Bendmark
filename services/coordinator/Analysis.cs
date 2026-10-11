using System.Globalization;
using Grpc.Core;

namespace Bendmark.Coordinator;

public enum AnalysisStatus
{
    // Прогон завершён, ответа анализатора ещё нет
    Pending,
    // Отказ найден: limit_rps — последняя ступень без отказа (нет, если отказ на первой)
    Found,
    // Отказа не было: limit_rps — нижняя оценка предела
    NotFound,
    // Измерения не определяют предел: агент не успевал подавать нагрузку или нет ответов
    Undetermined,
    // Анализ не удался (анализатор недоступен, отклонил данные); статус прогона не меняется
    Error,
}

// Результат поиска предела в GET /runs/{id}
public sealed record Analysis
{
    public required AnalysisStatus Status { get; init; }
    public double? LimitRps { get; init; }
    public uint? FailedStepIndex { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    // Сколько первых ступеней ушло анализатору: меньше всех ступеней, если прогон
    // остановлен посреди ступени или агент начал пропускать запросы
    public required int AnalyzedSteps { get; init; }
    // Итог для человека, в том числе почему проанализированы не все ступени
    public required string Message { get; init; }
}

// Ответ анализатора без деталей gRPC
public sealed record LimitResult(bool Found, double? LimitRps, uint? FailedStepIndex, IReadOnlyList<string> Reasons);

// Что отдать анализатору и как объяснить результат
public sealed class AnalysisPlan
{
    private AnalysisPlan(IReadOnlyList<StepResultDto> steps, bool aborted, StepResultDto? firstSkipped)
    {
        Steps = steps;
        Aborted = aborted;
        FirstSkipped = firstSkipped;
    }

    // Ступени до первой с пропусками: пропуски — ограничение генератора, а не сервиса.
    // Если отказ найден раньше — это честный предел; если нет — «не ниже X, дальше агент не успевал»
    public IReadOnlyList<StepResultDto> Steps { get; }

    public bool Aborted { get; }

    public StepResultDto? FirstSkipped { get; }

    // null — анализ не нужен: прогон failed или в нём нет ни одной полной ступени
    public static AnalysisPlan? For(RunStatus status, IReadOnlyList<StepResultDto> steps)
    {
        if (status is not (RunStatus.Completed or RunStatus.Aborted) || steps.Count == 0)
            return null;

        var cut = 0;
        while (cut < steps.Count && steps[cut].SkippedCount == 0)
            cut++;
        return new AnalysisPlan(
            steps.Take(cut).ToList(),
            status == RunStatus.Aborted,
            cut < steps.Count ? steps[cut] : null);
    }

    // Состояние сразу после завершения прогона
    public Analysis Initial() => Steps.Count == 0
        ? Undetermined($"агент не успевал подавать нагрузку уже на ступени {FirstSkipped!.Index}")
        : new Analysis { Status = AnalysisStatus.Pending, AnalyzedSteps = Steps.Count, Message = "Анализ выполняется" };

    public Analysis FromLimit(LimitResult limit)
    {
        string message;
        if (limit.Found)
        {
            var failed = Steps.FirstOrDefault(step => step.Index == limit.FailedStepIndex);
            var at = failed is null
                ? $"ступени {limit.FailedStepIndex}"
                : $"ступени {failed.Index} ({Format(failed.TargetRps)} запр/с)";
            message = limit.LimitRps is { } rps
                ? $"Предел: {Format(rps)} запр/с, отказ на {at}"
                : $"Предел ниже первой ступени: отказ уже на {at}";
        }
        else
        {
            var bound = limit.LimitRps is { } rps ? $"предел не ниже {Format(rps)} запр/с" : "предел не определён";
            message = FirstSkipped is null
                ? $"Отказа не было: {bound}"
                : $"Отказа не было до ступени {FirstSkipped.Index}: {bound}, дальше агент не успевал подавать нагрузку";
        }

        return new Analysis
        {
            Status = limit.Found ? AnalysisStatus.Found : AnalysisStatus.NotFound,
            LimitRps = limit.LimitRps,
            FailedStepIndex = limit.Found ? limit.FailedStepIndex : null,
            Reasons = limit.Reasons,
            AnalyzedSteps = Steps.Count,
            Message = Scope(message),
        };
    }

    public Analysis FromRpcError(StatusCode code, string detail) => code switch
    {
        StatusCode.FailedPrecondition => Undetermined(detail),
        StatusCode.InvalidArgument => Error($"анализатор отклонил данные: {detail}"),
        StatusCode.Unavailable => Error($"анализатор недоступен: {detail}"),
        StatusCode.DeadlineExceeded => Error("анализатор не ответил вовремя"),
        _ => Error($"ошибка анализатора ({code}): {detail}"),
    };

    public Analysis Error(string reason) => new()
    {
        Status = AnalysisStatus.Error,
        AnalyzedSteps = Steps.Count,
        Message = Scope($"Анализ не выполнен: {reason}"),
    };

    private Analysis Undetermined(string detail) => new()
    {
        Status = AnalysisStatus.Undetermined,
        AnalyzedSteps = Steps.Count,
        Message = Scope($"Предел не определён: агент не успевал подавать нагрузку или нет измерений ({detail})"),
    };

    private string Scope(string message) => Aborted
        ? $"Прогон остановлен, анализ только по завершённым ступеням. {message}"
        : message;

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
