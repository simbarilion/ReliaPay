# Справочник контракта course-1

Канонические JSON Schema лежат в `contracts/course-1/`. Этот файл собирает нормы, которые читает HTTP-клиент и Course CLI.

## HTTP-маршрут

Единственный предметный вход:

```
POST /api/{module}/{action}
```

После `/api` ровно два сегмента. Версия только в заголовке `X-Action-Version`. Без заголовка runtime берёт включённую default-версию.

OpenAPI:

- `GET /openapi/default.json` — включённые default-маршруты;
- `GET /openapi/actions/{module}/{action}/{version}.json` — одна опубликованная версия.

Health шлюза:

- `GET /health/live` — процесс gateway, JSON `{ "status": "live" }`;
- `GET /health/ready` — готовность внутреннего api; `200` или `503`.

## Заголовки

| Заголовок | Кто задаёт | Смысл |
| --- | --- | --- |
| `Authorization: Bearer <jwt>` | клиент | HS256, claims `sub`, `consumer`, `scope`, `iat`, `exp` |
| `Idempotency-Key` | клиент | обязателен, если в манифесте `idempotency_mode=required`; в context это `requestId` |
| `X-Action-Version` | клиент, опционально | точная версия; иначе default |
| `X-Provider-Signature` | adapter | `v1=<lowercase hex HMAC-SHA256>` над исходными body bytes; gateway передаёт заголовок и тело без изменения |
| `Content-Type: application/json` | клиент | тело — JSON-объект |
| `X-Correlation-Id` | сервер | UUID, который mintит gateway; клиентский одноимённый заголовок отбрасывается |

## JWT

| Переменная | Значение по умолчанию |
| --- | --- |
| `COURSE_JWT_ISSUER` | `moduledev-course` |
| `COURSE_JWT_AUDIENCE` | `moduledev-api` |
| `COURSE_JWT_SIGNING_KEY` | секрет ≥ 32 байт, не в Git |

Неверный JSON-тип claim даёт `401 auth.invalid`, а не 500 фреймворка.

Демо-principals:

| `sub` | `consumer` | `scope` |
| --- | --- | --- |
| `candidate-client` | `web` | `payment:write payment:read workflow:read` |
| `workflow-worker` | `internal` | `workflow:execute payment:internal` |
| `reviewer` | `backoffice` | `workflow:manual payment:read` |
| `denied-client` | `test` | пусто |

`principal`, `consumer` и `scopes` в `api.invoke` берутся только из проверенного JWT. Поля payload их не подменяют.

## TrustedContext

Схема: `contracts/course-1/trusted-context.schema.json`.

```json
{
  "principal": "candidate-client",
  "consumer": "web",
  "scopes": ["payment:write", "payment:read", "workflow:read"],
  "correlationId": "00000000-0000-0000-0000-000000000001",
  "requestId": "idempotency-key-or-null",
  "deadline": "2026-08-25T12:00:00+00:00",
  "transport": {
    "signatureVerified": true,
    "signatureVersion": 1
  }
}
```

`requestId` равен заголовку `Idempotency-Key`, если он передан, иначе `null`. `deadline` считается из `timeout_ms` манифеста. `timeout_ms` — целое от 1 до 30000. Шлюз ждёт ответ api 35 секунд, чтобы успеть отдать `action.timeout`, а не `dependency.unavailable`.

Поле `transport` появляется только после успешной проверки HMAC над исходными body bytes. В context нет секрета и полной подписи. Без `transport` остальные actions работают как раньше; `receipt.accept` без `signatureVerified=true` даёт `403 receipt.signature_required` и не меняет Inbox.

## HTTP-конверты

Успех (`http-success.schema.json`):

```json
{
  "status": "ok",
  "outcome": "CREATED",
  "result": {},
  "meta": {
    "correlationId": "uuid",
    "actionVersion": 1
  }
}
```

Ошибка (`http-error.schema.json`):

```json
{
  "status": "error",
  "code": "auth.invalid",
  "message": "...",
  "retryable": false,
  "details": {},
  "meta": {
    "correlationId": null,
    "actionVersion": null
  }
}
```

После успешной аутентификации `meta.correlationId` обязателен. До неё допустим `null`.

## Матрица ошибок

| Код | HTTP | retryable | Когда |
| --- | --- | --- | --- |
| `auth.invalid` | 401 | нет | нет/битый JWT, неверный тип claim |
| `signature.invalid` | 401 | нет | формат, версия или HMAC `X-Provider-Signature`; target не вызывается |
| `request.invalid` | 400 | нет | не JSON, неизвестный маршрут, неверная версия |
| `idempotency.required` | 400 | нет | нет `Idempotency-Key` при `required` |
| `access.denied` | 403 | нет | не хватает scope из `required_policy` |
| `receipt.signature_required` | 403 | нет | `receipt.accept` без verified transport; Inbox не меняется |
| `action.not_found` | 404 | нет | неизвестный, выключенный или нерезолвящийся action |
| `operation.not_found` | 404 | нет | `operation.get` не нашёл операцию |
| `idempotency.conflict` | 409 | нет | тот же ключ, другой payload |
| `workflow.decision_conflict` | 409 | нет | `workflow.manual`, шаг уже решён |
| `payload.invalid` | 422 | нет | тело не проходит `request_schema` |
| `receipt.external_request_not_found` | 422 | нет | неизвестный `externalRequestId` у receipt |
| `dependency.unavailable` | 503 | да | PostgreSQL недоступен |
| `action.timeout` | 504 | да | истёк `timeout_ms` |
| `action.contract_violation` | 500 | нет | outcome/result вне манифеста |
| `internal.error` | 500 | нет | прочее без утечки SQL и stack |
| `workflow.mapping_missing` | — | нет | нет JSON Pointer в process data; action не вызывается |
| `workflow.lease_stale` | — | нет | finish/fail с чужим owner или `leaseVersion` |
| `workflow.conflict` | — | нет | конфликт идемпотентности process/сигнала или неизменяемой карты |

Сообщения не содержат JWT, полного payload, имён внутренних хостов и stack trace.

## Envelope `api.invoke`

Схема: `invoke-envelope.schema.json`.

- `status=ok` → обязательны `outcome` и `result`;
- `status=error` → обязательны `code` и `message`.

Runtime коммитит транзакцию только на `ok` с допустимым outcome и валидным result. Иначе rollback. См. [ADR-0002](adr/0002-technical-vs-domain-result.md).

## Базовые платёжные actions

### `payment.request` v1

- policy: `payment:write`;
- idempotency: `required` / `principal_action`;
- outcome: `CREATED`;
- payload: `operationKind` (`PAYMENT_EXECUTION` \| `PAYMENT_APPROVAL`), `amount` (строка), `currency` = `RUB`;
- клиент не передаёт имя workflow и финальный status;
- при создании operation через `payment.request` поле `process_id` равно `null` (process появляется после старта workflow).

Повтор того же ключа и payload возвращает тот же `operationId`. Другой payload с тем же ключом — `409 idempotency.conflict`.

### `operation.get` v1

- policy: `payment:read`;
- idempotency: `none`;
- outcome: `FOUND`;
- payload: `{ "operationId": "<uuid>" }`.

## Actions доставки и ручного решения

### `payment.submit` v1

- policy: `payment:write`;
- payload: `{ "operationId": "<uuid>" }` — без flow, kind, amount, limit, status;
- binding на сервере: `PAYMENT_EXECUTION` → `payment-processing`, `PAYMENT_APPROVAL` → `payment-review`;
- повтор того же ключа и тела возвращает исходный result со `status=PROCESSING` и тем же `processId`.

### `receipt.accept` v1

- policy: `receipt:write`;
- HMAC над исходными bytes; outcomes `RECEIVED` / `DUPLICATE`;
- `messageId` равен `providerPaymentId`;
- неизвестный `externalRequestId` — `422 receipt.external_request_not_found`.

### `workflow.manual` v1

- policy: `workflow:manual`;
- payload: `processId`, `stepInstanceId`, `decision` `APPROVED`|`REJECTED`, `reason` 1–500;
- в audit пишутся principal из context и `reason_hash`, не сырой reason;
- уже решённый шаг — `409 workflow.decision_conflict`.

Схемы: `receipt-v1.schema.json`, `provider-v02-callback.schema.json`, `payment-submit.payload.schema.json`, `workflow-manual.payload.schema.json`.

## Манифест action

Схема: `action-manifest.schema.json`. Обязательные поля опубликованного course-1: `contract_version`, `module`, `action`, `version`, `http_method=POST`, `target_schema`, `target_function`, `request_schema`, `response_schema`, `outcomes`, `required_policy`, `idempotency_mode`, `idempotency_scope`, `timeout_ms`, `enabled`, `is_default`. Неизвестные свойства файла не делают манифест невалидным. `request_schema` / `response_schema` — JSON Schema Draft 2020-12 (объект или boolean). Publish записывает `enabled=false` и `is_default=false` независимо от файла.

Опубликованная версия неизменяема. Повтор `action publish` с тем же телом безопасен. Другое тело той же версии — `manifest.conflict`.

## CLI

Stdout — ровно один JSON. Диагностика — stderr. Ошибка — ненулевой код процесса.

Успех: `cli-success.schema.json` (`status=ok`, `meta.contractVersion=course-1`).  
Ошибка: `cli-error.schema.json`.

Команды:

```
migration apply <directory>
action validate <manifest>
action publish <manifest>
action list
action activate <module.action> --version <version>
action disable <module.action> --version <version> [--replacement-version <version>]
flow validate <map>
flow publish <map>
flow list
flow activate <name> --version <version>
flow start <name> --business-key <key> --data <file>
flow get <processId>
flow signal <processId> --type <type> --message-id <id> --payload <file>
```

## autocheck

Базовые read-only views: `contract_info`, `action_definitions`, `action_dispatches`, `operations`, `operation_events`.

Views workflow: `flow_versions`, `processes`, `steps`, `jobs`, `attempts`, `signals`, `workflow_events`.

Views доставки и решений: `external_requests`, `receipts`, `outbox`, `inbox`, `decisions`. Views не публикуют capability, JWT, HMAC, полный callback и сырой reason.

`autocheck.contract_info.contract_version` = `course-1`.  
`action_dispatches.status` ∈ `OK`, `ERROR`.  
`operations.status` ∈ `CREATED`, `PROCESSING`, `COMPLETED`, `REJECTED`. `payment.request` создаёт operation со статусом `CREATED`.  
`payload_hash` — lowercase SHA-256 hex. Полный payload во views не публикуется.
