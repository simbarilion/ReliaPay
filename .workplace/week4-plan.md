# План Week 4 — Надёжность и восстановление

**Название недели:** «Пережив сбой и докажи результат»  
**Две части задания (обязательны обе):**
1. **Устойчивость** — контролируемые сбои + самовосстановление без ручного SQL.
2. **Доказательство** — результат операции доказывается сохранёнными фактами (трейс/метрики/состояние Progress), а не «угадыванием» по логам.

**Источник задания:** `moduledev-week-4-reliability-task/` (редирект `week-4.1`)  
**Дедлайн:** 30 сентября 2026, 23:59 МСК  
**Стек:** не останавливать live `reliapay` через `down -v`  
**Статус на сейчас:** анализ + замена autocheck + gitignore + план; код Week 4 ещё не реализован

Обязательные материалы задания (в игнорируемом каталоге):

- `task/assignment.md`, `task/student-handout.md`
- `docs/05-week-4.md`, `docs/configuration.md`, `docs/observability-contracts.md`
- `docs/07-autocheck-outline.md`, `docs/06-rating-and-checks.md`
- `contracts/course-1` (в т.ч. diagnostics schemas)

Черновики из ретейлов: свободно класть в `.workspace/` (в `.gitignore`).  
План Week 4 — живой, в `.workplace/`, под явным коммитом.

---

## Архитектура (как в гайде)

API принимает запрос → пишет состояние в **Progress** → событие/запись уходит в **Outbox** → **Dispatcher** шлёт запрос провайдеру → ответ проверяет **Adapter** и сохраняется через Generic C# API.

**Новое на неделе:** второй **Dispatcher** и второй **Reconciler** (`*-b`). Worker остаётся с lease/fencing.

**Критерии успеха после crash + restart:**
- нет потерянных сообщений;
- нет второго предметного эффекта (второй оплаты у провайдера);
- нет ложных успехов (`DEAD` ≠ domain reject / REJECTED / process FAILED).

Восстановление — **только** автоматическими циклами Worker / Dispatcher / Reconciler по состоянию Progress в PostgreSQL.

---

## Главные правила (из памятки — учтены в плане)

| Правило | Как отражено |
|--------|----------------|
| Сначала Week 3 рабочий сценарий → таблица границ → replicas+fencing → только потом FailPoints | «Порядок работы» + фазы 0→2→3 |
| При падении из‑за гонки **не** увеличивать `sleep` | стоп-фактор + фаза 6 |
| Короткая TX: owner + LeaseVersion + lease_until → **commit** → HTTP **после** commit → conditional UPDATE | фазы 2–3 |
| Повторять только то, что может пройти со 2-го раза (transport/timeout/408/429/5xx); прочие 4xx — терминальные | фаза 2 |
| Test profile: 4 попытки, 200/400/800 + jitter 0–100; `NextAttemptTime` в Postgres, не в памяти | фаза 2 |
| Таблица идентичности: JobId / ExecutionId (стабильный) / AttemptId (новый) / LeaseVersion (растёт) / ExternalRequestId + body (одинаковые) | фаза 0 таблица + фаза 2 |
| `DEAD` ≠ `REJECTED`: Lost Response → delivery DEAD, operation PROCESSING, ожидание сигнала; late receipt → CONFIRMED | фаза 2 |
| FailPoints только test profile; лог `FailPointTrigger` + InstanceId; production игнорирует; нет config-endpoint для включения | фаза 3 |
| Live / Ready / Metrics на каждом процессе; provider **не** влияет на Ready | фаза 4 |
| Метрики / логи / трейс: трейс из связей БД = доказательство; логи = рассказ | фаза 4 + observability-гайд ниже |
| Секреты в логах — стоп-фактор | фаза 4 / 5 / 7 |
| После FailPoint-цикла: нет потерь, нет 2-го эффекта, нет ложного успеха, история попыток At-most-once по ключу | фазы 3, 6, 7 |

---

## Цель Week 4 (кратко)

Продолжение тех же репозиториев (недели 1–3). После контролируемого сбоя и полного restart: нет потерянных сообщений; нет второго provider payment; нет ложного успеха. Recovery только через loops на PostgreSQL.

### Обязательные deliverables

1. **Compose seam:** сервисы `outbox-dispatcher-b`, `inbox-reconciler-b`; один local Python 3.12+ image на все Python-сервисы; разные `OUTBOX_OWNER`; host-порт только у `gateway`; named volume для PostgreSQL.
2. **Надёжная доставка:** claim → commit → HTTP; conditional update по owner/`leaseVersion`; bounded retry; backoff+jitter; stable `externalRequestId`/body; reclaim; `DEAD` + late receipt → `CONFIRMED`.
3. **Workflow recovery:** lease reclaim, stale completion → `workflow.lease_stale`; переживают READY/RETRY_WAIT/WAITING_*; возобновление callback/manual после restart.
4. **Failpoints** (только `COURSE_TEST_PROFILE=1`): 6 точек, JSON ack в лог, блокировка до stop; production игнорирует.
5. **Health / OpenMetrics:** live/ready/metrics; обязательные series на `api`; без high-cardinality labels.
6. **Actions:** `diagnostics.trace` v1 и `diagnostics.stalled` v1 (`diagnostics:read`).
7. **README / runbook:** два контура (пережить / доказать), конфиг, readiness, ручная проверка, примеры trace/stalled.
8. **Конфиг checker:** переменные из `docs/configuration.md`, `autocheck_reader`, schema `autocheck`, test-profile интервалы.

### Ограничения / стоп-факторы

- Не ломать контрактные границы недель 1–3.
- Не копировать checker-repo поверх root `check.sh` решения.
- Секреты/full payload не в repo, image layers, logs, trace, report.
- Не чинить race через увеличение `sleep`.
- `DEAD` не переводит operation в `REJECTED` / process в `FAILED`.
- Provider outage не делает API/worker/dispatcher `not ready`.

---

## Фаза 0 — Анализ (текущая / завершена по подготовке)

- [x] Прочитаны README, assignment, student-handout, контракты checker/observability/configuration.
- [x] Зафиксирован baseline Week 3: Compose без `*-b`; есть `course_data`; один dispatcher/reconciler; `COURSE_FAILPOINT` уже прокинут частично.
- [x] Заменён `autocheck/` на Week 4; каталог задания в `.gitignore`.
- [x] Сводка гайда «Пережив сбой и докажи результат» внесена в этот план (архитектура, DEAD, FailPoints, логи/трейс).
- [ ] Заполнить таблицу **«Граница commit → durable fact → кто восстанавливает»** (перед кодом) — шаблон ниже.
- [ ] Заполнить **таблицу идентичности** (JobId / ExecutionId / AttemptId / LeaseVersion / ExternalRequestId / body).
- [ ] Сверить `.env.example` / Compose с полной таблицей переменных Week 4.
- [ ] Инвентаризация: health/metrics, Outbox DEAD-поля, lease fencing, diagnostics actions — что уже есть vs пробелы.

**Выход фазы:** живой план + baseline gaps + две заполненные таблицы.

### Шаблон: граница commit / durable fact / owner recovery

| Ситуация | Что уже в Postgres после commit | Кто подхватывает | Чего нельзя сделать |
|----------|----------------------------------|------------------|---------------------|
| Упал процесс в TX Operation+Outbox до commit | ничего durable | повтор с тем же idempotency / API | считать оплату совершённой |
| Outbox claimed, HTTP ещё не ушёл | owner, leaseVersion, lease_until | другой Dispatcher после expiry | второй claim без fencing |
| HTTP ушёл, ответ потерян (Lost Response) | attempt + externalRequestId | retry тем же ExternalRequestId; после бюджета → DEAD | REJECTED / FAILED операции |
| Worker A завис после claim Job | job lease + leaseVersion | Worker B с новой version | stale finish от A |
| Valid late receipt после DEAD | receipt + delivery → CONFIRMED | Reconciler / receipt path | оставить «ложный» предметный отказ |

### Шаблон: таблица идентичности

| Поле | Стабильность | Правило |
|------|--------------|---------|
| JobId | стабильный | один job на шаг |
| ExecutionId | стабильный | одна execution |
| AttemptId | **новый** на попытку | не переиспользовать |
| LeaseVersion | **растёт** | fencing |
| ExternalRequestId | **одинаковый** на все attempts доставки | иначе второй платёж |
| Тело запроса провайдеру | **одинаковое** | иначе второй платёж |

---

## Фаза 1 — Compose seam и конфигурация

1. Добавить `outbox-dispatcher-b` и `inbox-reconciler-b` (тот же image `reliapay-python-perimeter:local`, разные entrypoints/role, те же DB grants).
2. `OUTBOX_OWNER` / owner B — разные значения (`outbox-dispatcher` / `outbox-dispatcher-b`).
3. Выровнять `COURSE_*` под configuration contract (timeouts, attempts, backoff, lease, poll, failpoint, autocheck password и т.д.).
4. Подтвердить named volume PostgreSQL (`course_data`) — переживает `compose down` без `-v`.
5. Проверить: host-порт только `gateway`; Python/provider без host ports.
6. `autocheck_reader` + read-only schema `autocheck` (CONNECT/USAGE/SELECT views only).

**Выход:** cold `up --build` поднимает обязательные service names; readiness зелёный при отключённом provider у API/worker/dispatcher.

---

## Фаза 2 — Надёжная доставка (Outbox / dual dispatcher)

1. Короткая claim-транзакция; HTTP **вне** claim; результат — conditional update owner/`leaseVersion` (или тот же ExternalRequestId).
2. Bounded retry: transport, timeout, 408/429/5xx; **не** retry terminal 4xx.
3. Test profile: 4 attempts, timeout 500 ms, base 200/400/800, jitter 0–100, lease 2 s, poll ≤ 100 ms; `next_attempt_at` в Postgres.
4. Stable body + `externalRequestId` на все attempts; reclaim просроченной delivery.
5. `DEAD`: `state`, `dead_at`, `last_error_code`, `attempt_count`; operation остаётся PROCESSING, process WAITING_SIGNAL.
6. Late valid receipt → delivery `CONFIRMED`, process продолжается.
7. Два dispatcher без второго provider payment (fencing).

**Выход:** outage + resume + dual-dispatcher сценарии готовы к public outbox phase.

---

## Фаза 3 — Workflow recovery и failpoints

1. Expired job → reclaim с новым `leaseVersion`; stale finish → `workflow.lease_stale`.
2. Retry: тот же `jobId`/`executionId`, новый `attemptId`; action retry без повторного subject effect.
3. Исчерпанные action retries → job `DEAD`, process `FAILED`, safe error code (это **не** то же самое, что Outbox `DEAD` без предметного результата).
4. Шесть failpoints + JSON/`FailPointTrigger` + InstanceId; production profile игнорирует; нет публичного HTTP endpoint активации.
5. Цикл проверки: включить FailPoint → дождаться строки → stop контейнера → убрать FailPoint → up без ручного SQL → доказать трейсом/метриками/логами.
6. После restart сохранены READY / RETRY_WAIT / WAITING_SIGNAL / WAITING_MANUAL; callback/manual возобновляются.

**Выход:** crash matrix закрыта живыми loops; 4 проверяемых исхода (нет потерь / нет 2-го эффекта / нет ложного успеха / at-most-once по ключу).

---

## Фаза 4 — Наблюдаемость (метрики + логи + трейс)

1. `GET /health/live`, `/health/ready`, `/metrics` на всех требуемых сервисах (порты из configuration.md).
2. PostgreSQL — readiness dependency API/worker/dispatcher/reconciler; adapter зависит от gateway; provider outage ≠ not ready.
3. Обязательные series на `api:8080/metrics` (`workflow_jobs_ready`, `workflow_job_oldest_age_seconds`, `workflow_processes_waiting`, `outbox_pending`, `outbox_oldest_age_seconds`, `workflow_failures_total`).
4. Labels: без UUID/уникальных ID; value ≤ 64; ≤ 20 значений любого label на scrape.
5. Action `diagnostics.trace` v1: policy `diagnostics:read`, до ~11 идентификаторов (CorrelationId, RequestId, OperationId, …), единая цепочка из БД; 404 `diagnostics.trace_not_found`.
6. Action `diagnostics.stalled` v1: `POST /api/diagnostics/stalled`, `{}`, outcome `FOUND`, items с `operationId`/`processId`/`externalRequestId`, без мутации состояния.
7. При необходимости — схемы из task `contracts/course-1` в проектный `contracts/` (на этапе реализации, не сейчас).

### Логи (гайд) — в объёме реализации

- Каждая строка отвечает на 5 вопросов: что / где / почему / кто инициатор / к чему привело.
- Логируем: начало, прогресс, завершение; **стабильные** имена событий; `CorrelationId` наследуется детьми.
- Уровни: Error / Warning / Info / Debug (Debug остаётся в коде, не в прод-пайплайне логов курса).
- **Запрет в логах:** JWT/HMAC, подписи, пароли БД, полные платёжные пути, тела квитанций, callback провайдера, ручные решения. Вместо деталей — идентификаторы и безопасные коды.
- Агент/диагностика: фильтр по CorrelationId по обоим dispatcher, сортировка по времени; гипотеза + доказательства (трейс + БД), не «угадывание» по тексту.

**Выход:** observability phase public checker зелёный по контракту.

---

## Фаза 5 — Документация и регрессия Week 3

1. Обновить README: два контура («пережить» / «доказать»), конфиг, запуск, ручная проверка, readiness, типовые отказы.
2. Runbook: примеры вызовов `diagnostics.trace` / `diagnostics.stalled` и чтения результата; цикл FailPoint → restart → доказательство.
3. Регрессия сценариев Week 3 (receipt/review/outbox baseline) не сломана.
4. Перепроверить замечания Week 1–3 на дыры в соседних сценариях.

**Выход:** сдача воспроизводима по README другим разработчиком.

---

## Фаза 6 — Тесты (локально)

1. Unit/integration: lease fencing, DEAD/late receipt, dual owner, metrics cardinality, identity table.
2. Smoke failpoints (по матрице) на test profile без ручного SQL.
3. Restart без `-v`: state в volume сохранён.
4. Не делать `docker compose down -v` на live-стеке `reliapay`.
5. Если тест падает из‑за гонки — чинить fencing/условие, **не** sleep.

**Выход:** уверенность перед public checker.

---

## Фаза 7 — Week 4 autocheck

Запуск из корня решения после swap:

```text
./check.sh
```

(или путь, который указывает README после обновления; checker-pack `check.sh` не копировать поверх корневого, если конфликт — следовать assignment).

Public phases: `admission` → `startup` → `outbox` → `receipt` → `review` → `observability` → `recovery` → `security`.

Артефакт: `week-4-public-report.json` (локально, не в сдаче).

Коды: 0 ok / 1 контракт решения / 2 окружение checker.

**Выход:** public report без секретов; при необходимости `--keep-stack` для отладки.

---

## Фаза 8 — Отчёт и сдача

1. Зафиксировать SHA commit, ветку, URL репозитория для сдачи.
2. Краткий отчёт: что сделано, пробелы, риски.
3. Не класть secrets, build output, логи, generated reports в сдачу.
4. Не push без явной просьбы.

---

## Порядок работы (рекомендация handout)

1. Зафиксировать working E2E Week 3.  
2. Заполнить таблицы границ и идентичности.  
3. Сначала dual replicas + fencing, потом failpoints.  
4. Потом observability + docs + checker.  
5. Диагностический цикл агента: FailPoint → timeline логов → сверка трейса и метрик → фиксация в README.

---

## Соответствие этой памяткой ↔ план

| Тема из памятки | Уже было | Дополнено сейчас |
|-----------------|----------|------------------|
| Две части названия (пережить / доказать) | частично в цели | явный блок в шапке |
| Архитектура API→Progress→Outbox→… + dual B | в deliverables | отдельный раздел |
| Таблица границ commit/fact/owner | чеклист «заполнить» | шаблон таблицы |
| Таблица идентичности | в фазе 2 кратко | шаблон + фаза 0 |
| Протокол claim/commit/HTTP/conditional UPDATE | фазы 2–3 | усилено в правилах |
| Retry classification + NextAttemptTime в PG | фаза 2 | явно в правилах |
| DEAD ≠ Rejected / Lost Response | фаза 2 + риски | усилено примером |
| FailPoints цикл и 6 точек | фаза 3 | цикл проверки по шагам |
| Live/Ready/Metrics, provider ≠ Ready | фаза 4 | без изменений по сути |
| Метрики vs логи vs трейс | фаза 4 | подраздел логов/агента |
| Запрет секретов в логах | стоп-факторы | детальный список |
| Не увеличивать sleep при гонке | стоп-фактор | повтор в фазе 6 |
| Порядок: Week3 → таблицы → replicas → FP | порядок работы | сохранён |

**Вывод:** да — содержательно план уже вёл туда же; после этого обновления памятка отражена **явно** (таблицы, логи/доказательство, порядок, DEAD/Lost Response, FailPoint-цикл). Реализация кода по-прежнему со **Фазы 1**.

---

## Главные риски (на старт реализации)

| Риск | Почему | Митигация |
|------|--------|-----------|
| Dual dispatcher без fencing | второй payment / lost response | Conditional owner/lease; HTTP вне claim |
| `DEAD` как domain reject | ложный REJECTED/FAILED | delivery DEAD; operation PROCESSING/WAITING_SIGNAL; late receipt |
| High-cardinality metrics | провал observability phase | IDs только в trace/logs |
| Failpoint timing на Docker pause | Exit 2 окружения, не дефект | не чинить flaky run ответственностью |
| Live stack `down -v` | потеря volume | только `down` без `-v`; отдельный checker project |
| Корневой `check.sh` | checker ожидает другой entry | меняем только `autocheck/`; root script не трогаем вслепую |
| Контракты diagnostics | в task-репо, не в решении | скопировать нужные schema в `contracts/` на фазе 4 |

---

## Следующий шаг реализации

Сначала **Фаза 0 (дозаполнение таблиц)** на 30–60 минут, затем **Фаза 1**: добавить `outbox-dispatcher-b` / `inbox-reconciler-b` и выровнять Compose/env под configuration contract Week 4, не останавливая live-стек через `down -v`.
