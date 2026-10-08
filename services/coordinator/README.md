# coordinator

Координатор прогонов. C#, ASP.NET Core.

Принимает сценарий, ставит прогон в очередь, запускает его на агенте по gRPC
и отдаёт статус и результат. Пока прогоны хранятся в памяти и после
перезапуска пропадают.

## Запуск

```bash
cd services/coordinator
dotnet run --urls http://localhost:5080
```

Или в Visual Studio профиль `http`.

Клиент gRPC генерируется при сборке из общих контрактов
[`proto/bendmark/v1/`](../../proto/bendmark/v1/) (пакет `Grpc.Tools`), поэтому
проект собирается только внутри репозитория. Для Docker контекст сборки -
корень репозитория, чтобы в него попал каталог `proto/`.

## Настройки

| Ключ | По умолчанию | Переменная окружения | Что задаёт |
| --- | --- | --- | --- |
| `Agent:Address` | `http://agent:50051` (в Development - `http://localhost:50051`) | `Agent__Address` | Адрес gRPC-сервера агента |
| `Agent:RequestTimeoutSeconds` | `5` | `Agent__RequestTimeoutSeconds` | Таймаут одного HTTP-запроса агента; нужен для срока вызова `Run` |

Срок вызова `Run` = сумма `duration_seconds` всех ступеней
\+ число ступеней × `Agent:RequestTimeoutSeconds` + 30 с запаса.

## Как идёт прогон

1. `POST /runs` проверяет сценарий, создаёт прогон в статусе `created` и ставит его в очередь.
2. Фоновый обработчик берёт прогоны из очереди по одному (в v1 один агент и один
   прогон за раз), переводит в `running` и вызывает у агента `Run` со всем сценарием.
3. Агент присылает по одному результату на каждую полную ступень; координатор
   сразу добавляет их в `steps`.
4. По исходу вызова `Run` прогон получает конечный статус (таблица из
   [`proto/README.md`](../../proto/README.md)):

| Исход `Run` | Статус прогона | `error` |
| --- | --- | --- |
| `OK` | `completed` | - |
| `INVALID_ARGUMENT`, `INTERNAL`, `UNAVAILABLE` и прочие ошибки | `failed` | Текст ошибки; полученные ступени сохраняются как диагностика |
| `RESOURCE_EXHAUSTED` | `failed` | `агент занят` |
| `CANCELLED` после `POST /runs/{id}/stop` | `aborted` | - |
| `DEADLINE_EXCEEDED` | `aborted` | `Истёк срок вызова агента` |

Во всех случаях в `steps` только полные ступени: неполную агент не отправляет.
Если агент нарушил контракт (ступени не по порядку, `NaN` в метриках), прогон
становится `failed`.

## API

`POST /runs` - принимает сценарий в JSON (`Content-Type: application/json`)
или YAML (`Content-Type: application/yaml`), возвращает `201` с `id` и `status`
и заголовком `Location: /runs/{id}`.
Если сценарий некорректный - `400` со списком ошибок по полям.
Другой `Content-Type` - `415`.

В v1 `target.method` должен быть ровно `GET`: другие методы и другой регистр
отклоняются с 400. Сценарий может содержать постоянную или убывающую нагрузку;
строгий рост нужен отдельно для поиска предела анализатором.

`GET /runs/{id}` - прогон целиком, `404`, если его нет:

```json
{
  "id": "…",
  "status": "running",
  "created_at": "2026-10-08T10:00:00+00:00",
  "started_at": "2026-10-08T10:00:01+00:00",
  "finished_at": null,
  "error": null,
  "scenario": { "name": "abstock-home-ramp", "target": { … }, "steps": [ … ] },
  "steps": [ { "index": 1, "target_rps": 100, … } ]
}
```

Ступени в `steps` - в формате `result.json`: `index`, `target_rps`,
`duration_seconds`, `request_count`, `throughput_rps`, `latency_ms{p50,p90,p99}`,
`errors{count, rate_percent}`, `skipped_count`. Счётчики - целые числа,
`rate_percent` считается координатором из счётчиков и округляется до одного знака.

Статусы: `created`, `running`, `completed`, `failed`, `aborted`.

`GET /runs/{id}/result` - обёртка `result.json` `{scenario_name, status, steps}`
для прогона в статусе `completed` или `aborted`; для остальных - `409`,
для неизвестного - `404`. Её можно сохранить в файл и передать анализатору из
командной строки.

`POST /runs/{id}/stop` - остановка:

- прогон ждёт в очереди - сразу `aborted`, агент не вызывается; ответ `200` с прогоном;
- прогон идёт - координатор вызывает у агента `Stop`, дожидается завершения
  вызова `Run` и отвечает `200` с прогоном (`aborted`, только полные ступени).
  Если прогон успел завершиться сам, в ответе его фактический статус. Если агент
  не освободился за несколько секунд - `202`, итог появится в `GET /runs/{id}`;
- прогон уже `completed`, `failed` или `aborted` - `409`, статус не меняется;
- прогона нет - `404`.

## Проверка через curl

Команды выполняются из `services/coordinator`. На Windows в PowerShell пишите `curl.exe` вместо `curl`.
Без запущенного агента прогон через пару секунд станет `failed` с ошибкой
«Агент недоступен» - это ожидаемо.

```bash
# создать прогон из YAML -> 201 (именно --data-binary: -d выбрасывает переводы строк и ломает YAML)
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/yaml" --data-binary "@../../testdata/scenarios/ramp.yaml"

# статус прогона (id из ответа выше) -> 200
curl http://localhost:5080/runs/<id>

# остановить прогон -> 200 (или 409, если он уже завершён)
curl -i -X POST http://localhost:5080/runs/<id>/stop

# result.json завершённого или остановленного прогона -> 200, иначе 409
curl -o result.json http://localhost:5080/runs/<id>/result

# несуществующий прогон -> 404
curl -i http://localhost:5080/runs/00000000-0000-0000-0000-000000000000

# некорректный сценарий в JSON -> 400 с ошибками по полям
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/json" --data-binary "@../../testdata/scenarios/invalid-scenario.json"

# значение не того типа -> 400 с ошибкой поля steps[1].target_rps
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/json" --data-binary "@../../testdata/scenarios/wrong-type-scenario.json"

# битый YAML -> 400
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/yaml" --data-binary "@../../testdata/scenarios/malformed.yaml"

# неподдерживаемый формат -> 415
curl -i -X POST http://localhost:5080/runs -H "Content-Type: text/plain" -d "hello"
```

## Тесты

Из `services/coordinator`:

```bash
dotnet test Bendmark.Coordinator.slnx --configuration Release
```

Реальный агент тестам не нужен:

- API проверяется через `WebApplicationFactory`. Фабрика `CoordinatorFactory`
  подменяет `IAgentRunner` фейком, которым тест управляет вручную: отправить
  ступень, завершить поток, вернуть ошибку gRPC. Так проверяются статусы,
  остановка и формат ступеней (`RunLifecycleTests`, `ResultFormatTests`).
- `GrpcAgentRunnerTests` запускает `GrpcAgentRunner` против gRPC-сервера в памяти
  процесса на сгенерированном `AgentService.AgentServiceBase`. Тестовый проект
  генерирует серверные заготовки из тех же `.proto`; поэтому в основном проекте
  сгенерированные классы `internal`, иначе одноимённые типы конфликтовали бы.

Тесты читают сценарии из общего [`testdata/scenarios/`](../../testdata/scenarios/)
и эталон формата [`testdata/results/limit-found.json`](../../testdata/results/limit-found.json);
при сборке они автоматически копируются в каталог вывода тестов. Описание каждого
случая - в [`testdata/README.md`](../../testdata/README.md). Тесты идут
последовательно: каждый поднимает свой хост.

Полное покрытие валидатора, строгий разбор неизвестных полей и запрет убывающей
нагрузки добавляются в #35.
