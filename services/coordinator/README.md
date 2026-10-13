# coordinator

Координатор прогонов. C#, ASP.NET Core.

Принимает сценарий, ставит прогон в очередь, запускает его на агенте по gRPC,
после завершения просит анализатор найти предел и отдаёт статус, ступени и
предел. Пока прогоны хранятся в памяти и после перезапуска пропадают.

## Запуск

```bash
cd services/coordinator
dotnet run --urls http://localhost:5080
```

Или в Visual Studio профиль `http`.

Или вместе с остальными сервисами через Docker из корня репозитория: `docker compose up --build`.
Порт тот же, 5080. Проверка живости: `GET /health` отвечает `200 Healthy`.

Клиент gRPC генерируется при сборке из общих контрактов
[`proto/bendmark/v1/`](../../proto/bendmark/v1/) (пакет `Grpc.Tools`), поэтому
проект собирается только внутри репозитория. Для Docker контекст сборки -
корень репозитория, чтобы в него попал каталог `proto/`.

## Настройки

| Ключ | По умолчанию | Переменная окружения | Что задаёт |
| --- | --- | --- | --- |
| `Agent:Address` | `http://agent:50051` (в Development - `http://localhost:50051`) | `Agent__Address` | Адрес gRPC-сервера агента |
| `Agent:RequestTimeoutSeconds` | `5` | `Agent__RequestTimeoutSeconds` | Таймаут одного HTTP-запроса агента; нужен для срока вызова `Run` |
| `Analyzer:Address` | `http://analyzer:50052` (в Development - `http://localhost:50052`) | `Analyzer__Address` | Адрес gRPC-сервера анализатора |
| `Analyzer:TimeoutSeconds` | `10` | `Analyzer__TimeoutSeconds` | Срок вызова `FindLimit` |

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
| `RESOURCE_EXHAUSTED` | `failed` | `Агент занят`; повтора пока нет, см. ниже |
| `CANCELLED` после `POST /runs/{id}/stop` | `aborted` | - |
| `DEADLINE_EXCEEDED` | `aborted` | `Истёк срок вызова агента` |

Во всех случаях в `steps` только полные ступени: неполную агент не отправляет.
Если агент нарушил контракт (недостающие или лишние ступени, неверный порядок,
`NaN` в метриках), прогон становится `failed`.

## Поиск предела

После `completed` или `aborted` со ступенями координатор вызывает у анализатора
`FindLimit` ([`services/analyzer`](../analyzer/README.md)) с порогами по умолчанию.
Для `failed` и для `aborted` без ступеней анализа нет (`analysis: null`).
Остановка (`POST /runs/{id}/stop`) анализа не ждёт.

Анализатору уходят ступени **до первой с `skipped_count > 0`**: пропуски значат,
что не успевал агент, а не сервис. Если отказ найден раньше - это честный предел.
Если нет - «предел не ниже X, дальше агент не успевал». Если пропуски уже на первой
ступени, анализатор не вызывается, результат - `undetermined`.

Результат лежит в `analysis` прогона:

| `status` | Когда | Поля |
| --- | --- | --- |
| `pending` | Прогон завершён, анализатор ещё не ответил | - |
| `found` | Найден отказ | `limit_rps` - последняя ступень без отказа (`null`, если отказ на первой), `failed_step_index` (с единицы), `reasons` |
| `not_found` | Отказа не было | `limit_rps` - нижняя оценка предела |
| `undetermined` | Анализатор ответил `FAILED_PRECONDITION` (ступень без ответов или пропуски) или пропуски с первой ступени | - |
| `error` | Анализатор недоступен, отклонил данные или не ответил вовремя | - |

Во всех статусах есть `analyzed_steps` (сколько ступеней ушло анализатору) и
`message` - итог для человека. У остановленного прогона `message` начинается с
«Прогон остановлен, анализ только по завершённым ступеням». **Статус прогона
анализ не меняет**: ошибка анализатора видна только в `analysis`.

```json
"analysis": {
  "status": "found",
  "limit_rps": 350,
  "failed_step_index": 7,
  "reasons": ["p99 812 мс выше порога 500 мс", "ошибок 2.4 % при пороге 1 %"],
  "analyzed_steps": 7,
  "message": "Предел: 350 запр/с, отказ на ступени 7 (400 запр/с)"
}
```

## API

`POST /runs` - принимает сценарий в JSON (`Content-Type: application/json`)
или YAML (`Content-Type: application/yaml`), возвращает `201` с `id` и `status`
и заголовком `Location: /runs/{id}`.
Если сценарий некорректный - `400` со списком ошибок по полям.
Другой `Content-Type` - `415`.

В v1 `target.method` должен быть ровно `GET`: другие методы и другой регистр
отклоняются с 400. `target_rps` должен строго расти от ступени к ступени:
постоянная или убывающая нагрузка даёт 400 с ошибкой `steps[i].target_rps`
(анализатору для поиска предела нужна рампа).

Неизвестные поля, например опечатка `target_rsp`, тоже дают 400: ошибка
приходит по полному пути поля (`steps[1].target_rsp`) со списком допустимых имён.
Имена полей чувствительны к регистру, в JSON и YAML одинаково.

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
  "steps": [ { "index": 1, "target_rps": 100, … } ],
  "analysis": null
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
  Если прогон успел завершиться сам, в ответе его фактический статус. Если прогон
  не завершился за отведённое время - `202`, итог появится в `GET /runs/{id}`;
- прогон уже `completed`, `failed` или `aborted` - `409`, статус не меняется;
- прогона нет - `404`.

Ответ на `stop` приходит не позже чем через `Agent:RequestTimeoutSeconds` + 10 с
(15 с по умолчанию): до `Agent:RequestTimeoutSeconds` + 5 с на `Stop` у агента
(он ждёт незавершённые запросы), до 3 с на завершение потока `Run`, до 2 с после
отмены вызова, если агент не завершил поток сам. Обычно - сразу после остановки агента.

Если агент ответил `RESOURCE_EXHAUSTED` (занят другим прогоном), контракт предлагает
повторить позже. Координатор пока сразу ставит `failed` с ошибкой «Агент занят»:
в v1 он запускает прогоны по одному, и агент может быть занят только чужим прогоном,
например оставшимся после перезапуска координатора.

## Проверка через curl

Команды выполняются из `services/coordinator`. На Windows в PowerShell пишите `curl.exe` вместо `curl`.
Без запущенного агента прогон через пару секунд станет `failed` с ошибкой
«Агент недоступен» - это ожидаемо. Без запущенного анализатора прогон завершится,
но `analysis.status` будет `error`. Анализатор локально:
`python -m analyzer.server --listen 127.0.0.1:50052` из `services/analyzer`
(подготовка - в [его README](../analyzer/README.md#grpc-сервис)).

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
- Анализатор в фабрике тоже фейковый (`FakeAnalyzer`): тест задаёт его ответ или
  ошибку. `AnalysisTests` проверяют все статусы `analysis`, отсечение ступеней по
  пропускам и то, что ошибка анализатора не меняет статус прогона.
  `GrpcAnalyzerTests` - `GrpcAnalyzer` против сервера на `AnalyzerService.AnalyzerServiceBase`.

Тесты читают сценарии из общего [`testdata/scenarios/`](../../testdata/scenarios/)
и эталон формата [`testdata/results/limit-found.json`](../../testdata/results/limit-found.json);
при сборке они автоматически копируются в каталог вывода тестов. Описание каждого
случая - в [`testdata/README.md`](../../testdata/README.md). Тесты идут
последовательно: каждый поднимает свой хост.
