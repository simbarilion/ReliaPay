# Runbook: диагностика и failpoint-цикл

Операционные примеры для локального контура ReliaPay. Доказательство результата — по сохранённым фактам (PostgreSQL / `diagnostics.trace` / метрики), а не по `sleep`. Ручной SQL/DML для «починки» сценария не используется.

Предпосылки: `docker compose up -d --build`, JWT с `scope`, покрывающим нужные policy (`payment:write`, `payment:read`, `workflow:read`, `diagnostics:read`). Том `course_data` не удалять (`down` без `-v`).

## diagnostics.trace

Возвращает согласованную проекцию цепочки по любому известному сквозному идентификатору. Policy: `diagnostics:read`. Idempotency не требуется.

```bash
curl -sS http://127.0.0.1:8080/api/diagnostics/trace \
  -H "Authorization: Bearer <jwt>" \
  -H "Content-Type: application/json" \
  -d "{\"identifier\":\"<uuid-or-external-request-id>\"}"
```

Поддерживаемые значения `identifier`: `correlationId`, `requestId`, `operationId`, `processId`, `stepInstanceId`, `jobId`, `executionId`, `attemptId`, `externalRequestId`, `messageId`, `decisionId`.

Успех: HTTP 200, `outcome=FOUND`. В `result` — связка operation / process / jobs / outbox / inbox / decisions по сохранённым данным. Один и тот же набор фактов для любого известного id цепочки; отличаются `query.identifier` и `query.matchedBy`.

Неизвестный identifier: HTTP 404, `code=diagnostics.trace_not_found`.

Trace не содержит JWT, HMAC, секреты, полные payload/body, текст callback `message` и manual `reason`.

## diagnostics.stalled

Выборка операций с **затянувшимся** ожиданием квитанции после исчерпания автоматических попыток доставки (Outbox `DEAD` при process в `WAITING_SIGNAL` и operation `PROCESSING`). Policy: `diagnostics:read`. Состояние не меняет.

**Правило возраста (авторитетно в PostgreSQL):** операция попадает в выборку только если `delivery.outbox.dead_at` уже старше порога относительно `clock_timestamp()`. Отсчёт начинается с момента перехода Outbox в `DEAD` (колонка `dead_at`). По умолчанию порог — **60 секунд** (`interval '60 seconds'`). Переопределение на сессии/роли runtime: GUC `app.diagnostics_stalled_after` (текст interval PostgreSQL, например `10 seconds`). Нулевой или отрицательный порог игнорируется и заменяется значением по умолчанию. Свежая `DEAD`-запись до границы в `items` не входит; после валидной квитанции (`CONFIRMED`) запись исчезает из выборки.

```bash
curl -sS http://127.0.0.1:8080/api/diagnostics/stalled \
  -H "Authorization: Bearer <jwt>" \
  -H "Content-Type: application/json" \
  -d "{}"
```

Успех: HTTP 200, `outcome=FOUND`, `result.items` — массив объектов `{ "operationId", "processId", "externalRequestId" }` без повторов, порядок по `operationId`. Пустая выборка: `{"items":[]}`.

До/после вызова предметные поля operation/process/outbox не должны меняться (только обычный action audit). Чтение не порождает повторный платёж и не меняет Outbox/Inbox.

## Цикл failpoint без ручного SQL

Failpoints работают **только** при `COURSE_TEST_PROFILE=1`. Иначе `COURSE_FAILPOINT` игнорируется. Активации через HTTP нет.

Шесть имён: `after_job_claim`, `after_action_before_finish`, `after_outbox_claim`, `after_provider_response`, `after_inbox_saved`, `after_manual_decision`.

Общий цикл:

1. Задать точку на целевом сервисе (пример — worker после claim):

```bash
# в .env или одноразово через окружение Compose
COURSE_TEST_PROFILE=1
COURSE_FAILPOINT=after_job_claim
```

```bash
docker compose up -d --no-build --force-recreate worker-a
```

Для Outbox-точек пересоздайте `outbox-dispatcher`; для API-точек — `api`. На время сценария остановите конкурирующий экземпляр (`worker-b` / `outbox-dispatcher-b`), чтобы работу взял целевой исполнитель.

2. Создайте нагрузку, которая дойдёт до точки (платёж, job, callback / `workflow.manual` — по выбранному имени).

3. Дождитесь JSON-ack в stdout (не `sleep` «на глаз»):

```bash
docker compose logs --no-color worker-a 2>&1 | findstr failpoint.reached
```

Linux / macOS / Git Bash:

```bash
docker compose logs --no-color worker-a 2>&1 | grep failpoint.reached
```

Ожидаемая строка:

```json
{"event":"failpoint.reached","name":"after_job_claim","instanceId":"worker-a"}
```

4. Остановите зависший контейнер:

```bash
docker compose stop worker-a
```

5. Снимите failpoint и поднимите сервис снова:

```bash
# COURSE_FAILPOINT=  (пусто) в .env
docker compose up -d --no-build --force-recreate worker-a
```

При необходимости снова запустите второй экземпляр (`worker-b` / `outbox-dispatcher-b`) для проверки reclaim/fencing.

6. **Докажите** исход фактами, а не паузой:

- `POST /api/diagnostics/trace` по `operationId` / `externalRequestId` / `correlationId`;
- при необходимости `POST /api/diagnostics/stalled`;
- OpenMetrics на api (см. README): `outbox_pending`, `workflow_jobs_ready`, `workflow_processes_waiting`;
- `POST /api/operation/get` / `POST /api/workflow/get`.

Инварианты после цикла: нет потери сообщения; нет второго provider payment; нет ложного предметного успеха (`DEAD` Outbox ≠ `REJECTED` / process `FAILED`). Ручной `UPDATE`/`DELETE` в PostgreSQL для «дожима» сценария не допускается.

Краткая карта точек:

| Имя | Где | Граница |
| --- | --- | --- |
| `after_job_claim` | worker | после commit lease, до invoke |
| `after_action_before_finish` | worker | после effect, до `finish_job`/commit (rollback при stop) |
| `after_outbox_claim` | outbox-dispatcher | после commit claim, до HTTP provider |
| `after_provider_response` | outbox-dispatcher | после ответа provider, до conditional succeed/fail |
| `after_inbox_saved` | api | после commit Inbox, до HTTP-ответа |
| `after_manual_decision` | api | до commit общей TX `workflow.manual` (rollback при stop) |

## Типовые отказы

### `workflow.lease_stale`

Stale `finish_job` / `fail_job`: чужой `lease_owner` или устаревший `leaseVersion` после reclaim. Цикл worker не останавливается; завершение игнорируется. В trace / `workflow/get` виден рост `leaseVersion` у нового владельца. Это fencing, а не поломка контура.

### Outbox `DEAD` vs process `FAILED`

| Ситуация | Outbox | Operation | Process |
| --- | --- | --- | --- |
| Исчерпаны автоматические попытки доставки | `DEAD` (+ `dead_at`, `last_error_code`) | остаётся `PROCESSING` | остаётся `WAITING_SIGNAL` |
| Terminal исчерпание action retries у job | — | по карте | job `DEAD`, process может стать `FAILED` |

`DEAD` доставки **не** означает domain reject и **не** переводит process в `FAILED`. Такие операции попадают в `diagnostics.stalled`. Поздняя валидная квитанция может перевести delivery в `CONFIRMED` и продолжить process.

### Lost response / late receipt

- **Lost response:** provider принял платёж, ответ dispatcher потерян. Повтор с тем же `externalRequestId` и телом не создаёт второй payment (`paymentCount=1` в аудите simulator). Delivery доводится succeed/fail или уходит в retry/`DEAD` по политике.
- **Late receipt:** валидный signed receipt после `DEAD` Outbox подтверждает delivery (`CONFIRMED`) и снимает ожидание signal. Смотреть связку в `diagnostics.trace`, не «чинить» вручную SQL.

### Provider outage и readiness

Недоступность provider **не** делает api / worker / dispatcher `not ready`. Outbox копит / ретраит delivery; после возврата provider работа продолжается из PostgreSQL. Ready api/worker/dispatcher зависит от PostgreSQL; ready adapter — от gateway.

### Быстрые проверки состояния

```bash
curl -sS http://127.0.0.1:8080/health/live
curl -sS http://127.0.0.1:8080/health/ready
docker compose exec -T api curl -fsS http://127.0.0.1:8080/metrics
docker compose ps
docker compose logs --tail=100 worker-a outbox-dispatcher api
```

Не используйте `docker compose down -v` на живом локальном томе `course_data`: это удалит PostgreSQL data directory. Для остановки достаточно `docker compose down` (том сохранится) или `stop` отдельных сервисов.

## Callback adapter

Provider шлёт legacy callback на `http://receipt-adapter:8082/...` (listen_port adapter из configuration.md, не 8080). `PROVIDER_CALLBACK_TOKEN` — JWT HS256 со scope `receipt:write` (тот же iss/aud/signing key, что у COURSE_JWT_*); opaque-строка даёт 401 на `receipt.accept`.
