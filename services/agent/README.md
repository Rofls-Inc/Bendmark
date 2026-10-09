# load_agent

Генератор HTTP-нагрузки: шлёт GET-запросы с заданной частотой, замеряет задержки и пишет результат ступени в JSON.

## Сборка

```bash
mkdir build && cd build
cmake .. -DCMAKE_BUILD_TYPE=Release
cmake --build . -j
```

Нужны: CMake ≥ 3.16, C++17, libcurl с заголовками (`libcurl4-openssl-dev` на Debian/Ubuntu).

## Запуск

```
./load_agent <URL> <rate_per_sec> <duration_sec> [options]
```

Пример:

```bash
./load_agent https://example.com 100 45.5 --concurrency 64 --json result.json
```

## Опции

| Опция | По умолчанию | Описание |
|---|---|---|
| `--concurrency N` | `16` | Число потоков-воркеров |
| `--timeout MS` | `5000` | Таймаут одного запроса, мс |
| `--json FILE` | — | Куда писать JSON (без флага — задержки в stdout) |

`<duration_sec>` — дробное число, например `45.5`.

## Что именно измеряется

Задержка считается **от запланированного момента отправки** до ответа или таймаута. По всем завершившимся попыткам, включая неуспешные и таймауты. Пропуски в задержки не входят.

Если все воркеры заняты — тик пропускается, `skipped_count++`, запрос не отправляется. Пропуск означает, что агент не смог подать заданную нагрузку; ступень с `skipped_count > 0` считается недостоверным замером.

## JSON

```json
{
  "index": 1,
  "target_rps": 100,
  "duration_seconds": 45.5,
  "request_count": 4550,
  "throughput_rps": 100,
  "latency_ms": { "p50": 12.345, "p90": 18.2, "p99": 22 },
  "errors": { "count": 0, "rate_percent": 0 },
  "skipped_count": 0
}
```

| Поле | Описание |
|---|---|
| `index` | Позиция ступени в сценарии, с единицы |
| `target_rps` | Заданная частота |
| `duration_seconds` | Заданная длительность, дробная |
| `request_count` | Завершившиеся попытки: успехи, HTTP-ошибки, таймауты |
| `throughput_rps` | `request_count / duration_seconds` |
| `latency_ms.p50/p90/p99` | Перцентили задержки от запланированного момента (nearest-rank) |
| `errors.count` | HTTP 4xx/5xx, таймауты, транспортные ошибки |
| `errors.rate_percent` | Доля ошибок от `request_count`, до 1 знака |
| `skipped_count` | Тики, не отправленные из-за занятости воркеров |

## Коды возврата

| Код | Значение |
|---|---|
| `0` | Хотя бы один запрос успешен |
| `1` | Ошибка аргументов или файла |
| `2` | Все запросы провалились |
| `3` | Ни одного запроса не отправлено (в том числе если пропущены все) |

## Структура проекта

```
load_agent/
├── CMakeLists.txt
├── include/
│   ├── load_agent.hpp
│   └── sender.hpp
└── src/
    ├── main.cpp
    ├── load_agent.cpp
    └── sender_http.cpp
```

`Sender` — интерфейс отправителя. Сейчас единственная реализация — `HttpSender`. WebSocket-отправитель добавляется в следующем спринте, интерфейс уже готов.