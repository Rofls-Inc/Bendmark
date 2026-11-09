<div align="center">

<img src="assets/banner.webp" alt="Bendmark — находит нагрузку, при которой сервис ломается, и объясняет почему" width="100%">

<br>

![C++](https://img.shields.io/badge/C++-20-1F2225?style=flat-square&labelColor=101113)
![C#](https://img.shields.io/badge/C%23-.NET-1F2225?style=flat-square&labelColor=101113)
![Python](https://img.shields.io/badge/Python-3-1F2225?style=flat-square&labelColor=101113)
![Go](https://img.shields.io/badge/Go-прокси-1F2225?style=flat-square&labelColor=101113)
![TypeScript](https://img.shields.io/badge/TypeScript-React-1F2225?style=flat-square&labelColor=101113)
![статус](https://img.shields.io/badge/статус-в%20разработке-6B4F70?style=flat-square&labelColor=101113)

</div>

---

Bendmark — инструмент для нагрузочного тестирования. Он постепенно увеличивает нагрузку на сервис, находит момент, когда сервис перестаёт справляться, и пытается объяснить, почему это произошло.

k6, Gatling и Locust умеют подавать нагрузку и рисовать графики, но где сервис сломался и из-за чего, приходится разбираться самому. Мы хотим, чтобы это делал инструмент.

<div align="center">
<img src="assets/report.webp" alt="Макет отчёта по прогону" width="100%">
</div>

<sub>Это макет полной версии. Сейчас в интерфейсе упрощённые MVP-экраны, см. [«Экраны»](#экраны).</sub>

---

## Коротко

- **Что это:** инструмент нагрузочного тестирования, который ищет точку отказа сервиса и её причину
- **Зачем:** обычные генераторы нагрузки выдают графики, а предел и причину по ним ищут вручную
- **Для кого:** для разработчиков, которые отвечают за свой сервис и хотят знать, сколько он выдержит
- **Что получаем:** ответ вида «держит 350 запр/с, дальше не хватает соединений с базой» и графики, на которых это видно

---

## Как это работает

<div align="center">
<img src="assets/flow.webp" alt="Схема: ступени нагрузки, замер, кривая, предел, диагноз" width="100%">
</div>

1. Нагрузка растёт ступенями, например от 100 до 400 запросов в секунду с шагом 50.
2. На каждой ступени измеряем пропускную способность и задержку.
3. Когда пропускная перестаёт расти, а задержка резко идёт вверх, это и есть предел сервиса.
4. По признакам (ошибки, время ожидания, загрузка базы и т.д.) пытаемся понять причину.

Задержку считаем от момента, когда запрос *должен был* уйти, а не от момента, когда он реально ушёл. Иначе генератор тормозит вместе с сервисом и показывает задержку в разы меньше настоящей. Эта проблема называется coordinated omission.

Для экспериментов с неисправностями есть TCP-прокси: он добавляет задержку
и джиттер, отклоняет и обрывает соединения. В Compose он стоит перед HTTP-сервером
ABStock как `abstock-slow`. Управление из сценария и эксперименты с зависимостями
сервиса планируются отдельно; параметры задаются при запуске
[инжектора](services/injector/README.md).

---

## Примеры данных прогона

Для [задачи #11](https://github.com/Rofls-Inc/Bendmark/issues/11) подготовлены [сценарий](examples/scenario.yaml) и [результат](testdata/results/limit-found.json). Результат хранится в одном месте и используется в тестах; его числа придуманы для примера, а не получены из ABStock.

- `target.url` — адрес, доступный агенту. Имя `abstock` в примере предполагает общую Docker-сеть с тестовым сервисом.
- `target_rps` — плановая частота запросов в секунду для всех агентов вместе; `duration_seconds` — длительность ступени.
- `request_count` — число завершившихся попыток за ступень, включая неуспешные ответы и таймауты; `throughput_rps` = `request_count / duration_seconds`.
- `latency_ms.p50/p90/p99` — перцентили задержки в миллисекундах от запланированного момента отправки до ответа или таймаута.
- `errors.count` — ответы HTTP 4xx/5xx и таймауты; `errors.rate_percent` = `errors.count / request_count × 100`, округлённое до одного знака.

Общие данные для автоматических тестов находятся в [`testdata/`](testdata/README.md):
сценарии для координатора и результаты разных видов отказа для анализатора.
Тесты сервисов используют эти файлы; `examples/` содержит пример сценария.

---

## Экраны

Макеты, будут дорабатываться. Для каждого экрана есть полная версия и упрощённая (MVP). В спринте 2 сделаны MVP-экраны сценария, прогона и отчёта ([интерфейс](services/web/README.md)); экрана целей пока нет.

<table>
<tr>
<td width="50%">

<img src="assets/scenario.webp" alt="Сценарий">

**Сценарий** — куда и как подаём нагрузку

</td>
<td width="50%">

<img src="assets/live-run.webp" alt="Прогон">

**Прогон** — что происходит прямо сейчас

</td>
</tr>
<tr>
<td width="50%">

<img src="assets/report.webp" alt="Отчёт">

**Отчёт** — предел, причина и графики

</td>
<td width="50%">

<img src="assets/goals.webp" alt="Цели">

**Цели** — сервисы, по которым можно стрелять, с подтверждением владения

</td>
</tr>
</table>

<details>
<summary><b>Все состояния экранов (14)</b></summary>
<br>

<table>
<tr>
<td width="50%">

<img src="assets/goals-pending.webp" alt="Цель · ожидает проверки">

**Цель · ожидает проверки** — владелец добавляет DNS-запись

</td>
<td width="50%">

<img src="assets/goals-verified.webp" alt="Цель · подтверждена">

**Цель · подтверждена** — проверка через файл /.well-known

</td>
</tr>
<tr>
<td width="50%">

<img src="assets/goals-not-found.webp" alt="Цель · запись не найдена">

**Цель · запись не найдена** — что делать, если проверка не прошла

</td>
<td width="50%">

<img src="assets/scenario-new.webp" alt="Новый сценарий">

**Новый сценарий** — неподтверждённые цели выбрать нельзя

</td>
</tr>
<tr>
<td width="50%">

<img src="assets/scenario-validation.webp" alt="Ошибки в сценарии">

**Ошибки в сценарии** — красное блокирует запуск, жёлтое только предупреждает

</td>
<td width="50%">

<img src="assets/live-prepare.webp" alt="Прогон · подготовка">

**Прогон · подготовка** — агенты подключаются

</td>
</tr>
<tr>
<td width="50%">

<img src="assets/live-limit-found.webp" alt="Прогон · предел найден">

**Прогон · предел найден** — автостоп на второй провальной ступени

</td>
<td width="50%">

<img src="assets/live-stopped.webp" alt="Прогон · остановлен">

**Прогон · остановлен** — остановили вручную

</td>
</tr>
<tr>
<td width="50%">

<img src="assets/live-no-connection.webp" alt="Прогон · нет связи">

**Прогон · нет связи** — агент сам снимет нагрузку

</td>
<td width="50%">

<img src="assets/report-no-limit.webp" alt="Отчёт · предел не найден">

**Отчёт · предел не найден** — сервис выдержал все ступени

</td>
</tr>
<tr>
<td width="50%">

<img src="assets/report-aborted.webp" alt="Отчёт · прогон прерван">

**Отчёт · прогон прерван** — данные есть только до обрыва

</td>
<td width="50%">

<img src="assets/report-low-confidence.webp" alt="Отчёт · низкая уверенность">

**Отчёт · низкая уверенность** — признаки не сходятся

</td>
</tr>
<tr>
<td width="50%">

<img src="assets/report-no-metrics.webp" alt="Отчёт · без метрик сервиса">

**Отчёт · без метрик сервиса** — диагноз только по внешним признакам

</td>
<td width="50%">

<img src="assets/chart-expanded.webp" alt="Кривая производительности">

**Кривая производительности** — развёрнутый график

</td>
</tr>
</table>

</details>

<details>
<summary><b>MVP-версии (4)</b></summary>
<br>

<table>
<tr>
<td width="50%">

<img src="assets/mvp-scenario.webp" alt="Сценарий · MVP">

**Сценарий · MVP** — упрощённая версия

</td>
<td width="50%">

<img src="assets/mvp-live-run.webp" alt="Прогон · MVP">

**Прогон · MVP** — упрощённая версия

</td>
</tr>
<tr>
<td width="50%">

<img src="assets/mvp-report.webp" alt="Отчёт · MVP">

**Отчёт · MVP** — упрощённая версия

</td>
<td width="50%">

<img src="assets/mvp-goals.webp" alt="Цели · MVP">

**Цели · MVP** — упрощённая версия

</td>
</tr>
</table>

</details>

---

## Архитектура

Так мы видим проект в итоге, делать будем по частям.

<div align="center">
<img src="assets/arch.webp" alt="Сервисы Bendmark" width="100%">
</div>

- **Агент** подаёт нагрузку и собирает замеры. Агентов может быть несколько.
- **Координатор** запускает прогоны, раздаёт агентам задания и собирает результаты.
- **Анализатор** по результатам находит предел и причину.
- **Инжектор** портит сеть между сервисом и его зависимостями.
- **Интерфейс** — веб-приложение для всего этого.

---

## Стек

Каждый сервис пишем на своём языке. Так лучше, потому что задачи у сервисов разные: генератору нагрузки важна скорость, анализатору — математика, координатору — удобный веб-фреймворк. Язык подбираем под задачу. Заодно сервисы остаются независимыми: каждый можно писать, тестировать и перезапускать отдельно.

| Сервис | Язык | Почему |
| --- | --- | --- |
| Агент | C++ | Нужна скорость и точные замеры времени |
| Координатор | C# (ASP.NET Core) | Удобно писать API, язык знает вся команда |
| Анализатор | Python | numpy и scipy для работы с кривыми |
| Тестовый сервис | C# (Blazor) | Наш прошлый проект [ABStock](https://github.com/yarosvla/ABStock) — мультиагентная биржа. На нём проверяем инструмент |
| Инжектор | Go | Удобно писать сетевой прокси |
| Интерфейс | TypeScript (React) | |

Сервисы общаются через gRPC. Запускаем всё через Docker Compose.

Контракты для связи координатора с агентом и анализатором находятся в
[`proto/bendmark/v1/`](proto/bendmark/v1/). В [описании контрактов](proto/README.md)
зафиксированы поля, поведение `Run`/`Stop`/`FindLimit` и генерация кода для каждого
сервиса. Агент реализует по ним gRPC-сервер `AgentService`, анализатор - сервер
`AnalyzerService`, координатор - клиенты к обоим.

---

## Запуск

Нужен Docker с Compose v2. Из корня репозитория:

```bash
docker compose up --build
```

Первая сборка занимает несколько минут: ABStock собирается из [своего репозитория](https://github.com/yarosvla/ABStock).
`up` поднимает весь стенд: ABStock, инжектор `abstock-slow`, агент, анализатор, координатор и интерфейс.
Когда сервисы станут `healthy`, открываются:

| Сервис | Адрес |
| --- | --- |
| Интерфейс | http://localhost:5173 |
| Координатор (HTTP API) | http://localhost:5080 |
| ABStock | http://localhost:5062 |
| ABStock через инжектор | http://localhost:8081 |
| Агент (gRPC) | `localhost:50051` |
| Анализатор (gRPC) | `localhost:50052` |

### Прогон через интерфейс

1. Откройте http://localhost:5173. Форма уже заполнена сценарием по ABStock:
   `http://abstock:8080/`, от 100 до 400 запр/с с шагом 50, ступень 45 с — 7 ступеней, около 5 минут.
   Адрес цели указывается так, как его видит агент внутри сети Compose, поэтому `abstock:8080`, а не `localhost:5062`.
2. Нажмите «Запустить». Координатор отправляет сценарий агенту по gRPC, страница прогона
   раз в 2 секунды показывает пришедшие ступени: задержку p50/p90/p99 и пропускную.
   Прогон можно остановить кнопкой «Остановить».
3. После последней ступени координатор передаёт результат анализатору, и страница
   превращается в отчёт: предел, кривая производительности и причины отказа.
   Если сервис выдержал все ступени, вместо предела показывается нижняя оценка,
   например «≥ 400 запр/с». Отсюда же можно скачать `result.json`.

Для быстрой проверки уменьшите длительность ступени или число ступеней в форме.
Тот же прогон без интерфейса — запросами из [README координатора](services/coordinator/README.md#проверка-через-curl).

Чтобы проверить влияние неисправностей, укажите цель `http://abstock-slow:8081/`
вместо `http://abstock:8080/`. По умолчанию инжектор добавляет к каждому фрагменту
ответа задержку 600 мс и джиттер до 100 мс. Сравнение двух прогонов описано в
[README инжектора](services/injector/README.md#docker-compose-и-сравнение-прогонов).

Прогоны хранятся в SQLite в томе `coordinator-data` и переживают `docker compose restart coordinator` и `down`; список - `GET /runs` у координатора.

### Агент и анализатор из командной строки

Для отладки агент и анализатор можно запустить отдельно, без координатора:

```bash
# 50 запр/с в течение 10 с по ABStock внутри общей сети
docker compose run --rm agent http://abstock:8080/ 50 10
# анализатор по файлу result.json; путь относительно корня репозитория
docker compose run --rm analyzer-cli testdata/results/limit-found.json
```

Остановить всё: `docker compose down`. База ABStock и прогоны координатора остаются в томах `abstock-data` и `coordinator-data`, удалить их можно через `docker compose down -v`.

---

## Команда

- [@yarosvla](https://github.com/yarosvla)
- [@CronciUS](https://github.com/CronciUS)
- [@mikhailuci](https://github.com/mikhailuci)
- [@witcher141](https://github.com/witcher141)

Направления внутри команды меняются от спринта к спринту.

---

## Спринты и сроки

| Спринт | Срок | Цель |
| --- | --- | --- |
| [Спринт 1](https://github.com/Rofls-Inc/Bendmark/milestone/1) · `v0.1` | до 29.09.2026 | Описание проекта, макеты, стек, начало кода по каждому сервису |
| [Спринт 2](https://github.com/Rofls-Inc/Bendmark/milestone/2) · `v0.2` | до 13.10.2026 | Весь стенд поднимается `docker compose up`, прогон проходит целиком: интерфейс → координатор → агент → анализатор |
| [Спринт 3](https://github.com/Rofls-Inc/Bendmark/milestone/3) | до 27.10.2026 | Первая версия диагноза, наивный замер для сравнения, инжектор в стенде, история прогонов |
| Спринт 4 | до 10.11.2026 | Несколько агентов, сравнение прогонов, защита проекта |

Спринт длится две недели. В конце спринта `dev` вливается в `main` и ставится тег `v0.N`.

---

## Backlog задач

Все задачи — в [Issues](https://github.com/Rofls-Inc/Bendmark/issues).

**Спринт 1**

| # | Задача | Область |
| --- | --- | --- |
| [#10](https://github.com/Rofls-Inc/Bendmark/issues/10) | Паспорт проекта и настройка репозитория | docs |
| [#11](https://github.com/Rofls-Inc/Bendmark/issues/11) | Формат сценария и результата прогона | docs |
| [#12](https://github.com/Rofls-Inc/Bendmark/issues/12) | Агент: запросы с заданной частотой | agent |
| [#13](https://github.com/Rofls-Inc/Bendmark/issues/13) | Агент: перцентили и запись результата | agent |
| [#14](https://github.com/Rofls-Inc/Bendmark/issues/14) | Координатор: приём сценария | coordinator |
| [#15](https://github.com/Rofls-Inc/Bendmark/issues/15) | Анализатор: поиск предела | analyzer |
| [#16](https://github.com/Rofls-Inc/Bendmark/issues/16) | ABStock как тестовый сервис | target |
| [#17](https://github.com/Rofls-Inc/Bendmark/issues/17) | CI: сборка на каждый PR | infra |

**Спринт 2**

| # | Задача | Область |
| --- | --- | --- |
| [#28](https://github.com/Rofls-Inc/Bendmark/issues/28) | Доменная модель и правила сценария | docs |
| [#29](https://github.com/Rofls-Inc/Bendmark/issues/29) | Тестовые данные для всех сервисов | infra |
| [#30](https://github.com/Rofls-Inc/Bendmark/issues/30) | Контракты gRPC | infra |
| [#31](https://github.com/Rofls-Inc/Bendmark/issues/31) | Агент: честный замер задержки | agent |
| [#32](https://github.com/Rofls-Inc/Bendmark/issues/32) | Агент: ступени, перцентили и gRPC-сервер | agent |
| [#44](https://github.com/Rofls-Inc/Bendmark/issues/44) | Агент: результат ступеней по контракту | agent |
| [#45](https://github.com/Rofls-Inc/Bendmark/issues/45) | Агент: gRPC-сервер | agent |
| [#33](https://github.com/Rofls-Inc/Bendmark/issues/33) | Координатор: запуск прогона через агента | coordinator |
| [#35](https://github.com/Rofls-Inc/Bendmark/issues/35) | Координатор: тесты и строгий разбор сценария | coordinator |
| [#49](https://github.com/Rofls-Inc/Bendmark/issues/49) | Сборка координатора в Docker из корня репозитория | coordinator |
| [#34](https://github.com/Rofls-Inc/Bendmark/issues/34) | Анализатор как сервис | analyzer |
| [#36](https://github.com/Rofls-Inc/Bendmark/issues/36) | Docker Compose: запуск одной командой | infra |
| [#42](https://github.com/Rofls-Inc/Bendmark/issues/42) | Интерфейс: MVP экранов сценария, прогона и отчёта | ui |
| [#51](https://github.com/Rofls-Inc/Bendmark/issues/51) | Интерфейс: остановка, живой прогон и история | ui |

**Спринт 3**

| # | Задача | Область |
| --- | --- | --- |
| [#56](https://github.com/Rofls-Inc/Bendmark/issues/56) | Итоги спринта 2: README по факту и релиз v0.2 в main | docs |
| [#57](https://github.com/Rofls-Inc/Bendmark/issues/57) | Анализатор: диагноз причины v1 и предел после второго отказа | analyzer |
| [#58](https://github.com/Rofls-Inc/Bendmark/issues/58) | Агент: наивный замер для сравнения (closed model) | agent |
| [#63](https://github.com/Rofls-Inc/Bendmark/issues/63) | Сценарий: методы кроме GET, заголовки и тело запроса | agent |
| [#60](https://github.com/Rofls-Inc/Bendmark/issues/60) | Координатор: хранение прогонов и список GET /runs | coordinator |
| [#62](https://github.com/Rofls-Inc/Bendmark/issues/62) | Инжектор: джиттер, обрывы и запуск в Compose | infra |
| [#59](https://github.com/Rofls-Inc/Bendmark/issues/59) | CI: сквозной прогон через координатор в smoke-тесте | infra |
| [#61](https://github.com/Rofls-Inc/Bendmark/issues/61) | Интерфейс: доработать дизайн по макетам | ui |

**Дальше**

- **Нагрузка:** сценарий из нескольких запросов, несколько агентов
- **Анализ:** уверенность диагноза, диагноз по метрикам сервиса
- **Сервис:** повтор запуска, когда агент занят ([#48](https://github.com/Rofls-Inc/Bendmark/issues/48)), сравнение двух прогонов
- **Интерфейс:** цели с подтверждением владения, отчёт по ссылке

---

## Работа в команде

- Ветки: `main`, `dev`, `feature/*`
- В `dev` и `main` только через Pull Request с ревью
- Задачи вливаются в `dev`. В конце каждого спринта, после демо, `dev` вливается в `main` через Pull Request «Спринт N», а на `main` ставится тег `v0.N`. В `main` всегда лежит то, что показали на последнем демо
- Коммиты: `feat`, `fix`, `refactor`, `docs`, `chore`
- Задачи — в [Issues](https://github.com/Rofls-Inc/Bendmark/issues)

---

<sub>Учебный проект по программной инженерии, СПбГУ, 2026.</sub>
