using Grpc.Core;

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

// Исполняет прогоны на агенте по одному и останавливает их по запросу.
// Конечный статус начатого прогона фиксирует только ExecuteAsync - так переход
// в конечный статус происходит один раз, а полученные ступени не теряются
public sealed class RunExecutor(RunStore store, IAgentRunner agent, ILogger<RunExecutor> logger)
{
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

        try
        {
            var run = store.TryStart(runId);
            if (run is null)
            {
                logger.LogInformation("Прогон {RunId} пропущен: остановлен, пока ждал в очереди", runId);
                return;
            }

            var (status, error) = await RunOnAgentAsync(run, callCancellation.Token, stoppingToken);
            store.TryFinish(runId, status, error);
            logger.LogInformation("Прогон {RunId} завершён со статусом {Status}", runId, status);
        }
        finally
        {
            lock (_gate)
                _active = null;
            active.MarkDone();
        }
    }

    private async Task<(RunStatus Status, string? Error)> RunOnAgentAsync(
        Run run, CancellationToken callToken, CancellationToken stoppingToken)
    {
        try
        {
            var expectedIndex = 1u;
            await foreach (var step in agent.RunAsync(run.Id, run.Scenario, callToken))
            {
                if (step.Index != expectedIndex)
                    throw new AgentProtocolException(
                        $"Агент прислал ступень {step.Index}, ожидалась {expectedIndex}");
                store.AddStep(run.Id, step);
                expectedIndex++;
            }
            return (RunStatus.Completed, null);
        }
        catch (RpcException e)
        {
            var outcome = Classify(e.StatusCode, e.Status.Detail,
                store.IsStopRequested(run.Id), stoppingToken.IsCancellationRequested);
            if (outcome.Status == RunStatus.Failed)
                logger.LogWarning(e, "Агент завершил прогон {RunId} с ошибкой {Code}", run.Id, e.StatusCode);
            return outcome;
        }
        catch (OperationCanceledException) when (callToken.IsCancellationRequested)
        {
            return (RunStatus.Aborted, stoppingToken.IsCancellationRequested ? "Координатор остановлен" : null);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Ошибка обмена с агентом в прогоне {RunId}", run.Id);
            return (RunStatus.Failed, $"Ошибка обмена с агентом: {e.Message}");
        }
    }

    // Таблица статусов из proto/README.md
    internal static (RunStatus Status, string? Error) Classify(
        StatusCode code, string detail, bool stopRequested, bool coordinatorStopping) => code switch
    {
        StatusCode.Cancelled when stopRequested => (RunStatus.Aborted, null),
        StatusCode.Cancelled when coordinatorStopping => (RunStatus.Aborted, "Координатор остановлен"),
        StatusCode.Cancelled => (RunStatus.Aborted, "Прогон отменён на стороне агента"),
        StatusCode.DeadlineExceeded => (RunStatus.Aborted, "Истёк срок вызова агента"),
        // Отступление от контракта: он предлагает повторить позже, а мы сразу ставим failed.
        // В v1 координатор один и запускает прогоны по одному, поэтому агент занят только
        // чужим прогоном - например, оставшимся после перезапуска координатора.
        // #48: повтор с паузой вместо немедленного failed (issue «Координатор: повтор при RESOURCE_EXHAUSTED»)
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
        if (active is not null)
        {
            var wasRunning = false;
            try
            {
                wasRunning = await agent.StopAsync(runId, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Не удалось вызвать Stop у агента для прогона {RunId}", runId);
            }

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

    private ActiveRun? GetActive(Guid runId)
    {
        lock (_gate)
            return _active?.RunId == runId ? _active : null;
    }

    private sealed class ActiveRun(Guid runId, CancellationTokenSource callCancellation)
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid RunId { get; } = runId;

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
