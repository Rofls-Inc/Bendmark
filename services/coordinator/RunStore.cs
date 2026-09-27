using System.Collections.Concurrent;

namespace Bendmark.Coordinator;

// Хранилище в памяти: после перезапуска всё пропадает (позже заменим)
public class RunStore
{
    private readonly ConcurrentDictionary<Guid, Run> _runs = new();

    public Run Create(Scenario scenario)
    {
        var run = new Run { Id = Guid.NewGuid(), Scenario = scenario };
        _runs[run.Id] = run;
        return run;
    }

    public Run? Get(Guid id) => _runs.TryGetValue(id, out var run) ? run : null;
}
