using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

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

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    // Файл SQLite; относительный путь - от каталога запуска
    public string Path { get; set; } = "data/runs.db";
}

// Строка списка GET /runs: без сценария и ступеней
public sealed record RunSummary
{
    public required Guid Id { get; init; }
    public string? ScenarioName { get; init; }
    public required RunStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public AnalysisStatus? AnalysisStatus { get; init; }
    public double? LimitRps { get; init; }
}

public sealed record RunPage(IReadOnlyList<RunSummary> Runs, Guid? NextBefore);

// Прогоны в SQLite: переживают перезапуск координатора. Ступени пишутся по мере прихода.
// Все изменения идут под одной блокировкой; наружу отдаются неизменяемые снимки Run.
public sealed class RunStore
{
    public const string RestartedWhileRunning = "Координатор перезапущен во время прогона";
    public const string RestartedInQueue = "Координатор перезапущен, прогон не успел начаться";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly Lock _gate = new();
    private readonly string _connectionString;
    // Флаг остановки нужен только идущему прогону, после перезапуска таких нет
    private readonly HashSet<Guid> _stopRequested = [];

    public RunStore(IOptions<StorageOptions> options)
    {
        var path = Path.GetFullPath(options.Value.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS runs (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                started_at TEXT,
                finished_at TEXT,
                error TEXT,
                scenario_name TEXT,
                scenario TEXT NOT NULL,
                analysis TEXT
            );
            CREATE TABLE IF NOT EXISTS steps (
                run_id TEXT NOT NULL REFERENCES runs (id),
                position INTEGER NOT NULL,
                step TEXT NOT NULL,
                PRIMARY KEY (run_id, position)
            );
            """);
        RecoverAfterRestart(connection);
    }

    public Run Create(Scenario scenario)
    {
        var run = new Run { Id = Guid.NewGuid(), Scenario = scenario };
        lock (_gate)
        {
            using var connection = Open();
            Execute(connection, """
                INSERT INTO runs (id, status, created_at, scenario_name, scenario)
                VALUES ($id, $status, $created_at, $scenario_name, $scenario)
                """,
                ("$id", run.Id.ToString()),
                ("$status", Name(run.Status)),
                ("$created_at", Time(run.CreatedAt)),
                ("$scenario_name", scenario.Name),
                ("$scenario", JsonSerializer.Serialize(scenario, Json)));
        }
        return run;
    }

    public Run? Get(Guid id)
    {
        lock (_gate)
        {
            using var connection = Open();
            return Load(connection, id);
        }
    }

    // Список от новых к старым. before - id прогона, после которого продолжить;
    // null, если такого прогона нет
    public RunPage? List(int limit, Guid? before)
    {
        lock (_gate)
        {
            using var connection = Open();
            long? beforeSeq = null;
            if (before is { } beforeId)
            {
                beforeSeq = Scalar<long?>(connection, "SELECT seq FROM runs WHERE id = $id", ("$id", beforeId.ToString()));
                if (beforeSeq is null)
                    return null;
            }

            using var command = Command(connection, """
                SELECT id, scenario_name, status, created_at, started_at, finished_at, analysis
                FROM runs
                WHERE $before IS NULL OR seq < $before
                ORDER BY seq DESC
                LIMIT $limit
                """,
                ("$before", beforeSeq),
                ("$limit", limit + 1));
            using var reader = command.ExecuteReader();
            var runs = new List<RunSummary>();
            while (reader.Read())
            {
                var analysis = Deserialize<Analysis>(reader, 6);
                runs.Add(new RunSummary
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    ScenarioName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Status = ParseStatus(reader.GetString(2)),
                    CreatedAt = ParseTime(reader.GetString(3)),
                    StartedAt = OptionalTime(reader, 4),
                    FinishedAt = OptionalTime(reader, 5),
                    AnalysisStatus = analysis?.Status,
                    LimitRps = analysis?.LimitRps,
                });
            }

            // Лишняя строка означает, что есть следующая страница
            if (runs.Count <= limit)
                return new RunPage(runs, null);
            runs.RemoveAt(limit);
            return new RunPage(runs, runs[^1].Id);
        }
    }

    // created -> running. null, если прогона нет или он уже не в очереди (например, остановлен)
    public Run? TryStart(Guid id)
    {
        lock (_gate)
        {
            using var connection = Open();
            var changed = Execute(connection, """
                UPDATE runs SET status = $running, started_at = $now
                WHERE id = $id AND status = $created
                """,
                ("$id", id.ToString()),
                ("$running", Name(RunStatus.Running)),
                ("$created", Name(RunStatus.Created)),
                ("$now", Time(DateTimeOffset.UtcNow)));
            return changed == 1 ? Load(connection, id) : null;
        }
    }

    // Добавляет полную ступень к идущему прогону и сразу сохраняет её
    public bool AddStep(Guid id, StepResultDto step)
    {
        lock (_gate)
        {
            using var connection = Open();
            if (StatusOf(connection, id) != RunStatus.Running)
                return false;

            Execute(connection, """
                INSERT INTO steps (run_id, position, step)
                VALUES ($id, (SELECT COUNT(*) FROM steps WHERE run_id = $id), $step)
                """,
                ("$id", id.ToString()),
                ("$step", JsonSerializer.Serialize(step, Json)));
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
            using var connection = Open();
            var changed = Execute(connection, """
                UPDATE runs SET status = $status, error = $error, finished_at = $now, analysis = $analysis
                WHERE id = $id AND status = $running
                """,
                ("$id", id.ToString()),
                ("$status", Name(status)),
                ("$running", Name(RunStatus.Running)),
                ("$error", error),
                ("$now", Time(DateTimeOffset.UtcNow)),
                ("$analysis", Serialize(analysis)));
            if (changed == 1)
                _stopRequested.Remove(id);
            return changed == 1;
        }
    }

    // Результат анализа завершённого прогона
    public bool SetAnalysis(Guid id, Analysis analysis)
    {
        lock (_gate)
        {
            using var connection = Open();
            return Execute(connection, """
                UPDATE runs SET analysis = $analysis
                WHERE id = $id AND status NOT IN ($created, $running)
                """,
                ("$id", id.ToString()),
                ("$analysis", Serialize(analysis)),
                ("$created", Name(RunStatus.Created)),
                ("$running", Name(RunStatus.Running))) == 1;
        }
    }

    public StopRequestState RequestStop(Guid id)
    {
        lock (_gate)
        {
            using var connection = Open();
            switch (StatusOf(connection, id))
            {
                case null:
                    return StopRequestState.NotFound;
                case RunStatus.Created:
                    Execute(connection, "UPDATE runs SET status = $aborted, finished_at = $now WHERE id = $id",
                        ("$id", id.ToString()),
                        ("$aborted", Name(RunStatus.Aborted)),
                        ("$now", Time(DateTimeOffset.UtcNow)));
                    return StopRequestState.AbortedInQueue;
                case RunStatus.Running:
                    _stopRequested.Add(id);
                    return StopRequestState.Running;
                default:
                    return StopRequestState.AlreadyFinished;
            }
        }
    }

    public bool IsStopRequested(Guid id)
    {
        lock (_gate)
            return _stopRequested.Contains(id);
    }

    // Прогоны прошлого запуска, которые уже никто не доведёт до конца:
    // created и running становятся aborted, недождавшийся анализ - ошибкой анализа
    private static void RecoverAfterRestart(SqliteConnection connection)
    {
        var stale = new List<Guid>();
        using (var command = Command(connection, """
            SELECT id FROM runs
            WHERE status IN ($created, $running) OR json_extract(analysis, '$.status') = $pending
            """,
            ("$created", Name(RunStatus.Created)),
            ("$running", Name(RunStatus.Running)),
            ("$pending", Name(AnalysisStatus.Pending))))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                stale.Add(Guid.Parse(reader.GetString(0)));
        }

        foreach (var id in stale)
        {
            var run = Load(connection, id)!;
            var status = run.Status;
            var error = run.Error;
            var finishedAt = run.FinishedAt;
            var reason = "координатор перезапущен до ответа анализатора";
            if (status is RunStatus.Created or RunStatus.Running)
            {
                reason = "координатор перезапущен во время прогона";
                error = status == RunStatus.Created ? RestartedInQueue : RestartedWhileRunning;
                status = RunStatus.Aborted;
                finishedAt = DateTimeOffset.UtcNow;
            }

            var plan = AnalysisPlan.For(status, run.Steps);
            var analysis = plan is null ? null
                : plan.Steps.Count == 0 ? plan.Initial()
                : plan.Error(reason);

            Execute(connection, """
                UPDATE runs SET status = $status, error = $error, finished_at = $finished_at, analysis = $analysis
                WHERE id = $id
                """,
                ("$id", id.ToString()),
                ("$status", Name(status)),
                ("$error", error),
                ("$finished_at", finishedAt is { } at ? Time(at) : null),
                ("$analysis", Serialize(analysis)));
        }
    }

    private static Run? Load(SqliteConnection connection, Guid id)
    {
        Run run;
        using (var command = Command(connection, """
            SELECT status, created_at, started_at, finished_at, error, scenario, analysis
            FROM runs WHERE id = $id
            """, ("$id", id.ToString())))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            run = new Run
            {
                Id = id,
                Status = ParseStatus(reader.GetString(0)),
                CreatedAt = ParseTime(reader.GetString(1)),
                StartedAt = OptionalTime(reader, 2),
                FinishedAt = OptionalTime(reader, 3),
                Error = reader.IsDBNull(4) ? null : reader.GetString(4),
                Scenario = Deserialize<Scenario>(reader, 5)!,
                Analysis = Deserialize<Analysis>(reader, 6),
            };
        }

        var steps = new List<StepResultDto>();
        using (var command = Command(connection,
            "SELECT step FROM steps WHERE run_id = $id ORDER BY position", ("$id", id.ToString())))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                steps.Add(Deserialize<StepResultDto>(reader, 0)!);
        }

        return run with { Steps = steps };
    }

    private static RunStatus? StatusOf(SqliteConnection connection, Guid id) =>
        Scalar<string?>(connection, "SELECT status FROM runs WHERE id = $id", ("$id", id.ToString())) is { } status
            ? ParseStatus(status)
            : null;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteScalar() is { } value and not DBNull ? (T)value : default;
    }

    private static string Name<T>(T value) where T : Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static RunStatus ParseStatus(string status) =>
        Enum.Parse<RunStatus>(status.Replace("_", ""), ignoreCase: true);

    // ISO 8601 с часовым поясом: сохраняет DateTimeOffset без потерь
    private static string Time(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? OptionalTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTime(reader.GetString(ordinal));

    private static string? Serialize<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, Json);

    private static T? Deserialize<T>(SqliteDataReader reader, int ordinal) where T : class =>
        reader.IsDBNull(ordinal) ? null : JsonSerializer.Deserialize<T>(reader.GetString(ordinal), Json);
}
