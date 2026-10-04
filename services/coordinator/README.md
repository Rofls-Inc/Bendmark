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

В v1 `target.method` должен быть ровно `GET`: другие методы и другой регистр
отклоняются с 400. Сценарий может содержать постоянную или убывающую нагрузку;
строгий рост нужен отдельно для поиска предела анализатором.

`GET /runs/{id}` - прогон целиком: сценарий, статус, время создания. Если прогона нет - `404`.

Статусы: `created`, `running`, `completed`, `failed`, `aborted`.

## Проверка через curl

Команды выполняются из `services/coordinator`. На Windows в PowerShell пишите `curl.exe` вместо `curl`.

```bash
# создать прогон из YAML -> 201 (именно --data-binary: -d выбрасывает переводы строк и ломает YAML)
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/yaml" --data-binary "@../../examples/scenario.yaml"

# статус прогона (id из ответа выше) -> 200
curl http://localhost:5080/runs/<id>

# несуществующий прогон -> 404
curl -i http://localhost:5080/runs/00000000-0000-0000-0000-000000000000

# некорректный сценарий в JSON -> 400 с ошибками по полям
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/json" -d "@examples/invalid-scenario.json"

# значение не того типа -> 400 с ошибкой поля steps[1].target_rps
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/json" -d "@examples/wrong-type-scenario.json"

# битый YAML -> 400
curl -i -X POST http://localhost:5080/runs -H "Content-Type: application/yaml" --data-binary "name: ["

# неподдерживаемый формат -> 415
curl -i -X POST http://localhost:5080/runs -H "Content-Type: text/plain" -d "hello"
```
