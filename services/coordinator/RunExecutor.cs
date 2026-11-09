using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Bendmark.Coordinator;

public enum StopOutcome
{
    NotFound,
    // Прогон был в конечном статусе ещё до запроса: 409, статус не меняется
    AlreadyFinished,
    // Остановка доведена до конца; в Run - итоговый статус (обычно aborted)
    Finished,
    // Агент ещё не освободился: прогон завершится позже
    Pending,
}

public readonly record struct StopResult(StopOutcome Outcome, Run? Run);

// Исполняет прогоны по одному на группе агентов и останавливает их по запросу.
// Конечный статус начатого прогона фиксирует только ExecuteAsync - так переход
// в конечный статус происходит один раз, а полученные ступени не теряются
public sealed class RunExecutor(
    RunStore store,
    AgentGroup agents,
    IAnalyzer analyzer,
    IOptions<AgentOptions> agentOptions,
    TimeProvider time,
    ILogger<RunExecutor> logger)
{
    private readonly AgentOptions _agentOptions = agentOptions.Value;

    // Время ответа POST /runs/{id}/stop не больше StopCallTimeout + FinishGrace + CancelGrace,
    // то есть Agent:RequestTimeoutSeconds + 10 с (15 с по умолчанию); см. README.

    // Сколько ждать конца потока Run после Stop. Если агент остановил прогон, поток
    // завершится сразу. Если ответил, что прогона нет, — либо он уже закончил и OK
    // вот-вот придёт, либо Run до агента ещё не дошёл, и тогда вызов отменяем сами
    private static readonly TimeSpan FinishGrace = TimeSpan.FromSeconds(3);

    // Сколько ждать после отмены вызова: отмена локальная, поток завершается сразу
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private ActiveRun? _active;

    public async Task ExecuteAsync(Guid runId, CancellationToken stoppingToken)
    {
        using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var active = new ActiveRun(runId, callCancellation);
        // Регистрируем до перехода в running: StopAsync всегда найдёт идущий прогон
        lock (_gate)
            _active = active;

        AnalysisPlan? plan = null;
        try
        {
            var run = store.TryStart(runId);
            if (run is null)
            {
                logger.LogInformation("Прогон {RunId} пропущен: остановлен, пока ждал в очереди", runId);
                return;
            }

            var (status, error) = await RunOnAgentsAsync(run, active, callCancellation.Token, stoppingToken);
            plan = AnalysisPlan.For(status, store.Get(runId)!.Steps);
            store.TryFinish(runId, status, error, plan?.Initial());
            logger.LogInformation("Прогон {RunId} завершён со статусом {Status}", runId, status);
        }
        finally
        {
            lock (_gate)
                _active = null;
            // Остановка ждёт только конца прогона, а не анализа
            active.MarkDone();
        }

        if (plan is { Steps.Count: > 0 })
            await AnalyzeAsync(runId, plan, stoppingToken);
    }

    // Статус прогона анализ не меняет: ошибка анализатора попадает только в analysis
    private async Task AnalyzeAsync(Guid runId, AnalysisPlan plan, CancellationToken stoppingToken)
    {
        Analysis analysis;
        try
        {
            var limit = await analyzer.FindLimitAsync(plan.Steps, stoppingToken);
            analysis = plan.FromLimit(limit);
        }
        catch (RpcException e)
        {
            analysis = plan.FromRpcError(e.StatusCode, e.Status.Detail);
            if (analysis.Status == AnalysisStatus.Error)
                logger.LogWarning(e, "Анализ прогона {RunId} не выполнен: {Code}", runId, e.StatusCode);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            analysis = plan.Error("координатор остановлен");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Ошибка анализа прогона {RunId}", runId);
            analysis = plan.Error(e.Message);
        }

        store.SetAnalysis(runId, analysis);
        logger.LogInformation("Анализ прогона {RunId}: {Status}", runId, analysis.Status);
    }

    // Вызывает Run у всех агентов группы, каждому - его долю нагрузки (#78). Если хотя бы один
    // агент занят (RESOURCE_EXHAUSTED) до нагрузки, остальным отменяет вызов и повторяет всю группу
    // после паузы, до Agent:BusyRetryCount раз; на время пауз прогон остаётся running без ступеней
    private async Task<(RunStatus Status, string? Error)> RunOnAgentsAsync(
        Run run, ActiveRun active, CancellationToken callToken, CancellationToken stoppingToken)
    {
        try
        {
            var attempts = _agentOptions.BusyRetryCount + 1;
            for (var attempt = 1; ; attempt++)
            {
                var failure = await RunAttemptAsync(run, callToken);
                if (failure is null)
                    return (RunStatus.Completed, null);

                // По контракту «занят» приходит до нагрузки. Если ступени уже были, повтор
                // задублировал бы их: такой ответ разбирается как обычная ошибка
                if (failure is not { Error: RpcException { StatusCode: StatusCode.ResourceExhausted }, BeforeLoad: true })
                    return Outcome(run.Id, failure, callToken, stoppingToken);

                var agentName = agents.Describe(failure.Agent);
                if (attempt == attempts)
                {
                    // Остановку запросили, пока шёл последний вызов: итог как на промежуточной
                    // попытке (там PauseAsync вернёт false), иначе он зависел бы от номера попытки
                    if (store.IsStopRequested(run.Id))
                    {
                        logger.LogInformation("Прогон {RunId} остановлен, агент был занят до последней попытки", run.Id);
                        return (RunStatus.Aborted, null);
                    }

                    logger.LogWarning("Агент занят: прогон {RunId}, {Agent}, попытка {Attempt} из {Attempts}, повторов больше нет",
                        run.Id, agentName, attempt, attempts);
                    return Outcome(run.Id, failure, callToken, stoppingToken);
                }

                logger.LogInformation(
                    "Агент занят: прогон {RunId}, {Agent}, попытка {Attempt} из {Attempts}, повтор через {Delay} с",
                    run.Id, agentName, attempt, attempts, _agentOptions.BusyRetryDelaySeconds);

                // Остановка во время паузы отменяет callToken: Task.Delay бросит OperationCanceledException
                if (!await active.PauseAsync(_agentOptions.BusyRetryDelay, time, callToken))
                {
                    logger.LogInformation("Прогон {RunId} остановлен до повтора вызова Run", run.Id);
                    return (RunStatus.Aborted, null);
                }
            }
        }
        catch (OperationCanceledException) when (callToken.IsCancellationRequested)
        {
            return (RunStatus.Aborted, stoppingToken.IsCancellationRequested ? CoordinatorStopped : null);
        }
    }

    // Первый агент, завершившийся не OK, и его ошибка. BeforeLoad - ни этот агент,
    // ни группа ещё не прислали ни одной ступени: «занят» можно повторить
    private sealed record AgentFailure(AgentEndpoint Agent, Exception Error, bool BeforeLoad);

    // Одна попытка: Run у всех агентов параллельно. Ступень k попадает в прогон, когда её
    // прислали все агенты. Первая ошибка любого агента отменяет вызовы остальных: их CANCELLED
    // после этой отмены итог не меняет. null - все агенты прислали все ступени и завершили OK
    private async Task<AgentFailure?> RunAttemptAsync(Run run, CancellationToken callToken)
    {
        var plannedSteps = run.Scenario.Steps ?? [];
        var agentScenario = ScenarioSplit.ForAgent(run.Scenario, agents.Count);
        using var groupCancellation = CancellationTokenSource.CreateLinkedTokenSource(callToken);

        var gate = new Lock();
        var received = agents.Agents.Select(_ => new List<StepResultDto>()).ToArray();
        var storedCount = 0;
        AgentFailure? failure = null;

        async Task PumpAsync(int position)
        {
            var agent = agents.Agents[position];
            var expectedIndex = 1u;
            try
            {
                await foreach (var step in agent.Runner.RunAsync(run.Id, agentScenario, groupCancellation.Token))
                {
                    if (step.Index != expectedIndex)
                        throw new AgentProtocolException(
                            $"Агент прислал ступень {step.Index}, ожидалась {expectedIndex}");
                    if (expectedIndex > plannedSteps.Count)
                        throw new AgentProtocolException(
                            $"Агент прислал лишнюю ступень {step.Index}, в сценарии {plannedSteps.Count} ступеней");
                    expectedIndex++;

                    lock (gate)
                    {
                        received[position].Add(step);
                        while (storedCount < plannedSteps.Count && received.All(steps => steps.Count > storedCount))
                        {
                            var parts = received.Select(steps => steps[storedCount]).ToList();
                            LogParts(run.Id, parts);
                            store.AddStep(run.Id, StepMerger.Merge(
                                (uint)(storedCount + 1), plannedSteps[storedCount], parts));
                            storedCount++;
                        }
                    }
                }

                var receivedStepCount = expectedIndex - 1;
                if (receivedStepCount != plannedSteps.Count)
                    throw new AgentProtocolException(
                        $"Агент завершил поток: получено {receivedStepCount} из {plannedSteps.Count} ступеней");
            }
            catch (Exception e)
            {
                bool first;
                lock (gate)
                {
                    first = failure is null;
                    if (first)
                        failure = new AgentFailure(agent, e, BeforeLoad: expectedIndex == 1);
                }

                // Вне замка: отмена может сразу выполнить продолжения других агентов
                if (first && agents.Count > 1)
                    groupCancellation.Cancel();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, agents.Count).Select(PumpAsync));
        // При отмене другой поток ещё может вернуть уже готовую ступень. Решаем,
        // можно ли повторить группу, только когда все потоки закончились.
        return failure is null ? null : failure with
        {
            BeforeLoad = failure.BeforeLoad && received.All(steps => steps.Count == 0),
        };
    }

    // Ступени отдельных агентов в базе не хранятся; лог показывает, какой агент выбивается
    private void LogParts(Guid runId, IReadOnlyList<StepResultDto> parts)
    {
        if (parts.Count == 1)
            return;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            logger.LogInformation(
                "Прогон {RunId}, ступень {Index}, {Agent}: запросов {Requests}, ошибок {Errors}, пропусков {Skipped}, p99 {P99} мс",
                runId, part.Index, agents.Describe(agents.Agents[i]), part.RequestCount, part.Errors.Count,
                part.SkippedCount, part.LatencyMs.P99);
        }
    }

    // Статус прогона по ошибке агента; текст ошибки - с указанием агента, если их несколько
    private (RunStatus Status, string? Error) Outcome(
        Guid runId, AgentFailure failure, CancellationToken callToken, CancellationToken stoppingToken)
    {
        switch (failure.Error)
        {
            case RpcException e:
                var (status, error) = Classify(e.StatusCode, e.Status.Detail,
                    store.IsStopRequested(runId), stoppingToken.IsCancellationRequested);
                if (status == RunStatus.Failed)
                    logger.LogWarning(e, "{Agent} завершил прогон {RunId} с ошибкой {Code}",
                        agents.Describe(failure.Agent), runId, e.StatusCode);
                // Остановка координатора относится ко всему прогону, а не к агенту
                return (status, error is null or CoordinatorStopped ? error : agents.WithAgent(failure.Agent, error));
            case OperationCanceledException when callToken.IsCancellationRequested:
                return (RunStatus.Aborted, stoppingToken.IsCancellationRequested ? CoordinatorStopped : null);
            default:
                logger.LogError(failure.Error, "Ошибка обмена с агентом в прогоне {RunId}: {Agent}",
                    runId, agents.Describe(failure.Agent));
                return (RunStatus.Failed, agents.WithAgent(failure.Agent, $"Ошибка обмена с агентом: {failure.Error.Message}"));
        }
    }

    private const string CoordinatorStopped = "Координатор остановлен";

    // Таблица статусов из proto/README.md
    internal static (RunStatus Status, string? Error) Classify(
        StatusCode code, string detail, bool stopRequested, bool coordinatorStopping) => code switch
    {
        StatusCode.Cancelled when stopRequested => (RunStatus.Aborted, null),
        StatusCode.Cancelled when coordinatorStopping => (RunStatus.Aborted, CoordinatorStopped),
        StatusCode.Cancelled => (RunStatus.Aborted, "Прогон отменён на стороне агента"),
        StatusCode.DeadlineExceeded => (RunStatus.Aborted, "Истёк срок вызова агента"),
        // Сюда RESOURCE_EXHAUSTED доходит только после всех повторов (Agent:BusyRetryCount),
        // см. RunOnAgentsAsync, или если агент прислал его посреди потока
        StatusCode.ResourceExhausted => (RunStatus.Failed, "Агент занят"),
        StatusCode.InvalidArgument => (RunStatus.Failed, $"Агент отклонил сценарий: {detail}"),
        StatusCode.Unavailable => (RunStatus.Failed, $"Агент недоступен: {detail}"),
        StatusCode.Internal => (RunStatus.Failed, $"Внутренняя ошибка агента: {detail}"),
        _ => (RunStatus.Failed, $"Ошибка агента ({code}): {detail}"),
    };

    public async Task<StopResult> StopAsync(Guid runId, CancellationToken cancellationToken)
    {
        switch (store.RequestStop(runId))
        {
            case StopRequestState.NotFound:
                return new StopResult(StopOutcome.NotFound, null);
            case StopRequestState.AlreadyFinished:
                return new StopResult(StopOutcome.AlreadyFinished, store.Get(runId));
            case StopRequestState.AbortedInQueue:
                logger.LogInformation("Прогон {RunId} остановлен в очереди", runId);
                return new StopResult(StopOutcome.Finished, store.Get(runId));
        }

        // Прогон идёт. Сам статус не меняем: его зафиксирует ExecuteAsync по исходу вызова Run
        var active = GetActive(runId);
        if (active is not null && active.RequestStop())
        {
            // Прогон ждал повтора после RESOURCE_EXHAUSTED: на агенте его нет, Stop не нужен.
            // Пауза уже прервана, ExecuteAsync сразу зафиксирует aborted
            await active.WaitAsync(CancelGrace, cancellationToken);
        }
        else if (active is not null)
        {
            // Stop у всех агентов сразу: время ответа то же, что с одним агентом
            var stops = await Task.WhenAll(agents.Agents.Select(a => StopOnAgentAsync(a, runId, cancellationToken)));
            var wasRunning = stops.Any(stopped => stopped);

            if (!await active.WaitAsync(FinishGrace, cancellationToken))
            {
                if (wasRunning)
                    logger.LogWarning("Агент остановил прогон {RunId}, но поток Run не завершился за {Grace}", runId, FinishGrace);
                active.Cancel();
                await active.WaitAsync(CancelGrace, cancellationToken);
            }
        }

        var run = store.Get(runId)!;
        return new StopResult(run.Status == RunStatus.Running ? StopOutcome.Pending : StopOutcome.Finished, run);
    }

    // true, если агент нашёл и остановил прогон. Ошибка вызова Stop не мешает остановке:
    // ExecuteAsync всё равно дождётся потока Run или отменит его
    private async Task<bool> StopOnAgentAsync(AgentEndpoint agent, Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            return await agent.Runner.StopAsync(runId, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Не удалось вызвать Stop у агента для прогона {RunId}: {Agent}",
                runId, agents.Describe(agent));
            return false;
        }
    }

    private ActiveRun? GetActive(Guid runId)
    {
        lock (_gate)
            return _active?.RunId == runId ? _active : null;
    }

    private sealed class ActiveRun(Guid runId, CancellationTokenSource callCancellation)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Пауза и запрос остановки меняются под одним замком: остановка либо застаёт паузу
        // и прерывает её, либо пауза не начнётся / после неё не будет нового вызова Run
        private readonly Lock _pauseGate = new();
        private bool _paused;
        private bool _stopRequested;

        public Guid RunId { get; } = runId;

        // Пауза перед повтором Run. false - остановку уже запросили, повторять не нужно.
        // Если остановку запросят во время паузы, она прервётся OperationCanceledException
        public async Task<bool> PauseAsync(TimeSpan delay, TimeProvider time, CancellationToken cancellationToken)
        {
            lock (_pauseGate)
            {
                if (_stopRequested)
                    return false;
                _paused = true;
            }

            await Task.Delay(delay, time, cancellationToken);

            lock (_pauseGate)
            {
                _paused = false;
                return !_stopRequested;
            }
        }

        // Отмечает запрос остановки. true - прогон стоял на паузе между попытками:
        // пауза прервана, агент этот прогон не исполняет
        public bool RequestStop()
        {
            bool paused;
            lock (_pauseGate)
            {
                _stopRequested = true;
                paused = _paused;
            }

            // Отмена вне замка: продолжение прогона может выполниться прямо в Cancel
            if (paused)
                Cancel();
            return paused;
        }

        public void MarkDone() => _done.TrySetResult();

        public void Cancel()
        {
            try
            {
                callCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Прогон уже завершился
            }
        }

        public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                await _done.Task.WaitAsync(timeout, cancellationToken);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }
}
