# load_agent

Генератор HTTP-нагрузки для Bendmark. Подаёт нагрузку ступенями, замеряет задержки, отдаёт результат координатору по gRPC или пишет в JSON при запуске из командной строки.

Работает в двух режимах:

- **gRPC-сервер** — по умолчанию, слушает `--listen`, принимает `Run` и `Stop` от координатора.
- **CLI** — для отладки и smoke-тестов: если первый аргумент — URL, агент проходит сценарий и печатает `result.json`.

Оба режима используют одно и то же ядро (`run_scenario`), одинаково измеряют задержки и считают метрики.

## Сборка

### Зависимости

- CMake ≥ 3.16
- C++17
- libcurl с заголовками
- Protobuf 3.21 и gRPC 1.51 (пакеты Ubuntu 24.04)
- pthreads

Установка на Ubuntu 24.04:

```bash
sudo apt install build-essential cmake \
                 libcurl4-openssl-dev \
                 libgrpc++-dev libgrpc-dev \
                 libprotobuf-dev protobuf-compiler protobuf-compiler-grpc
```

### Сборка

Из `services/agent/`:

```bash
mkdir build && cd build
cmake .. -DCMAKE_BUILD_TYPE=Release
cmake --build . -j
```

Протокольный код (`bendmark/v1/agent.pb.cc`, `agent.grpc.pb.cc`, `common.pb.cc`) генерируется в `build/generated/` во время сборки через `protoc` и `grpc_cpp_plugin`. Версия `protoc` совпадает с версией libprotobuf и libgrpc, потому что все они берутся из одной поставки — пакетов Ubuntu 24.04.

## gRPC-режим

### Запуск

```bash
./load_agent --listen 0.0.0.0:50051
```

Флаги:

| Флаг | По умолчанию | Описание |
|---|---|---|
| `--listen ADDR` | `0.0.0.0:50051` | Адрес и порт для gRPC-сервера |
| `--timeout MS` | `5000` | Таймаут одного запроса, мс |
| `--max-concurrency N` | `1000` | Потолок для авторазмера пула |
| `--concurrency N` | auto | Ручное переопределение числа воркеров (для отладки) |

Число воркеров агент считает сам на каждую ступень по формуле

```
W = min(ceil(target_rps × timeout_sec) + 1, max_concurrency)
```

Координатор число воркеров не передаёт.

### Контракт

Агент реализует `bendmark.v1.AgentService` из `proto/bendmark/v1/agent.proto`. Правила обмена и статусы описаны в `proto/README.md`.

**`Run(RunRequest) returns (stream RunResponse)`**

Проверки входа:

- `run_id` — валидный UUID;
- сценарий: непустое имя, URL `http://` или `https://`, непустой список ступеней, у каждой `target_rps` и `duration_seconds` — конечные числа больше нуля;
- метод — только `GET`.

Если что-то не так — `INVALID_ARGUMENT`, нагрузка не создаётся. Если агент уже занят другим прогоном — `RESOURCE_EXHAUSTED`. Если всё в порядке — на каждую **полную** ступень отправляется один `RunResponse` с её `StepResult`, затем `OK`.

**`Stop(StopRequest) returns (StopResponse)`**

Прекращает планирование новых запросов, обрывает незавершённые HTTP-запросы через `CURLOPT_XFERINFOFUNCTION` и ждёт освобождения воркеров. Отвечает `was_running=true`, если остановлен активный прогон, иначе `was_running=false`. Исходный `Run` завершается `CANCELLED`. Неполная ступень в стрим не отправляется.

Отмена вызова клиентом и истёкший срок `Run` дают ту же очистку: планировщик проверяет `ServerContext::IsCancelled()` в цикле тиков.

Полная ступень — та, что отработала всё окно планирования, в том числе с пропусками. Пропуски не делают ступень неполной. Ступень с `skipped_count > 0` **отправляется** и завершается `OK`; что с ней делать, решает координатор.

Гонка нормального завершения со `Stop` разрешается одним атомарным переходом состояния в `AgentCore`: либо `Run` заканчивается первым и возвращает `OK`, а `Stop` видит `was_running=false`, либо `Stop` выставляет флаг отмены первым и `Run` возвращает `CANCELLED`.

### Проверка через grpcurl

```bash
grpcurl -plaintext -d '{
  "run_id": "11111111-1111-1111-1111-111111111111",
  "scenario": {
    "name": "smoke",
    "target_url": "http://abstock:8080/",
    "method": "GET",
    "steps": [
      {"target_rps": 10, "duration_seconds": 2},
      {"target_rps": 20, "duration_seconds": 2}
    ]
  }
}' localhost:50051 bendmark.v1.AgentService/Run
```

Ожидаемо: два `RunResponse` с `index` 1 и 2, затем `OK`.

## CLI-режим

Если первый аргумент — URL, агент работает как обычная консольная программа.

```bash
./load_agent <URL> --steps RATE:DURATION[,RATE:DURATION...] [options]
./load_agent <URL> <rate_per_sec> <duration_sec> [options]
```

Примеры:

```bash
./load_agent http://abstock:8080/ --steps 100:45,200:45.5,300:30 --json result.json
./load_agent http://abstock:8080/ 100 45.5 --json result.json
```

Флаги CLI:

| Флаг | По умолчанию | Описание |
|---|---|---|
| `--steps LIST` | — | Ступени: `RATE:DURATION`, через запятую; длительность дробная |
| `--name NAME` | `cli` | Имя сценария в JSON |
| `--timeout MS` | `5000` | Таймаут одного запроса, мс |
| `--max-concurrency N` | `1000` | Потолок для авторазмера пула |
| `--concurrency N` | auto | Ручное переопределение числа воркеров (для отладки) |
| `--json FILE` | stdout | Куда писать `result.json` |

`RATE` и `DURATION` — конечные положительные числа. `inf`, `nan`, `0`, отрицательные и строки с «хвостом» (`10x`) отклоняются.

### Формат result.json

```json
{
  "scenario_name": "cli",
  "status": "completed",
  "steps": [
    {
      "index": 1,
      "target_rps": 100,
      "duration_seconds": 45,
      "request_count": 4500,
      "throughput_rps": 100,
      "latency_ms": { "p50": 12, "p90": 18, "p99": 22 },
      "errors": { "count": 0, "rate_percent": 0 },
      "skipped_count": 0
    }
  ]
}
```

| Поле | Описание |
|---|---|
| `scenario_name` | Имя сценария |
| `status` | `completed` — сценарий прошёл все ступени |
| `steps[].index` | Позиция ступени, с единицы |
| `steps[].target_rps` | Заданная частота, без потери точности |
| `steps[].duration_seconds` | Заданная длительность, дробная, без потери точности |
| `steps[].request_count` | Завершившиеся попытки: успехи, HTTP-ошибки, таймауты |
| `steps[].throughput_rps` | `request_count / duration_seconds` |
| `steps[].latency_ms.p50/p90/p99` | Перцентили задержки от запланированного момента |
| `steps[].errors.count` | HTTP 4xx/5xx, таймауты, транспортные ошибки |
| `steps[].errors.rate_percent` | Доля ошибок от `request_count`, до 1 знака |
| `steps[].skipped_count` | Тики, не отправленные из-за занятости воркеров |

В gRPC-режиме `RunResponse.step_result` — тот же `StepResult`, поле в поле. Формат JSON применяется только при CLI-запуске и при сборке итогового файла координатором.

## Семантика измерений

Задержка считается от **запланированного момента отправки** до ответа или таймаута, по всем завершившимся попыткам, включая неуспешные. Пропуски в задержки не входят.

Если все воркеры заняты в момент тика — запрос не отправляется, `skipped_count++`. Пропуск означает, что агент не смог подать заданную нагрузку; ступень с `skipped_count > 0` — признак ограничения генератора, а не сервиса. Анализатор отклонит такие ступени с `FAILED_PRECONDITION`.

## Коды возврата CLI

| Код | Значение |
|---|---|
| `0` | Сценарий выполнен |
| `1` | Ошибка использования: аргументы, `--steps`, неизвестный флаг |
| `2` | Некорректная конфигурация: `timeout` или `max-concurrency` ≤ 0 |
| `3` | Ошибка записи `result.json` |
| `4` | Ошибка исполнения сценария (не создались потоки и т. п.) |

## Тесты

```bash
cd build
ctest --output-on-failure
```

Наборы:

- **scheduler** — планировщик, пропуски, задержка от запланированного момента, пустой результат;
- **scenario** — индексы ступеней, дробная длительность, продолжение после ступени с пропусками, колбэк `on_step`;
- **percentiles** — nearest-rank: 1..100, n = 50, n = 1, пустой массив, крайние перцентили;
- **workers** — формула `W`, упор в `max_concurrency`, override;
- **grpc_run** — сценарий из двух ступеней даёт два `RunResponse` с `index` 1 и 2; ступень с пропусками отправляется;
- **grpc_stop** — `Stop` посреди ступени даёт `CANCELLED` и только полные ступени; повторный `Stop` возвращает `was_running=false`;
- **grpc_errors** — неверный метод → `INVALID_ARGUMENT`, второй одновременный `Run` → `RESOURCE_EXHAUSTED`, некорректный UUID в `Stop` → `INVALID_ARGUMENT`.

Тесты используют `FakeSender` — отправитель с настраиваемой задержкой, без сети. gRPC-тесты поднимают сервер на `localhost:0` (эфе-мерный порт) и общаются с ним через сгенерированный gRPC-клиент.

## Docker

Образ собирается **из корня репозитория**, потому что агенту нужны `proto/` для генерации кода:

```bash
docker build -f services/agent/Dockerfile -t bendmark-agent .
```

В `docker-compose.yml` агент поднимается вместе со всем стендом:

```bash
docker compose up --build
```

Порт `50051` пробрасывается на `127.0.0.1:50051`. Healthcheck — TCP-проверка порта. Smoke-шаг запускает `docker compose run --rm agent <URL>` — это CLI-режим, как раньше.

## Структура

```
services/agent/
├── CMakeLists.txt
├── Dockerfile
├── Dockerfile.dockerignore
├── include/
│   ├── load_agent.hpp        # run_step, run_scenario, compute_workers
│   ├── sender.hpp            # интерфейс отправителя
│   ├── stats.hpp             # StepConfig, StepResult, перцентили
│   └── agent_service.hpp     # AgentCore: состояние прогона, Run/Stop
├── src/
│   ├── load_agent.cpp        # планировщик, пул воркеров, JSON
│   ├── sender_http.cpp       # HTTP-отправитель с XFERINFOFUNCTION
│   ├── agent_service.cpp     # AgentCore: гонка Run/Stop, фабрика Sender
│   └── main.cpp              # CLI или gRPC-сервер по аргументам
└── tests/
    ├── CMakeLists.txt
    ├── fake_sender.hpp
    ├── test_util.hpp
    ├── test_scheduler.cpp
    ├── test_scenario.cpp
    ├── test_percentiles.cpp
    ├── test_workers.cpp
    ├── test_grpc_run.cpp
    ├── test_grpc_stop.cpp
    └── test_grpc_errors.cpp
```

`Sender` — интерфейс отправителя. Сейчас единственная реализация — `HttpSender`. Новый отправитель (например, WebSocket) реализует тот же интерфейс; контракт `send()` обязан быть потокобезопасным, `stop` — атомарный флаг, который реализация проверяет, чтобы прервать текущий запрос.

`AgentCore` — состояние одного прогона: активный `run_id`, флаг отмены, мьютекс для атомарного перехода `Idle ↔ Running`. `SenderFactory` в конструкторе создаёт новый `Sender` на каждый `Run`, чтобы прогоны не делили соединения.