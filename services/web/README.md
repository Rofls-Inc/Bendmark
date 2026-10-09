# web

Веб-интерфейс. TypeScript, React, Vite.

Первая версия трёх экранов по макетам `assets/mvp-*.webp`:

- **Сценарий** (`/`) — цель и ступени «от / до / шаг», план прогона. Отправляет сценарий в `POST /runs`, ошибки координатора показывает у полей формы.
- **Прогон** (`/runs/{id}`) — пока прогон не завершён, раз в 2 секунды опрашивает `GET /runs/{id}` и показывает пройденные ступени.
- **Отчёт** (тот же адрес, когда статус `completed`, `failed` или `aborted`) — предел, кривая производительности, причины отказа, таблица ступеней.

Координатор пока не отдаёт результаты ступеней (`steps`) и предел (`limit`): они появятся в #33 и #34. До этого страница прогона показывает пустое состояние, а отчёт целиком можно посмотреть на демо-прогоне `/runs/demo`. Он построен на копии `examples/result.json` (`src/demo/result.json`), предел взят из вывода анализатора на этом файле.

## Запуск

Нужен Node.js 22. Команды выполняются из `services/web`.

```bash
npm install
npm run dev
```

Интерфейс откроется на http://localhost:5173. Запросы к `/api/*` Vite передаёт координатору на `http://localhost:5080` (запуск координатора - в его README), поэтому CORS не нужен. Другой адрес координатора:

```bash
COORDINATOR_URL=http://localhost:5000 npm run dev
```

## Проверки

```bash
npm run lint
npm run typecheck
npm test
npm run build
```

## Docker

Образ собирает статику и раздаёт её через nginx, `/api/` nginx проксирует на `COORDINATOR_URL` (по умолчанию `http://coordinator:5080`, имя сервиса в Docker Compose).

```bash
docker build -t bendmark-web .
```

Сервис для `docker-compose.yml`:

```yaml
  web:
    build: services/web
    ports:
      - "127.0.0.1:5173:80"
    networks: [bendmark]
    depends_on:
      coordinator:
        condition: service_healthy
```
