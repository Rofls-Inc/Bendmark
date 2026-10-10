using System.Threading.Channels;

namespace Bendmark.Coordinator;

// Очередь прогонов на запуск. Читает её один RunWorker: в v1 один агент и один прогон за раз
public sealed class RunQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid runId)
    {
        if (!_channel.Writer.TryWrite(runId))
            throw new InvalidOperationException("Очередь прогонов закрыта");
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class RunWorker(RunQueue queue, RunExecutor executor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var runId in queue.ReadAllAsync(stoppingToken))
                await executor.ExecuteAsync(runId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Координатор останавливается
        }
    }
}
