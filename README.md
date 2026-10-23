# ReliaPay

> Итоговый проект backend-стажировки **ModulDev (Модульбанк)**, 17.08–05.10.2026.  
> Диплом адаптационного курса «Backend разработка банковских систем» (05.10.2026, Москва).  
> Автор: [simbarilion](https://github.com/simbarilion).

Универсальный API Gateway и runtime для публикации PostgreSQL-функций как HTTP-actions и исполнения версионированных workflow-карт.

Сервис принимает клиентский запрос на единственном хост-порту 8080, аутентифицирует вызывающего по JWT HS256, резолвит действие из каталога в PostgreSQL и исполняет зарегистрированную функцию внутри одной транзакции. Клиент выбирает только `module`, `action` и JSON-payload. Имя схемы, имя функции и SQL ему недоступны.

Предметная логика живёт в PostgreSQL: платёжные операции и персистентное workflow-ядро. C# не содержит отдельных контроллеров на каждый action. Новый зарегистрированный action и новая карта вызываются без пересборки образов gateway, api и worker.


## Цели надёжности

Два проверяемых контура одной платформы:

1. **Пережить** — контролируемый сбой (failpoint / stop контейнера / outage provider) и самовосстановление без ручного SQL: Worker, Dispatcher и Reconciler продолжают работу из состояния Progress / Outbox / Inbox в PostgreSQL.
2. **Доказать** — исход операции подтверждается сохранёнными фактами: проекция `diagnostics.trace`, выборка `diagnostics.stalled`, OpenMetrics на api и предметные статусы. Пауза (`sleep`) сама по себе не является доказательством.

Операционные примеры вызовов и failpoint-цикла — в [docs/runbook.md](docs/runbook.md).

## Возможности и ограничения

### Возможности

- единственный внешний вход `http://localhost:8080` (`http://127.0.0.1:8080` — тот же шлюз);
- тонкий gateway: whitelist маршрутов и прокси на внутренний api;
- generic runtime: JWT, JSON Schema, каталог действий, `api.invoke`;
- публикация actions манифестом через Course CLI без правки C#;
- generic-идемпотентность в `api.invoke` до вызова target: replay того же payload и `409` при конфликте;
- единый HTTP-конверт ошибок и успехов;
- OpenAPI из каталога: default-документ и документ точной версии;
- `correlationId` на каждый запрос и трассировка gateway → api;
- liveness шлюза и readiness по готовности PostgreSQL и инициализации;
- автоматическое применение checksum-миграций при `docker compose up`;
- два экземпляра одного generic worker с lease/fencing и failpoints;
- dual Outbox dispatcher / Inbox reconciler (`*-b`) с разными `OUTBOX_OWNER` и fencing по owner/`leaseVersion`;
- OpenMetrics series очередей на api и actions `diagnostics.trace` / `diagnostics.stalled`.

### Ограничения

Сервис:

- не отдаёт клиенту выбор БД, schema, функции или SQL;
- не хранит каталог действий в коде C#;
- не дублирует идемпотентность блокировкой процесса;
- не пишет JWT, секреты, сырой Idempotency-Key и полный payload в логи;
- не публикует на хост порты api, cli, worker, Python-периметра, provider и PostgreSQL;
- не реализует BPMN и не переносит выбор flow/limit/final status в Python.

Интерактивный Swagger UI не публикуется. Контрактные документы:

http://localhost:8080/openapi/default.json

## Решение

Ссылки на архитектурные решения:

- [C4: контейнеры](docs/c4-containers.md)
- [ADR-0001. Граница доверия](docs/adr/0001-trust-boundary.md)
- [ADR-0002. Технический и предметный результат](docs/adr/0002-technical-vs-domain-result.md)
- [ADR-0003. Worker, lease и одна транзакция invoke+finish](docs/adr/0003-worker-lease-transaction.md)
- [ADR-0004. Антикоррупционный слой Python и ACL доставки](docs/adr/0004-python-anti-corruption.md)
- [Справочник контракта course-1](docs/contract-reference.md)
- [Runbook: diagnostics и failpoint-цикл](docs/runbook.md)


### Архитектура

ReliaPay — это Database-first runtime, а не набор предметных REST-контроллеров и не BPMN-движок. Gateway реализуется один раз. Карты и actions публикуются в PostgreSQL; worker исполняет только то, что закреплено в версии карты.

Контур доставки платежа (кратко): **API → Progress → Outbox → Dispatcher → Adapter**. HTTP-action фиксирует предметное состояние в Progress и ставит запись в Outbox; dispatcher забирает delivery (claim → commit → HTTP provider), adapter принимает legacy callback и передаёт signed receipt обратно через gateway в Inbox/workflow. Параллельно работают два dispatcher и два reconciler; завершение delivery условно по `lease_owner` + `leaseVersion` (fencing), чтобы lost response и dual-instance не создавали второй provider payment.

Запрос проходит следующую последовательность этапов:

```
    Клиент
    Authorization: Bearer <jwt>
    POST /api/{module}/{action}
            │
            ▼
    gateway
    whitelist + X-Correlation-Id
            │
            ├── путь вне whitelist            → 400 request.invalid
            └── api не готов                  → 503 на /health/ready
            │
            ▼
    api
    JWT HS256 (iss/aud/key, типы claims)
            │
            ├── нет или битый JWT             → 401 auth.invalid
            └── нет required_policy           → 403 access.denied
            │
            ▼
    catalog  api.actions
    default или X-Action-Version
            │
            ├── неизвестен / выключен         → 404 action.not_found
            └── нет Idempotency-Key           → 400 idempotency.required
            │
            ▼
    JSON Schema  request_schema
            │
            └── payload не совпал             → 422 payload.invalid
            │
            ▼
    одна транзакция Npgsql
    SELECT api.invoke(...)
            │
            │  PostgreSQL
            │  policy, generic idempotency, target
            ▼
    commit только если status=ok,
    outcome ∈ manifest, result валиден
            │
            ├── повтор того же ключа          → 200, тот же result
            ├── ключ + другой payload         → 409 idempotency.conflict
            └── неизвестный outcome/result    → 500, rollback
            │
            ▼
    200 OK
    {
      "status": "ok",
      "outcome": "CREATED",
      "result": { ... },
      "meta": { "correlationId": "...", "actionVersion": 1 }
    }
```

Compose-контур:

```
    gateway :8080
        │
        ▼
    api :8080  (только внутренняя сеть)
        │
        ▼
    postgres  БД course, volume course_data

    cli  ──publication──► postgres
         миграции, catalog и карты

    worker-a ──workflow_worker──► postgres
    worker-b ──тот же image──► postgres
         claim / api.invoke / finish / fail

    outbox-dispatcher   ──outbox_dispatcher──► postgres
    outbox-dispatcher-b ──тот же image, OUTBOX_OWNER_B──► postgres
    inbox-reconciler    ──inbox_reconciler──► postgres
    inbox-reconciler-b  ──тот же image──► postgres
    receipt-adapter ──HTTP──► gateway  (без credentials БД; ready от gateway)
    provider-simulator ──callback──► receipt-adapter
         (provider НЕ dependency ready для api/worker/dispatcher)
```

Слои C#:

```
    HTTP-хосты
        ReliaPay.Gateway
        ReliaPay.Api
        ReliaPay.Cli
        ReliaPay.Worker
            │
            ▼
    Runtime
        ReliaPay.Execution
        ReliaPay.Catalog
        ReliaPay.OpenApi
        ReliaPay.Security
        ReliaPay.Json
            │
            ▼
    Инфраструктура
        ReliaPay.Data          Npgsql по ролям
        ReliaPay.Observability tracing без секретов
        ReliaPay.Contracts     конверты и коды ошибок
```

`ReliaPay.Execution` координирует HTTP-action и invoke для worker:

- резолвит манифест из catalog (HTTP) или из payload `claim_jobs` (worker);
- проверяет policy и request schema;
- вызывает `api.invoke` на переданной транзакции;
- HTTP коммитит сам; worker коммитит только вместе с `finish_job`;
- мапит ошибки в HTTP-матрицу.

PostgreSQL хранит каталог, идемпотентность, operations, карты, process/job и insert-only events.  
Gateway PostgreSQL не использует.

Роли БД:

| Роль | Login | Назначение |
| --- | --- | --- |
| `course_owner` | нет | владелец SECURITY DEFINER-функций |
| `course_publication` | да | миграции, catalog и карты |
| `course_runtime` | да | `api.invoke` и чтение catalog/views |
| `workflow_worker` | да | `claim_jobs`, `api.invoke`, `finish_job`, `fail_job` |
| `outbox_dispatcher` | да | только `delivery.claim_outbox` / `succeed_outbox` / `fail_outbox` |
| `inbox_reconciler` | да | только `delivery.reconcile_inbox` |

### Запуск

На хосте нужны Docker Compose v2, bash и curl.

1. Скопируйте шаблон окружения. Файл `.env` в Git не хранится.

Windows (cmd):

```
copy .env.example .env
```

Linux / macOS / Git Bash / WSL:

```
cp .env.example .env
```

Значения из шаблона достаточны для локального запуска: пароли непустые, ключ JWT не короче 32 байт.

2. Из корня репозитория:

```
docker compose up -d --build
```

Готовность: http://localhost:8080/health/ready возвращает 200.

Также доступны:

- http://localhost:8080/health/live
- http://localhost:8080/openapi/default.json
- http://127.0.0.1:8080/health/ready

Том PostgreSQL — named volume `course_data`. Обычная остановка: `docker compose down` или `docker compose stop` — том **сохраняется**, cold start после этого поднимает прежнее состояние.

**Не** используйте `docker compose down -v` на живом локальном контуре: флаг `-v` удалит data directory и все операции/Outbox. Для «чистого» эксперимента поднимите отдельный Compose project (как делает `./check.sh`), а не сносите volume основного `reliapay`.

На хост опубликован только порт `8080` на `127.0.0.1`. Порты PostgreSQL, api, cli, worker, Python-периметра и provider наружу не пробрасываются. Внутри сети Compose используются адреса `postgres:5432` и `http://api:8080`.

### Конфигурация

Шаблон — [`.env.example`](.env.example). Ниже — существенные переменные локального контура.

| Переменная | Назначение |
| --- | --- |
| `COURSE_GATEWAY_PORT` | Host-порт gateway, по умолчанию `8080` |
| `COURSE_JWT_ISSUER` | Ожидаемый `iss`; в Compose нет default |
| `COURSE_JWT_AUDIENCE` | Ожидаемый `aud`; в Compose нет default |
| `COURSE_JWT_SIGNING_KEY` | Секрет HS256, не короче 32 байт; в Compose нет default |
| `POSTGRES_DB` | Имя БД, по умолчанию `course` |
| `POSTGRES_USER` | Bootstrap-пользователь образа PostgreSQL |
| `COURSE_POSTGRES_PASSWORD` | Пароль bootstrap-пользователя (local-only placeholder в Compose) |
| `COURSE_MIGRATOR_PASSWORD` | Пароль migrator; задаётся postgres и cli |
| `COURSE_PUBLISHER_PASSWORD` | Пароль publication-роли; cli читает его как `COURSE_PUBLICATION_PASSWORD` |
| `COURSE_RUNTIME_PASSWORD` | Пароль runtime-роли |
| `COURSE_WORKER_PASSWORD` | Пароль роли worker |
| `COURSE_OUTBOX_PASSWORD` | Пароль роли `outbox_dispatcher` |
| `COURSE_INBOX_PASSWORD` | Пароль роли `inbox_reconciler` |
| `COURSE_WORKER_INSTANCE_ID` | Владелец lease: `worker-a` или `worker-b` |
| `COURSE_FAILPOINT` | Одна точка зависания (см. [runbook](docs/runbook.md)); пусто вне failpoint-сценария; вне test profile игнорируется |
| `COURSE_TEST_PROFILE` | `1` — короткие интервалы и разрешение failpoints; в tracked Compose default `1` |
| `COURSE_PROVIDER_TIMEOUT_MS` | Timeout HTTP provider у dispatcher (test profile: `500`) |
| `COURSE_OUTBOX_MAX_ATTEMPTS` | Bounded attempts доставки, включая первую (test: `4`); reclaim на потолке → `DEAD` без HTTP |
| `COURSE_OUTBOX_BACKOFF_BASE_MS` / `_MAX_MS` / `COURSE_OUTBOX_JITTER_MAX_MS` | Backoff+jitter; `next_attempt_at` хранится в PostgreSQL |
| `COURSE_OUTBOX_LEASE_MS` / `COURSE_OUTBOX_POLL_MS` | Lease delivery и poll dispatcher (test: `2000` / `≤100`) |
| `COURSE_JOB_LEASE_MS` / `COURSE_WORKER_POLL_MS` | Lease job и poll worker (test: `2000` / `≤100`) |
| `COURSE_INBOX_POLL_MS` | Poll reconciler |
| `COURSE_AUTOCHECK_PASSWORD` | Пароль read-only роли `autocheck_reader` (checker) |
| `PROVIDER_URL` | URL provider для dispatcher, по умолчанию `http://provider-simulator:8081` |
| `OUTBOX_OWNER` | Owner первого dispatcher, по умолчанию `outbox-dispatcher` |
| `OUTBOX_OWNER_B` | Owner второго dispatcher, по умолчанию `outbox-dispatcher-b` |
| `PROVIDER_CALLBACK_CAPABILITY` | Сегмент callback path; в Compose нет default |
| `PROVIDER_CALLBACK_TOKEN` | JWT adapter → `receipt.accept`; в Compose нет default |
| `PROVIDER_HMAC_SECRET` | HMAC receipt v1 для adapter и api; в Compose нет default |
| `RECEIPT_API_URL` | `http://gateway:8080/api/receipt/accept` |
| `COURSE_DB_HOST` | Хост из контейнеров api/cli, в Compose это `postgres` |
| `COURSE_DB_PORT` | Порт PostgreSQL внутри сети |
| `COURSE_DB_NAME` | Имя БД для api/cli |
| `COURSE_API_BASE_URL` | Задаётся в Compose для gateway: `http://api:8080` |
| `LOG_LEVEL` | Уровень журнала хостов |
| `ASPNETCORE_ENVIRONMENT` | `Development` или `Production` |
| `OTEL_SERVICE_NAME` | Имя сервиса для OpenTelemetry |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Необязательный OTLP-коллектор |

JWT читает только `api`. Gateway секрета подписи не знает. `PROVIDER_HMAC_SECRET` получают `api` и `receipt-adapter`.

Claims токена: `sub`, `consumer`, `scope`, `iat`, `exp`. `scope` — строка с пробелами или массив строк. Число или объект в `scope` отклоняются как `auth.invalid`.

Демо-клиент для `payment.request` / `operation.get`:

| Claim | Значение |
| --- | --- |
| `sub` | `candidate-client` |
| `consumer` | `web` |
| `scope` | `payment:write payment:read workflow:read diagnostics:read` |

`denied-client` с пустым `scope` получает `403 access.denied`.

### Миграции

Схема создаётся в два шага.

1. Пустой named volume: `db/init/01-roles.sh` создаёт роли, схемы `api` / `app` / `autocheck` / `workflow` / `delivery` и таблицу `api.schema_migrations`. Скрипт образа PostgreSQL выполняется только при первом создании data directory.
2. Сервис `cli` при старте без аргументов применяет `db/migrations` и остаётся в idle. `api` и worker стартуют только после healthy `cli`.

Каждый `.sql` файл применяется лексикографически, в одной транзакции, от имени publication с `SET LOCAL ROLE course_owner`. Повтор того же checksum безопасен. Изменение уже применённого файла даёт `migration.conflict`.

Ручное применение того же механизма:

```
docker compose run --rm --no-deps -T cli migration apply /migrations
```

Публикация action не требует нового SQL, если target уже существует. Для новой функции сначала добавляется миграция, затем манифест:

```
docker compose run --rm --no-deps -T \
  -v "$(pwd)/db/manifests:/manifests:ro" \
  cli action validate /manifests/check.ping.v1.json

docker compose run --rm --no-deps -T \
  -v "$(pwd)/db/manifests:/manifests:ro" \
  cli action publish /manifests/check.ping.v1.json

docker compose run --rm --no-deps -T \
  cli action activate check.ping --version 1
```

CLI пишет на stdout ровно один JSON. Ошибка — ненулевой код процесса. Повтор publish того же манифеста идемпотентен. Другое тело той же версии — `manifest.conflict`.

API миграции не применяет и publication-пароль не читает.

### Workflow-карты

Карта — JSON или YAML по schema `contracts/course-1/workflow-map.schema.json`. Четыре типа шагов: `automatic`, `wait_signal`, `manual`, `end`. Маршрутизация только по `outcome`. Версия карты неизменяема: повтор publish с тем же телом безопасен, другое тело той же версии — `workflow.conflict`. Старт процесса закрепляет `flowVersion`.

CLI не читает таблицы workflow напрямую. Публикация и процесс:

```
docker compose run --rm --no-deps -T \
  -v "$(pwd)/db/maps:/maps:ro" \
  cli flow validate /maps/workflow-smoke.v1.json

docker compose run --rm --no-deps -T \
  -v "$(pwd)/db/maps:/maps:ro" \
  cli flow publish /maps/workflow-smoke.v1.json

docker compose run --rm --no-deps -T \
  cli flow activate workflow-smoke --version 1

docker compose run --rm --no-deps -T \
  -v "$(pwd)/tmp:/data:ro" \
  cli flow start workflow-smoke --business-key demo-1 --data /data/process.json

docker compose run --rm --no-deps -T \
  cli flow get <processId>

docker compose run --rm --no-deps -T \
  -v "$(pwd)/tmp:/data:ro" \
  cli flow signal <processId> --type training.completed --message-id msg-1 --payload /data/signal.json
```

Встроенные миграции уже публикуют `training.canary`, `workflow-smoke` v1 (активна) / v2, `workflow-manual` v1 (старт сразу в `WAITING_MANUAL`) и обе платёжные карты v1: `payment-processing` и `payment-review` (обе active). Binding `payment.submit` выбирает карту по `operationKind` на сервере: `PAYMENT_EXECUTION` → `payment-processing`, `PAYMENT_APPROVAL` → `payment-review`. Файлы: `db/manifests/training.canary.v1.json`, `db/maps/workflow-smoke.v1.json`, `db/maps/workflow-smoke.v2.json`, `db/maps/workflow-manual.v1.json`, `db/maps/payment-processing.v1.json`, `db/maps/payment-review.v1.json`.

Состояние process читается и по HTTP: `POST /api/workflow/get` с правом `workflow:read`.

### Worker

`worker-a` и `worker-b` собираются из одного Dockerfile и объявляют один image `reliapay-worker:local`. Портов на хост нет. Роль `workflow_worker` вызывает только `workflow.claim_jobs`, `api.invoke`, `workflow.finish_job` и `workflow.fail_job`. Каталог worker не читает: манифест приходит в payload claim.

Успешный automatic-шаг: `api.invoke` + проверка конверта + `finish_job` в одной транзакции, затем COMMIT. Ошибка: ROLLBACK invoke и отдельный `fail_job`. `requestId` равен `executionId`, principal — `workflow-worker`. Чужой lease (`workflow.lease_stale`) цикл не останавливает.

Failpoints — только при `COURSE_TEST_PROFILE=1` (иначе `COURSE_FAILPOINT` игнорируется). Компонент пишет одну JSON-строку в stdout и блокируется до `stop`:

```json
{"event":"failpoint.reached","name":"after_job_claim","instanceId":"worker-a"}
```

Шесть имён: `after_job_claim`, `after_action_before_finish` (worker); `after_outbox_claim`, `after_provider_response` (dispatcher); `after_inbox_saved`, `after_manual_decision` (api). Production profile точек не активирует; HTTP-endpoint включения нет.

Цикл «задайте точку → дождитесь ack → stop → снимите failpoint → up → докажите фактами/trace/metrics» и разбор результатов — в [docs/runbook.md](docs/runbook.md).

`COURSE_TEST_PROFILE=1` задаёт короткие poll/lease (worker/outbox порядка 100 мс / 2000 мс). В tracked Compose default равен `1`.

### Python-периметр

Три процесса собираются из одного образа `reliapay-python-perimeter:local` (Python 3.12+, PID 1 — `python -m perimeter`). Host-портов нет.

| Сервис | Роль БД | Граница |
| --- | --- | --- |
| `outbox-dispatcher` | `outbox_dispatcher` | PostgreSQL Outbox → provider (`OUTBOX_OWNER`) |
| `outbox-dispatcher-b` | `outbox_dispatcher` | тот же image/роль, другой `OUTBOX_OWNER_B` |
| `receipt-adapter` | нет | callback provider → gateway; ready зависит от gateway |
| `inbox-reconciler` | `inbox_reconciler` | `delivery.reconcile_inbox` |
| `inbox-reconciler-b` | `inbox_reconciler` | второй экземпляр того же image |

Adapter не получает настройки PostgreSQL. Dispatcher и reconciler не вызывают `api.invoke` и не делают table DML. Python не выбирает flow, limit и финальный status: это карты и actions в PostgreSQL. Завершение Outbox — conditional update по owner/`leaseVersion` (fencing).

Перезапуск периметра (`docker compose up -d --no-build --force-recreate outbox-dispatcher outbox-dispatcher-b receipt-adapter inbox-reconciler inbox-reconciler-b`) продолжает работу из PostgreSQL. Образы `api` и worker при этом не пересобираются.

Периметр не пишет в журнал секреты, полный callback, текст `message` провайдера и reason ручного решения. HTTP `workflow.manual` сохраняет `reason_hash` и principal из trusted context.

### Provider

В Compose подключён выданный образ `internship-provider-simulator:v0.2.0` по digest. Он принимает `POST /payments` и шлёт legacy callback на `http://receipt-adapter:8082/callbacks/provider-v02/{PROVIDER_CALLBACK_CAPABILITY}`. Это не Python-сервис и не отправитель signed receipt v1.


### Readiness

| Зонд | Смысл |
| --- | --- |
| `GET /health/live` | Процесс отвечает на HTTP (шлюз / сервис жив). Глубоких зависимостей нет. |
| `GET /health/ready` | Сервис готов принимать работу. |

Ready **api / worker / dispatcher / reconciler** зависит от PostgreSQL. Ready **adapter** — от gateway. **Provider outage не означает**, что api, worker или dispatcher стали `not_ready`: Outbox сохраняет delivery и ретраит после возврата provider.

Неуспешный ready: HTTP 503 и `{"status":"not_ready","code":"dependency.unavailable"}` (на gateway для внешнего ready — компактный unavailable-конверт шлюза, пока внутренний api не готов).

Служебные `/health/*` и `/metrics` у worker/python слушают внутренние порты (api/worker/dispatcher/reconciler `:8080`, adapter `:8082`) и **не** публикуются на хост. С хоста доступны live/ready шлюза на `:8080`.

### Быстрая проверка

После `docker compose up -d --build`:

```bash
curl -sS http://127.0.0.1:8080/health/live
curl -sS http://127.0.0.1:8080/health/ready
```

Обязательные OpenMetrics series на **api** (через exec — порт api на хост не проброшен):

```bash
docker compose exec -T api curl -fsS http://127.0.0.1:8080/metrics
```

Ожидаются семейства / samples: `workflow_jobs_ready`, `workflow_job_oldest_age_seconds`, `workflow_processes_waiting`, `outbox_pending`, `outbox_oldest_age_seconds`, `workflow_failures_total` (counter), документ заканчивается `# EOF`. High-cardinality labels (UUID операций и т.п.) запрещены.

Диагностика цепочки и «застрявших» после `DEAD` Outbox:

```bash
curl -sS http://127.0.0.1:8080/api/diagnostics/trace \
  -H "Authorization: Bearer <jwt>" \
  -H "Content-Type: application/json" \
  -d "{\"identifier\":\"<operationId-or-other>\"}"

curl -sS http://127.0.0.1:8080/api/diagnostics/stalled \
  -H "Authorization: Bearer <jwt>" \
  -H "Content-Type: application/json" \
  -d "{}"
```

Подробнее — [docs/runbook.md](docs/runbook.md).

### Проверка

```
./check.sh
```

Скрипт — public checker надёжности (отдельный Compose project). Том пользовательского `reliapay` и named volume `course_data` скрипт не сносит. Прогон охватывает admission → startup → outbox → receipt → review → observability → recovery → security и пишет локальный отчёт `week-4-public-report.json` (файл в `.gitignore`, на GitHub не попадает). Ожидаемый финал зависит от версии checker.

`--keep-stack` оставляет isolated project после прогона.

Локальный контур после `docker compose up -d --build`:

Готовность: http://localhost:8080/health/ready возвращает 200.

Примеры запросов к запущенному контуру.

Создание платежной операции (JWT подписывается ключом `COURSE_JWT_SIGNING_KEY`):

```
curl -sS http://127.0.0.1:8080/api/payment/request \
  -H "Authorization: Bearer <jwt>" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: demo-key-1" \
  -d "{\"operationKind\":\"PAYMENT_EXECUTION\",\"amount\":\"10.00\",\"currency\":\"RUB\"}"
```

Успех: HTTP 200, `outcome=CREATED`, в `result` есть `operationId`.

Повтор с тем же ключом и телом возвращает тот же `operationId`. Тот же ключ и другой `operationKind` — `409`.

Чтение операции:

```
curl -sS http://127.0.0.1:8080/api/operation/get \
  -H "Authorization: Bearer <jwt>" \
  -H "Content-Type: application/json" \
  -d "{\"operationId\":\"<uuid>\"}"
```

Неизвестный UUID — `404 operation.not_found`.

Неизвестный путь отсекается шлюзом до api:

```
curl -sS http://127.0.0.1:8080/swagger
```

Ожидается `400 request.invalid`.

### Диагностика

Расширенный runbook (trace/stalled, failpoint-цикл, lease_stale, DEAD vs FAILED, lost response) — [docs/runbook.md](docs/runbook.md).

Смотреть журналы:

```
docker compose logs -f gateway api cli worker-a worker-b outbox-dispatcher outbox-dispatcher-b receipt-adapter inbox-reconciler inbox-reconciler-b
```

Каждый не-health HTTP-запрос пишет пару строк без JWT и тела:

```
HTTP POST /api/payment/request started correlationId=...
HTTP POST /api/payment/request -> 200 correlationId=...
```

`correlationId` — UUID, который создаёт gateway. Клиентский `X-Correlation-Id` игнорируется. После аутентификации тот же UUID возвращается в `meta.correlationId`. До аутентификации в теле ошибки `correlationId` равен `null`.

| Симптом | Что проверить |
| --- | --- |
| `/health/ready` = 503 | `docker compose ps`; логи `postgres`, `cli`, `api` |
| cli не healthy | миграция с другим checksum; пароль publication |
| worker не берёт job | `flow list`; активна ли карта; READY job в `flow get` |
| `workflow.lease_stale` | finish/fail с чужим owner, старым `leaseVersion` или истёкшим `lease_until` до reclaim |
| нет `failpoint.reached` | `COURSE_FAILPOINT` и `COURSE_WORKER_INSTANCE_ID` |
| 401 `auth.invalid` | ключ, iss, aud, срок `exp`, типы claims |
| 403 `access.denied` | `scope` не покрывает `required_policy` |
| 404 `action.not_found` | `action list`; не забыт ли `action activate` |
| 409 `idempotency.conflict` | тот же `Idempotency-Key` с другим JSON |
| 503 на action | PostgreSQL недоступен; после `start postgres` ready должен вернуться без recreate api |
| 401 `signature.invalid` | заголовок `X-Provider-Signature` или HMAC над исходными bytes |
| 403 `receipt.signature_required` | `receipt.accept` без verified transport; Inbox не меняется |
| adapter 404 пустое тело | capability в пути не совпал с `PROVIDER_CALLBACK_CAPABILITY` |
| Python после recreate «забыл» Inbox | состояние в PostgreSQL; смотреть `autocheck.inbox`, не логи процесса |
| Outbox `DEAD`, process не `FAILED` | ожидаемо: delivery исчерпан, operation `PROCESSING`, process `WAITING_SIGNAL`; см. `diagnostics.stalled` |
| Ложный «второй платёж» | проверить fencing owner/`leaseVersion` и стабильный `externalRequestId`; dual dispatcher допустим |
| Provider down, ready api=200 | норма: provider не dependency ready; смотреть `outbox_pending` / retry |

Health не пишется в application-лог трассировки, чтобы пробы Compose не забивали журнал.

### Ограничения

В контуре: Database-first runtime, workflow, Python-периметр доставки, dual dispatcher/reconciler, failpoints, health/metrics и diagnostics. Это не полный банковский процессинг и не BPMN.

Не входит в контур:

- миграция версии уже запущенного process;
- Consul, CORS-трансформации, rate limit, кэш gateway;
- отдельные C# handlers на предметные actions;
- публикация портов api/cli/worker/python/provider/postgres на хост;
- ручной SQL как способ «доказать» или «починить» failpoint-сценарий.

Клиент не может выбрать target-функцию из payload и не передаёт имя workflow или финальный status в `payment.request`.

Секреты (`COURSE_JWT_SIGNING_KEY`, пароли ролей) в Git не хранятся.

Gateway не является «умным шлюзом»: авторизация, schema и catalog живут во внутреннем api и в PostgreSQL. См. [ADR-0001](docs/adr/0001-trust-boundary.md). Антикоррупционный слой callback и ACL ролей доставки — [ADR-0004](docs/adr/0004-python-anti-corruption.md).

## Эндпойнты

Все URL — через gateway `http://localhost:8080`.

### GET /health/live

Проверяет, что процесс шлюза запущен и принимает HTTP.

```json
{ "status": "live" }
```

### GET /health/ready

На шлюзе: готов ли внутренний api (и его зависимости). У api/worker/dispatcher/reconciler ready зависит от PostgreSQL; у adapter — от gateway. Provider **не** входит в ready api/worker/dispatcher.

```json
{ "status": "ready" }
```

Неуспех на сервисах курса: HTTP 503 и `{ "status": "not_ready", "code": "dependency.unavailable" }`. Шлюз до готовности api может отдавать компактный unavailable-конверт.

### POST /api/payment/request

Создаёт operation. Нужны `payment:write` и заголовок `Idempotency-Key`.

Тело:

```json
{
  "operationKind": "PAYMENT_EXECUTION",
  "amount": "10.00",
  "currency": "RUB"
}
```

`operationKind` также принимает `PAYMENT_APPROVAL`.

Успех:

```json
{
  "status": "ok",
  "outcome": "CREATED",
  "result": {
    "operationId": "...",
    "requestId": "demo-key-1",
    "operationKind": "PAYMENT_EXECUTION",
    "amount": "10.00",
    "currency": "RUB",
    "status": "CREATED"
  },
  "meta": {
    "correlationId": "...",
    "actionVersion": 1
  }
}
```

### POST /api/operation/get

Читает созданную operation. Нужен `payment:read`. Idempotency-ключ не требуется.

```json
{ "operationId": "..." }
```

Успех: `outcome=FOUND` и тот же набор полей result.

### POST /api/workflow/get

Читает снимок process: `process`, `steps`, `jobs`, `attempts`. Нужен `workflow:read`.

```json
{ "processId": "..." }
```

Успех: `outcome=FOUND`.

### POST /api/diagnostics/trace

Сквозная проекция цепочки по `identifier`. Нужен `diagnostics:read`. Тело: `{ "identifier": "<value>" }`. Успех: `outcome=FOUND`. Неизвестно: `404 diagnostics.trace_not_found`. Примеры — [runbook](docs/runbook.md).

### POST /api/diagnostics/stalled

Операции после исчерпания доставки (ожидание квитанции). Нужен `diagnostics:read`. Тело: `{}`. Успех: `outcome=FOUND`, `result.items[]` с `operationId` / `processId` / `externalRequestId`. Состояние не меняет.

### GET /metrics (внутренний api)

OpenMetrics на api `:8080` (с хоста: `docker compose exec -T api curl -fsS http://127.0.0.1:8080/metrics`). Обязательные series перечислены в разделе «Быстрая проверка». На gateway маршрут `/metrics` не публикуется.

### GET /openapi/default.json

OpenAPI 3 только включённых default-маршрутов. После `action activate` документ меняется без пересборки api.

## Обработка ошибок и логирование

Все ошибки runtime возвращаются в одном формате:

```json
{
  "status": "error",
  "code": "payload.invalid",
  "message": "payload does not match schema",
  "retryable": false,
  "details": {},
  "meta": {
    "correlationId": "...",
    "actionVersion": 1
  }
}
```

Полная матрица кодов — в [справочнике контракта](docs/contract-reference.md).

`retryable=true` только для `dependency.unavailable` и `action.timeout`.

В технический журнал попадают метод, маршрут, HTTP-статус и `correlationId`. Текст payload, JWT, SQL и сырой `Idempotency-Key` не сохраняются: для ключа пишется только SHA-256.

## Course CLI

Сервис `cli` при `docker compose up` применяет встроенные миграции и ждёт команды. Разовые вызовы:

```
docker compose run --rm --no-deps -T cli action list
docker compose run --rm --no-deps -T cli flow list
```

Манифесты и карты с хоста монтируются в контейнер: образ cli копирует `db/migrations`, но не `db/manifests` и не `db/maps`.

## Автор

ReliaPay — демонстрационный runtime платёжных операций с упором на надёжность доставки и наблюдаемость.

Надежда Попова

nadezhdapopova13@yandex.ru

GitHub: [simbarilion](https://github.com/simbarilion)
