# web

Веб-интерфейс. TypeScript, React, Vite.

Первая версия трёх экранов по макетам `assets/mvp-*.webp`:

- **Сценарий** (`/`) — цель и ступени «от / до / шаг», план прогона. Отправляет сценарий в `POST /runs`, ошибки координатора показывает у полей формы.
- **Прогон** (`/runs/{id}`) — пока прогон не завершён, раз в 2 секунды опрашивает `GET /runs/{id}` и показывает пройденные ступени.
- **Отчёт** (тот же адрес, когда статус `completed`, `failed` или `aborted`) — предел, кривая производительности, причины отказа, таблица ступеней.

После завершения прогона координатор ещё какое-то время ищет предел (`analysis.status = pending`), поэтому страница продолжает опрос, пока анализатор не ответит. Отчёт без координатора и агента можно посмотреть на демо-прогоне `/runs/demo`. Он построен на копии `testdata/results/limit-found.json` (`src/demo/result.json`): образ собирается из `services/web` и не видит `testdata`, поэтому CI сверяет копию с оригиналом.

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

Сервис `web` уже есть в `docker-compose.yml` в корне репозитория: `docker compose up --build` поднимает весь стенд, интерфейс открывается на http://localhost:5173.
