# Week 4 — границы commit / durable fact и таблица идентичности

Факты сняты с текущего ReliaPay (миграции `db/migrations/`, `python/perimeter/dispatcher.py`, `workflow.claim_jobs`).  
Обновлено: 2026-09-28 (Asia/Barnaul).

---

## 1. Граница commit → durable fact → кто восстанавливает

| Ситуация (реальный путь) | Что уже в Postgres после commit | Кто подхватывает | Чего нельзя сделать |
|---|---|---|---|
| Crash внутри TX `app.payment_prepare_external` до COMMIT (INSERT `delivery.external_requests` + `delivery.outbox` + finish job ещё не зафиксированы) | Ничего durable: строки Outbox/`external_requests` не видны другим сессиям | Worker/API повтор того же workflow-шага с тем же `executionId` (idempotent ON CONFLICT по `external_request_id`) | Считать provider payment совершённым или Outbox «уже создан» |
| `payment_prepare_external` закоммичен: `external_requests.state=CREATED`, `outbox.state=PENDING`, `attempt_count=0`, `next_attempt_at≈now` | Durable intent доставки; `external_request_id` = `executionId` из context; `payload_hash` от стабильного JSON body | Любой `outbox-dispatcher` / `outbox-dispatcher-b` через `delivery.claim_outbox(owner, limit)` | Менять body / `external_request_id` на последующих attempts |
| Claim закоммичен: `outbox.state=LEASED`, `lease_owner`, `lease_version+1`, `lease_until=now+2s` (hardcoded в `claim_outbox`), `attempt_count+1`; HTTP ещё не ушёл (`dispatcher._claim` → потом `_deliver`) | Lease fencing в `delivery.outbox`; claim TX короткая (autocommit вокруг SELECT claim) | После expiry другой owner: claim выбирает `state=LEASED AND lease_until < now` (FOR UPDATE SKIP LOCKED) | Второй succeed/fail без совпадения owner+`lease_version` (код `delivery.lease_stale`) |
| HTTP к provider ушёл (`POST {PROVIDER_URL}/payments`, Idempotency-Key=`external_request_id`), ответ потерян / timeout / 5xx / transport | После fail: conditional `delivery.fail_outbox(outbox_id, owner, lease_version, error_code)` → `RETRY_WAIT` + `next_attempt_at` (backoff 200/400/800 ms при `*.retryable` и `attempt_count < 3`) или `DEAD` | Тот же/другой dispatcher после `next_attempt_at`; retry с тем же `external_request_id` и body из `provider_payment_bytes` | Переводить `app.operations` в `REJECTED` или process в `FAILED` только из-за Outbox `DEAD` |
| Успешный 202 ACCEPTED: `succeed_outbox` → `outbox.state=DELIVERED`, `external_requests.state=SENT`, `provider_payment_id` | Delivery принята provider; ждём receipt/signal | `inbox-reconciler` / `receipt.accept` → signal → process из `WAITING_SIGNAL` | Regression из `CONFIRMED` назад (succeed/fail на CONFIRMED возвращают ok/CONFIRMED) |
| Бюджет attempts исчерпан: `fail_outbox` → `state=DEAD`, `last_error_code`, `next_attempt_at=NULL` (колонки `dead_at` в таблице **ещё нет** — пробел Week 4) | Диагностика исчерпания доставки; operation остаётся `PROCESSING`, process `WAITING_SIGNAL` | Поздний valid receipt / reconcile может довести delivery до `CONFIRMED` (контракт Week 4; путь apply_receipt уже есть) | Считать предметный отказ (`REJECTED`/`FAILED`) эквивалентом `DEAD` |
| Worker claim: `workflow.claim_jobs(owner, lease_ms)` → job `LEASED`, `lease_version+1`, новый `workflow.attempts` (`attempt_id` UUID, `attempt_number=attempt_count`), старый RUNNING→`STALE` при reclaim | Job lease + attempt row; payload содержит `jobId`/`executionId`/`attemptId`/`leaseVersion` | Другой worker (`worker-a`/`worker-b`) после `lease_until`; finish только при owner+version | Принять stale finish от прежнего owner (`workflow.lease_stale`) |
| Crash worker после claim до finish action TX | Lease в `workflow.jobs`; незакоммиченный action effect откатывается | Reclaim после expiry → новый `attemptId`, тот же `jobId`/`executionId` | Дублировать subject effect при стабильном `executionId` (prepare_external ON CONFLICT) |
| Inbox: adapter сохранил receipt/`delivery.inbox` RECEIVED, reconciler ещё не применил | Durable inbox message + receipt row | `inbox-reconciler` / `inbox-reconciler-b` вызывают `delivery.reconcile_inbox(limit)` | Повторно применять тот же message как второй signal (at-most-once) |

**Кодовые якоря:** `delivery.claim_outbox` / `succeed_outbox` / `fail_outbox` в `20260909010000_delivery_outbox_inbox.sql`; HTTP вне claim — `python/perimeter/dispatcher.py`; identity внешнего платежа — `app.payment_prepare_external` (`external_request_id := executionId`); job fencing — `workflow.claim_jobs` в `20260826150000_workflow_contract_fields.sql`.

---

## 2. Таблица идентичности (как в ReliaPay сейчас)

| Поле | Где живёт | Стабильность | Правило в этом репозитории |
|---|---|---|---|
| **JobId** | `workflow.jobs.job_id` (uuid) | Стабильный на логический шаг | Один job на step instance; reclaim/retry не создают новый job |
| **ExecutionId** | `workflow.jobs.execution_id`; также ключ внешнего платежа | Стабильный на subject effect | `payment_prepare_external` берёт `context.executionId` → пишет как `delivery.external_requests.external_request_id` |
| **AttemptId** | `workflow.attempts.attempt_id` (uuid, gen при claim) | **Новый** на каждую попытку job | INSERT в `claim_jobs`; `attempt_number = jobs.attempt_count` после инкремента; UNIQUE `(job_id, attempt_number)` и `(job_id, lease_version)` |
| **LeaseVersion** | `workflow.jobs.lease_version` / `delivery.outbox.lease_version` (bigint) | **Растёт** на каждый claim/reclaim | Conditional UPDATE в `succeed`/`fail`/`finish` по owner+version; иначе `*_lease_stale` |
| **ExternalRequestId** | `delivery.external_requests.external_request_id` (PK text); копия в outbox | **Одинаковый** на все provider attempts данной delivery | = `executionId`; Idempotency-Key и body.operationId; UNIQUE outbox на этот id |
| **Body** (provider JSON) | Собирается в Python `provider_payment_bytes`; hash в `payload_hash` при prepare | **Одинаковое** на retries | `{"operationId":"<externalRequestId>","amount":"...","currency":"RUB"}` separators compact; менять нельзя — иначе второй платёж у provider |
| OutboxId | `delivery.outbox.outbox_id` | Стабильный на одну delivery | Один outbox row на external_request (UNIQUE) |
| CorrelationId | `delivery.external_requests.correlation_id`; header `X-Correlation-ID` | Стабильный после prepare | Из context или `gen_random_uuid()` при первом prepare |

**Пробелы Week 4 (не блокируют заполнение таблицы, нужны в фазах 2+):** нет колонки `dead_at`; `autocheck.outbox` ещё без `lease_owner`/`lease_version`/`lease_until`/`dead_at`; нет role `autocheck_reader`; в Compose нет `*-b`; backoff/lease/max attempts зашиты в SQL (2s / 200·400·800 / retry while attempt_count<3), не читают `COURSE_OUTBOX_*` env.