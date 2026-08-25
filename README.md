# ReliaPay

Универсальный API Gateway и runtime для публикации PostgreSQL-функций как HTTP-actions.

Сервис принимает клиентский запрос на единственном хост-порту 8080, аутентифицирует вызывающего по JWT HS256, резолвит действие из каталога в PostgreSQL и исполняет зарегистрированную функцию внутри одной транзакции. Клиент выбирает только `module`, `action` и JSON-payload. Имя схемы, имя функции и SQL ему недоступны.

Предметная логика живёт в PostgreSQL: создание operation и чтение её по идентификатору. C# не содержит отдельных контроллеров на каждый action. Новый зарегистрированный action вызывается тем же `POST /api/{module}/{action}` без пересборки образов gateway и api.

## Возможности и ограничения

### Возможности

- единственный внешний вход `http://127.0.0.1:8080`;
- тонкий gateway: whitelist маршрутов и прокси на внутренний api;
- generic runtime: JWT, JSON Schema, каталог действий, `api.invoke`;
- публикация actions манифестом через Course CLI без правки C#;
- идемпотентность `payment.request` уникальным индексом PostgreSQL;
- единый HTTP-конверт ошибок и успехов;
- OpenAPI из каталога: default-документ и документ точной версии;
- `correlationId` на каждый запрос и трассировка gateway → api;
- liveness шлюза и readiness по готовности PostgreSQL и инициализации;
- автоматическое применение checksum-миграций при `docker compose up`.

### Ограничения

Сервис:

- не отдаёт клиенту выбор БД, schema, функции или SQL;
- не хранит каталог действий в коде C#;
- не дублирует идемпотентность блокировкой процесса;
- не пишет JWT, секреты и полный payload в логи;
- не публикует на хост порты api, cli и PostgreSQL;
- не реализует workflow, worker, Outbox и вызов внешнего платёжного провайдера.

Интерактивный Swagger UI не публикуется. Контрактные документы:

http://127.0.0.1:8080/openapi/default.json

## Решение

Ссылки на архитектурные решения:

- [C4: контейнеры](docs/c4-containers.md)
- [ADR-0001. Граница доверия](docs/adr/0001-trust-boundary.md)
- [ADR-0002. Технический и предметный результат](docs/adr/0002-technical-vs-domain-result.md)
- [Справочник контракта course-1](docs/contract-reference.md)

Исходная формулировка недели 1 — в [TASK.md](TASK.md).

### Архитектура

ReliaPay — это Database-first runtime, а не набор предметных REST-контроллеров. Gateway реализуется один раз. Домен недели 1 — две PostgreSQL-функции и их манифесты.

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
            │  policy, target, unique index
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
         миграции и catalog
```

Слои C#:

```
    HTTP-хосты
        ReliaPay.Gateway
        ReliaPay.Api
        ReliaPay.Cli
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

`ReliaPay.Execution` координирует основной сценарий:

- резолвит манифест из catalog;
- проверяет policy и request schema;
- открывает транзакцию и вызывает `api.invoke`;
- коммитит только валидный предметный успех;
- мапит ошибки в HTTP-матрицу.

PostgreSQL хранит каталог, идемпотентность, operations и insert-only events.  
Gateway PostgreSQL не использует.

Роли БД:

| Роль | Login | Назначение |
| --- | --- | --- |
| `course_owner` | нет | владелец SECURITY DEFINER-функций |
| `course_publication` | да | миграции и мутации catalog |
| `course_runtime` | да | `api.invoke` и чтение catalog/views |

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

Готовность: http://127.0.0.1:8080/health/ready возвращает 200.

Также доступны:

- http://127.0.0.1:8080/health/live
- http://127.0.0.1:8080/openapi/default.json

Том PostgreSQL — `course_data`. Команда `docker compose down -v` удалит учебную базу.

На хост опубликован только порт `8080`. Порты PostgreSQL, api и cli наружу не пробрасываются. Внутри сети Compose используются адреса `postgres:5432` и `http://api:8080`.

### Конфигурация

| Переменная | Назначение |
| --- | --- |
| `COURSE_JWT_ISSUER` | Ожидаемый `iss`, по умолчанию `moduledev-course` |
| `COURSE_JWT_AUDIENCE` | Ожидаемый `aud`, по умолчанию `moduledev-api` |
| `COURSE_JWT_SIGNING_KEY` | Секрет HS256, не короче 32 байт |
| `POSTGRES_DB` | Имя БД, по умолчанию `course` |
| `POSTGRES_USER` | Bootstrap-пользователь образа PostgreSQL |
| `POSTGRES_PASSWORD` | Пароль bootstrap-пользователя |
| `COURSE_PUBLICATION_USER` | Роль миграций и catalog, по умолчанию `course_publication` |
| `COURSE_PUBLICATION_PASSWORD` | Пароль publication-роли |
| `COURSE_RUNTIME_USER` | Роль runtime, по умолчанию `course_runtime` |
| `COURSE_RUNTIME_PASSWORD` | Пароль runtime-роли |
| `COURSE_DB_HOST` | Хост из контейнеров api/cli, в Compose это `postgres` |
| `COURSE_DB_PORT` | Порт PostgreSQL внутри сети |
| `COURSE_DB_NAME` | Имя БД для api/cli |
| `COURSE_API_BASE_URL` | Задаётся в Compose для gateway: `http://api:8080` |
| `LOG_LEVEL` | Уровень журнала хостов |
| `ASPNETCORE_ENVIRONMENT` | `Development` или `Production` |
| `OTEL_SERVICE_NAME` | Имя сервиса для OpenTelemetry |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Необязательный OTLP-коллектор |

JWT читает только `api`. Gateway секрета подписи не знает.

Claims токена: `sub`, `consumer`, `scope`, `iat`, `exp`. `scope` — строка с пробелами или массив строк. Число или объект в `scope` отклоняются как `auth.invalid`.

Учебный клиент для `payment.request` / `operation.get`:

| Claim | Значение |
| --- | --- |
| `sub` | `candidate-client` |
| `consumer` | `web` |
| `scope` | `payment:write payment:read workflow:read` |

`denied-client` с пустым `scope` получает `403 access.denied`.

### Миграции

Схема создаётся в два шага.

1. Пустой named volume: `db/init/01-roles.sh` создаёт роли, схемы `api` / `app` / `autocheck` и таблицу `api.schema_migrations`. Скрипт образа PostgreSQL выполняется только при первом создании data directory.
2. Сервис `cli` при старте без аргументов применяет `db/migrations` и остаётся в idle. `api` стартует только после healthy `cli`.

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

### Проверка

```
bash ./check.sh
```

Скрипт поднимает Compose, прогоняет unit-тесты, пересоздаёт `api` и `gateway` без удаления тома, останавливает и запускает PostgreSQL, публикует action `check.ping` без пересборки образов и выполняет интеграционные и стресс-тесты через шлюз.

Ожидаемый финал: `check.sh: ok`.

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

Смотреть журналы:

```
docker compose logs -f gateway api cli
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
| 401 `auth.invalid` | ключ, iss, aud, срок `exp`, типы claims |
| 403 `access.denied` | `scope` не покрывает `required_policy` |
| 404 `action.not_found` | `action list`; не забыт ли `action activate` |
| 409 `idempotency.conflict` | тот же `Idempotency-Key` с другим JSON |
| 503 на action | PostgreSQL недоступен; после `start postgres` ready должен вернуться без recreate api |

Health не пишется в application-лог трассировки, чтобы пробы Compose не забивали журнал.

### Ограничения

Неделя 1 фиксирует механизм публикации функций, а не полный банковский процессинг.

Не входит в контур:

- workflow maps, process instances, worker, lease, retries;
- `payment.submit`, provider-simulator, receipts, manual actions;
- Outbox/Inbox, Consul, CORS-трансформации, rate limit, кэш gateway;
- отдельные C# handlers на предметные actions;
- публикация портов api/cli/postgres на хост.

Клиент не может выбрать target-функцию из payload и не передаёт имя workflow или финальный status в `payment.request`.

Секреты (`COURSE_JWT_SIGNING_KEY`, пароли ролей) в Git не хранятся.

Gateway не является «умным шлюзом»: авторизация, schema и catalog живут во внутреннем api и в PostgreSQL. См. [ADR-0001](docs/adr/0001-trust-boundary.md).

## Эндпойнты

Все URL — через gateway `http://127.0.0.1:8080`.

### GET /health/live

Проверяет, что процесс шлюза запущен и принимает HTTP.

```json
{ "status": "live" }
```

### GET /health/ready

Проверяет, что внутренний api готов: PostgreSQL доступен и `autocheck.contract_info` содержит `course-1`.

```json
{ "status": "ready" }
```

Иначе HTTP 503 и `{ "status": "unavailable" }`. Внутренние адреса в тело не попадают.

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

В технический журнал попадают метод, маршрут, HTTP-статус и `correlationId`. Текст payload, JWT и SQL не сохраняются.

## Course CLI

Сервис `cli` при `docker compose up` применяет встроенные миграции и ждёт команды. Разовые вызовы:

```
docker compose run --rm --no-deps -T cli action list
```

Манифесты с хоста монтируются в контейнер: образ cli копирует `db/migrations`, но не `db/manifests`.

## Автор

Выполнено в рамках учебного задания ModuleDev, неделя 1.

Надежда Попова

nadezhdapopova13@yandex.ru

GitHub: [simbarilion](https://github.com/simbarilion)
