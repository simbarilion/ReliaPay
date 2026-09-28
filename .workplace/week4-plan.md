# План Week 4 — Надёжность и восстановление

**Источник задания:** `moduledev-week-4-reliability-task/` (редакция `week-4.1`)  
**Дедлайн:** 30 сентября 2026, 23:59 МСК  
**Стек:** не останавливать live `reliapay` через `down -v`  
**Статус на сейчас:** анализ + замена autocheck + gitignore; фичи Week 4 ещё не реализованы

Связанные материалы задания (в игнорируемом каталоге):

- `task/assignment.md`, `task/student-handout.md`
- `docs/05-week-4.md`, `docs/configuration.md`, `docs/observability-contracts.md`
- `docs/07-autocheck-outline.md`, `docs/06-rating-and-checks.md`
- `contracts/course-1` (в т.ч. diagnostics schemas)

Примечание по каталогам: исторически заметки лежат в `.workspace/` (в `.gitignore`).  
План Week 4 — здесь, в `.workplace/`, по явной просьбе.

---

## Цели Week 4 (кратко)

Продолжить тот же репозиторий (недели 1–3). После контролируемого сбоя и полного restart:

- нет потерянного сообщения;
- нет второго предметного эффекта (двойной provider payment);
- нет ложного успеха (`DEAD` ≠ domain reject).

Recovery делают обычные worker / dispatcher / reconciler loops по состоянию PostgreSQL, не ручной SQL.

### Обязательные deliverables

1. **Compose seam:** сервисы `outbox-dispatcher-b`, `inbox-reconciler-b`; один local Python 3.12+ image на все 5 Python-сервисов; разные `OUTBOX_OWNER`; host-порт только у `gateway`; named volume для PostgreSQL.
2. **Надёжная доставка:** claim → commit → HTTP; conditional update по owner/`leaseVersion`; bounded retry (408/429/5xx/timeout/transport); backoff+jitter; stable `externalRequestId`/body; reclaim; `DEAD` + late receipt → `CONFIRMED`.
3. **Workflow recovery:** lease reclaim, stale completion → `workflow.lease_stale`, устойчивые READY/RETRY_WAIT/WAITING_*, идемпотентные callback/manual после restart.
4. **Failpoints** (только `COURSE_TEST_PROFILE=1`): шесть точек, JSON ack в лог, блокировка до stop; production игнорирует.
5. **Health / OpenMetrics:** live/ready/metrics на api, workers, обоих dispatcher, adapter, обоих reconciler; обязательные series на `api`; без high-cardinality labels.
6. **Actions:** `diagnostics.trace` v1 и `diagnostics.stalled` v1 (`diagnostics:read`).
7. **README / runbook:** первый запуск, конфигурация, readiness, полная проверка, команды trace/stalled.
8. **Конфиг checker:** переменные из `docs/configuration.md`, `autocheck_reader`, schema `autocheck`, test-profile интервалы.

### Ограничения / стоп-факторы

- Не менять архитектурные границы недель 1–3.
- Не копировать checker-repo поверх root `check.sh` предыдущих недель (корневой `check.sh` решения не затираем).
- Секреты/full payload не в repo, image layers, logs, trace, report.
- Не лечить race через увеличение `sleep`.
- `DEAD` не переводит operation в `REJECTED` / process в `FAILED`.
- Provider outage не делает API/worker/dispatcher `not ready`.

---

## Фаза 0 — Анализ (текущая / завершена по подготовке)

- [x] Прочитать `README`, `assignment`, `student-handout`, контракты checker/observability/configuration.
- [x] Зафиксировать baseline Week 3: Compose services сейчас без `*-b`; есть `course_data` volume; один `outbox-dispatcher` / `inbox-reconciler`; `COURSE_FAILPOINT` уже прокинут частично.
- [x] Заменить `autocheck/` на Week 4; добавить `moduledev-week-4-reliability-task/` в `.gitignore`.
- [ ] Составить таблицу `граница commit → durable fact → owner recovery` (перед кодом).
- [ ] Сверить `.env.example` / Compose с полной таблицей переменных Week 4.
- [ ] Инвентаризация: health/metrics endpoints, Outbox DEAD-поля, lease fencing, diagnostics actions — что уже есть vs пробелы.

**Выход фазы:** этот план + baseline gaps.

---

## Фаза 1 — Compose seam и конфигурация

1. Добавить `outbox-dispatcher-b` и `inbox-reconciler-b` (тот же image `reliapay-python-perimeter:local`, разные entrypoints/role, без новых DB grants).
2. `OUTBOX_OWNER` / `OUTBOX_OWNER_B` — разные значения (`outbox-dispatcher` / `outbox-dispatcher-b`).
3. Протащить все `COURSE_*` из configuration contract (timeouts, attempts, backoff, lease, poll, failpoint, autocheck password и т.д.).
4. Подтвердить named volume PostgreSQL (уже `course_data`) — переживает `compose down` без `-v`.
5. Убедиться: host-порт только `gateway`; Python/provider без host ports.
6. `autocheck_reader` + read-only schema `autocheck` (CONNECT/USAGE/SELECT views only).

**Выход:** cold `up --build` поднимает 12 обязательных service names; readiness ок при выключенном provider у API/worker/dispatcher.

---

## Фаза 2 — Надёжная доставка (Outbox / dual dispatcher)

1. Короткий claim-транзакцией; HTTP **вне** claim; результат — conditional update owner/`leaseVersion`.
2. Bounded retry: transport, timeout, 408/429/5xx; не retry terminal 4xx.
3. Test profile: 4 attempts, timeout 500 ms, base 200/400/800, jitter 0–100, lease 2 s, poll ≤ 100 ms.
4. Stable body + `externalRequestId` на всех attempts; reclaim просроченного delivery.
5. `DEAD`: `state`, `dead_at`, `last_error_code`, `attempt_count`; operation остаётся PROCESSING, process WAITING_SIGNAL.
6. Late valid receipt → delivery `CONFIRMED`, process продолжается.
7. Два dispatcher без двойного provider payment (fencing).

**Выход:** outage + resume + dual-dispatcher сценарии готовы к public outbox phase.

---

## Фаза 3 — Workflow recovery и failpoints

1. Expired job → reclaim с новым `leaseVersion`; stale finish → `workflow.lease_stale`.
2. Retry: тот же `jobId`/`executionId`, новый `attemptId`; action retry без повторного subject effect.
3. Исчерпание action retries → job `DEAD`, process `FAILED`, safe error code.
4. Шесть failpoints + JSON `failpoint.reached`; production profile игнорирует; без публичного HTTP endpoint активации.
5. Полный restart сохраняет READY / RETRY_WAIT / WAITING_SIGNAL / WAITING_MANUAL; callback/manual идемпотентны.

**Выход:** crash matrix закрыта обычными loops.

---

## Фаза 4 — Наблюдаемость

1. `GET /health/live`, `/health/ready`, `/metrics` на всех требуемых процессах (порты из configuration.md).
2. PostgreSQL — readiness dependency API/worker/dispatcher/reconciler; adapter зависит от gateway; provider outage ≠ not ready.
3. Обязательные series на `api:8080/metrics` (`workflow_jobs_ready`, `workflow_job_oldest_age_seconds`, `workflow_processes_waiting`, `outbox_pending`, `outbox_oldest_age_seconds`, `workflow_failures_total`).
4. Labels: без UUID/уникальных ID; value ≤ 64; ≤ 20 значений одного label на scrape.
5. Action `diagnostics.trace` v1: policy `diagnostics:read`, payload `{"identifier":...}`, 404 `diagnostics.trace_not_found`.
6. Action `diagnostics.stalled` v1: `POST /api/diagnostics/stalled`, `{}`, outcome `FOUND`, `{"items":[...]}` с `operationId`/`processId`/`externalRequestId`, без мутации состояния.
7. При необходимости — схемы из task `contracts/course-1` в проектный `contracts/` (на этапе реализации, не сейчас).

**Выход:** observability phase public checker зелёная на контракте.

---

## Фаза 5 — Документация и регрессия Week 3

1. Обновить README: окружение, конфиг, первый запуск, полная проверка, readiness, типовые отказы.
2. Runbook: команды вызова `diagnostics.trace` / `diagnostics.stalled` и чтения результата.
3. Регрессия сценариев Week 3 (receipt/review/outbox baseline) не ломается.
4. Перепроверить замечания Week 1–3 по коду и сохранённым сценариям.

**Выход:** сдача воспроизводима по README другим разработчиком.

---

## Фаза 6 — Тесты (локальные)

1. Unit/integration: lease fencing, DEAD/late receipt, dual owner, metrics cardinality.
2. Smoke failpoints (по одному) на test profile без ручного SQL.
3. Restart без `-v`: state и volume сохранены.
4. Не гонять `docker compose down -v` на live-стеке `reliapay`.

**Выход:** уверенность перед public checker.

---

## Фаза 7 — Week 4 autocheck

Запуск из клона задания (предпочтительно) **или** через локальный `autocheck/` после swap:

```text
./moduledev-week-4-reliability-task/check.sh --repo <path-to-ReliaPay>
```

Public phases: `admission` → `startup` → `outbox` → `receipt` → `review` → `observability` → `recovery` → `security`.

Артефакт: `week-4-public-report.json` (локальный, не в сдачу; уже в духе игнора week-*-public-report).

Коды: 0 ok / 1 контракт решения / 2 окружение checker.

**Выход:** public report без секретов; при необходимости `--keep-stack` для отладки.

---

## Фаза 8 — Отчёт и сдача

1. Зафиксировать SHA commit, ветку, URL репозитория для куратора.
2. Краткий отчёт: что сделано, пробелы, риски.
3. Не включать secrets, build output, логи, generated reports в сдачу.
4. Не push без явной просьбы (в этой подготовке — без push).

---

## Порядок работ (рекомендация handout)

1. Зафиксировать working E2E Week 3.  
2. Таблица `граница → durable fact → owner recovery`.  
3. Сначала вторые replicas + fencing, затем failpoints.  
4. Потом observability + docs + checker.

---

## Открытые риски (на старте реализации)

| Риск | Почему | Митигация |
|---|---|---|
| Dual dispatcher без fencing | Двойной payment / lost response | Conditional owner/lease; HTTP вне claim |
| `DEAD` как domain reject | Ложный REJECTED/FAILED | Явно оставить PROCESSING/WAITING_SIGNAL; late receipt |
| High-cardinality metrics | Падение observability phase | IDs только в trace/logs |
| Failpoint timing на Docker pause | Exit 2 окружения, не дефект | Не считать flaky run доказательством |
| Live stack `down -v` | Потеря volume | Только `down` без `-v`; отдельный checker project |
| Корневой `check.sh` | Задание запрещает затирать прошлые недели | Меняем только `autocheck/`; root script не трогаем |
| Контракты diagnostics | В task-репо, не в решении | Скопировать нужные schema в `contracts/` на фазе 4 |

---

## Следующий шаг реализации

Начать **Фазу 1**: добавить `outbox-dispatcher-b` / `inbox-reconciler-b` и выровнять Compose/env под configuration contract Week 4, не останавливая live-стек через `down -v`.