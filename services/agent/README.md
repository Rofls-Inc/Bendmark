# load_agent

Генератор HTTP-нагрузки: шлёт GET-запросы с заданной частотой, замеряет задержки и пишет агрегированную статистику в JSON.

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
./load_agent https://example.com 100 45 --concurrency 64 --json result.json
```

## Опции

| Опция | По умолчанию | Описание |
|---|---|---|
| `--concurrency N` | `16` | Максимум одновременных запросов |
| `--timeout MS` | `5000` | Таймаут одного запроса, мс |
| `--output FILE` | stdout | Куда писать задержки (одна на строку, мс) |
| `--json FILE` | — | Куда писать агрегированную статистику |

## Вывод

**Задержки** — в stdout или в `--output`, по одной на строку, в мс с точностью до микросекунды (только успешные):

```
12.483
11.902
13.771
```

**Ошибки** — в stderr:

```
request failed: Couldn't resolve host name
```

**Пропуски** (если упёрлись в `--concurrency`) — в stderr:

```
[warn] skipped 22 of 25 planned requests; actual rate ~0.60 req/s (requested 5.00)
```

**JSON** (`--json result.json`):

```json
{
  "target_rps": 100,
  "duration_seconds": 45,
  "request_count": 4500,
  "throughput_rps": 100,
  "latency_ms": { "p50": 12, "p90": 18, "p99": 22 },
  "errors": { "count": 0, "rate_percent": 0 }
}
```

## Коды возврата

| Код | Значение |
|---|---|
| `0` | Хотя бы один запрос успешен |
| `1` | Ошибка аргументов или файла |
| `2` | Все запросы провалились |
| `3` | Ни одного запроса не отправлено |

## Выбор concurrency

`concurrency ≥ rate × средняя_задержка_в_секундах`, с запасом ×2–4.
