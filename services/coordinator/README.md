# coordinator

Координатор прогонов. C#, ASP.NET Core.

Принимает сценарий, запускает прогон, отдаёт статус и результат.
Пока прогоны хранятся в памяти и после перезапуска пропадают.

## Запуск

```bash
cd services/coordinator
dotnet run --urls http://localhost:5080
```

Или в Visual Studio профиль `http`.

## API

`POST /runs` - принимает сценарий в JSON (`Content-Type: application/json`)
или YAML (`Content-Type: application/yaml`), возвращает `201` с `id` и `status`.
Если сценарий некорректный - `400` со списком ошибок по полям.
Другой `Content-Type` - `415`.

`GET /runs/{id}` - прогон целиком: сценарий, статус, время создания. Если прогона нет - `404`.

Статусы: `created`, `running`, `completed`, `failed`, `aborted`.

## Проверка через curl

Команды выполняются из `services/coordinator`. На Windows в PowerShell пишите `curl.exe` вместо `curl`.

```bash
# создать прогон из YAML -> 201 (именно --data-binary: -d выбрасывает переводы строк и ломает YAML)
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/yaml" --data-binary "@../../testdata/scenarios/ramp.yaml"

# статус прогона (id из ответа выше) -> 200
curl http://localhost:5080/runs/<id>

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

Проект `tests/Bendmark.Coordinator.Tests.csproj` проверяет API через
`WebApplicationFactory`. Он читает сценарии из общего
[`testdata/scenarios/`](../../testdata/scenarios/); при сборке они автоматически
копируются в каталог вывода тестов. Описание каждого случая — в
[`testdata/README.md`](../../testdata/README.md).

Это начальные проверки для задачи #29. Полное покрытие валидатора, строгий
разбор неизвестных полей и запрет убывающей нагрузки добавляются в #35.
