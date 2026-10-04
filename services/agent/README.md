# agent

Генератор HTTP-нагрузки: шлёт запросы с заданной частотой и замеряет время ответа каждого.

## Сборка

```bash
mkdir build && cd build
cmake .. -DCMAKE_BUILD_TYPE=Release
cmake --build . -j
```

Понадобятся: CMake ≥ 3.16, компилятор с C++17, libcurl с заголовками (`libcurl4-openssl-dev` на Debian/Ubuntu, `libcurl-devel` на Fedora, `curl` через brew на macOS).

## Docker

Из корня репозитория, вместе с ABStock в общей сети:

```bash
docker compose up -d --build abstock
docker compose run --rm agent http://abstock:8080/ 50 10
```

Образ собирается в два этапа: в первом есть компилятор, CMake и заголовки libcurl, во второй попадают только бинарник и `libcurl`.

## Запуск

```
./load_agent <URL> <rate_per_sec> <duration_sec> [options]
```

Пример:

```bash
./load_agent https://example.com 50 10 --concurrency 32 --output latencies.txt
```

## Аргументы

| Аргумент | Описание |
|---|---|
| `<URL>` | Целевой URL |
| `<rate_per_sec>` | Частота запросов в секунду (можно дробную) |
| `<duration_sec>` | Длительность теста в секундах |

## Опции

| Опция | По умолчанию | Описание |
|---|---|---|
| `--concurrency N` | `16` | Максимум одновременных запросов |
| `--timeout MS` | `5000` | Таймаут одного запроса, мс |
| `--output FILE` | stdout | Куда писать задержки |

Если `--concurrency` меньше, чем `rate × задержка`, часть запросов будет пропускаться — в stderr появится `[warn] skipped N ...`.

## Формат вывода

Одна задержка на строку, в миллисекундах с точностью до микросекунды:

```
12.483
11.902
13.771
```

Порядок строк — порядок завершения запросов. Если задан `--output`, результат пишется в файл, иначе — в stdout.
