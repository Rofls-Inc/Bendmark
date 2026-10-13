namespace Bendmark.Coordinator;

public enum StopRequestState
{
    NotFound,
    // Прогон ещё ждал в очереди и сразу стал aborted
    AbortedInQueue,
    // Прогон идёт: остановку должен довести до конца исполнитель
    Running,
    // Прогон уже в конечном статусе, ничего не меняется
    AlreadyFinished,
}

// Хранилище в памяти: после перезапуска всё пропадает (позже заменим).
// Все изменения идут под одной блокировкой; наружу отдаются неизменяемые снимки Run.
public sealed class RunStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _runs = new();

    private sealed class Entry(Run snapshot)
    {
        public Run Snapshot = snapshot;
        public bool StopRequested;
    }

    public Run Create(Scenario scenario)
    {
        var run = new Run { Id = Guid.NewGuid(), Scenario = scenario };
        lock (_gate)
            _runs.Add(run.Id, new Entry(run));
        return run;
    }

    public Run? Get(Guid id)
    {
        lock (_gate)
            return _runs.TryGetValue(id, out var entry) ? entry.Snapshot : null;
    }

    // created -> running. null, если прогона нет или он уже не в очереди (например, остановлен)
    public Run? TryStart(Guid id)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var entry) || entry.Snapshot.Status != RunStatus.Created)
                return null;

            entry.Snapshot = entry.Snapshot with
            {
                Status = RunStatus.Running,
                StartedAt = DateTimeOffset.UtcNow,
            };
            return entry.Snapshot;
        }
    }

    // Добавляет полную ступень к идущему прогону
    public bool AddStep(Guid id, StepResultDto step)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var entry) || entry.Snapshot.Status != RunStatus.Running)
                return false;

            entry.Snapshot = entry.Snapshot with { Steps = [.. entry.Snapshot.Steps, step] };
            return true;
        }
    }

    // running -> конечный статус. Срабатывает один раз: первый зафиксированный исход побеждает
    public bool TryFinish(Guid id, RunStatus status, string? error, Analysis? analysis = null)
    {
        if (status is RunStatus.Created or RunStatus.Running)
            throw new ArgumentOutOfRangeException(nameof(status), status, "Нужен конечный статус");

        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var entry) || entry.Snapshot.Status != RunStatus.Running)
                return false;

            entry.Snapshot = entry.Snapshot with
            {
                Status = status,
                Error = error,
                FinishedAt = DateTimeOffset.UtcNow,
                Analysis = analysis,
            };
            return true;
        }
    }

    // Результат анализа завершённого прогона
    public bool SetAnalysis(Guid id, Analysis analysis)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var entry)
                || entry.Snapshot.Status is RunStatus.Created or RunStatus.Running)
                return false;

            entry.Snapshot = entry.Snapshot with { Analysis = analysis };
            return true;
        }
    }

    public StopRequestState RequestStop(Guid id)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var entry))
                return StopRequestState.NotFound;

            switch (entry.Snapshot.Status)
            {
                case RunStatus.Created:
                    entry.StopRequested = true;
                    entry.Snapshot = entry.Snapshot with
                    {
                        Status = RunStatus.Aborted,
                        FinishedAt = DateTimeOffset.UtcNow,
                    };
                    return StopRequestState.AbortedInQueue;
                case RunStatus.Running:
                    entry.StopRequested = true;
                    return StopRequestState.Running;
                default:
                    return StopRequestState.AlreadyFinished;
            }
        }
    }

    public bool IsStopRequested(Guid id)
    {
        lock (_gate)
            return _runs.TryGetValue(id, out var entry) && entry.StopRequested;
    }
}
