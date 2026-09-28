# C4: ReliaPay — контейнеры

Два уровня: контекст системы и контейнеры Compose. Компонентный уровень C# намеренно не рисуется здесь — его роль описана в [ADR-0001](adr/0001-trust-boundary.md), [ADR-0003](adr/0003-worker-lease-transaction.md) и [ADR-0004](adr/0004-python-anti-corruption.md).

## Уровень 1. Контекст

Внешний клиент (автопроверка, интеграционные тесты, ручной `curl`) обращается только к ReliaPay. PostgreSQL — часть системы, не отдельный SaaS. Операции публикации выполняет человек или CI через Course CLI, а не через публичный HTTP. Generic worker исполняет automatic-шаги внутри сети Compose. Выданный provider-simulator — внешний по контракту участник: его видит только Python-периметр.

```mermaid
flowchart LR
  client["Клиент<br/>JWT HS256"]
  operator["Оператор / CI<br/>Course CLI"]
  system["ReliaPay<br/>runtime, workflow, периметр"]
  db[("PostgreSQL<br/>БД course")]
  provider["provider-simulator<br/>v0.2.0"]

  client -->|"HTTP :8080<br/>actions, OpenAPI, health"| system
  operator -->|"publication-роль<br/>миграции, catalog, карты"| db
  system -->|"runtime-роль<br/>api.invoke"| db
  system -->|"workflow_worker<br/>claim / invoke / finish / fail"| db
  system -->|"outbox_dispatcher / inbox_reconciler<br/>только delivery.*"| db
  system -->|"POST /payments"| provider
  provider -->|"legacy callback"| system
```

Клиент не видит имена функций, схемы и SQL. Он выбирает только `module` / `action` и JSON-payload. Worker не читает catalog таблицами: манифест приходит из `claim_jobs`. Python не выбирает flow, limit и финальный status.

## Уровень 2. Контейнеры

Десять процессов Docker Compose. На хост опубликован только порт gateway `8080`. `worker-a` и `worker-b` — два экземпляра одного образа. Три Python-сервиса — три процесса одного образа `reliapay-python-perimeter:local`.

```mermaid
flowchart TB
  client["Клиент"]

  subgraph compose["Docker Compose: reliapay"]
    gw["gateway<br/>ASP.NET Core<br/>whitelist + proxy"]
    api["api<br/>ASP.NET Core<br/>JWT, catalog, HMAC, транзакция"]
    cli["cli<br/>.NET<br/>миграции, catalog, карты"]
    wa["worker-a<br/>.NET runtime<br/>lease owner worker-a"]
    wb["worker-b<br/>тот же image<br/>lease owner worker-b"]
    dispatcher["outbox-dispatcher<br/>Python 3.12<br/>delivery.claim_outbox"]
    adapter["receipt-adapter<br/>Python 3.12<br/>антикоррупционный слой"]
    reconciler["inbox-reconciler<br/>Python 3.12<br/>delivery.reconcile_inbox"]
    provider["provider-simulator<br/>Go image v0.2.0<br/>без build"]
    pg[("postgres<br/>PostgreSQL 16<br/>volume course_data")]
  end

  client -->|"host :8080"| gw
  gw -->|"http://api:8080<br/>внутренний DNS"| api
  api -->|"course_runtime"| pg
  cli -->|"course_publication"| pg
  wa -->|"workflow_worker"| pg
  wb -->|"workflow_worker"| pg
  dispatcher -->|"outbox_dispatcher"| pg
  dispatcher -->|"POST /payments"| provider
  provider -->|"callback :8080"| adapter
  adapter -->|"POST /api/receipt/accept"| gw
  reconciler -->|"inbox_reconciler"| pg
```

| Контейнер | Технология | Ответственность | Порт на хост |
| --- | --- | --- | --- |
| `gateway` | ASP.NET Core / .NET 10 | Единственный вход, whitelist, прокси, live/ready | `8080` |
| `api` | ASP.NET Core / .NET 10 | JWT, schema, HMAC над исходными bytes, `api.invoke` | нет |
| `cli` | .NET 10 runtime | Checksum-миграции, catalog и `flow` | нет |
| `worker-a` | .NET 10 runtime | Claim/invoke/finish/fail, owner `worker-a` | нет |
| `worker-b` | тот же image | То же, owner `worker-b` | нет |
| `outbox-dispatcher` | Python 3.12 | Одна HTTP-попытка на claim, succeed/fail Outbox | нет |
| `receipt-adapter` | Python 3.12 | Legacy callback → signed receipt v1, без credentials БД | нет |
| `inbox-reconciler` | Python 3.12 | Poll `delivery.reconcile_inbox` | нет |
| `provider-simulator` | выданный Go image | `POST /payments` и legacy callback | нет |
| `postgres` | PostgreSQL 16 | БД `course`, роли, WAL, named volume | нет |

Направление клиентского HTTP неизменно: клиент → gateway → api → PostgreSQL. Worker ходит в PostgreSQL напрямую ролью `workflow_worker`, минуя HTTP. Adapter ходит только в gateway. Dispatcher ходит в provider и в функции `delivery.*`.

`api` и оба worker зависят от healthy `cli` (миграции уже применены) и healthy `postgres`. `gateway` зависит от healthy `api`. Перезапуск трёх Python-контейнеров не требует пересборки api/worker: очередь доставки остаётся в PostgreSQL.

## Что контейнеры сознательно не делают

| Контейнер | Не делает |
| --- | --- |
| `gateway` | Не проверяет JWT, не читает catalog, не открывает Npgsql |
| `api` | Не применяет миграции, не вызывает `publish_action` / `publish_flow`, не берёт publication-пароль |
| `cli` | Не слушает HTTP и не исполняет job |
| `worker-a` / `worker-b` | Не делают table DML, не читают catalog SELECT, не публикуют порт |
| `outbox-dispatcher` | Не выбирает flow/limit/status, не делает table DML, не вызывает `api.invoke` |
| `receipt-adapter` | Не хранит callback как истину, не получает `PG*` / `DB_*`, не ACK до ответа API |
| `inbox-reconciler` | Не пишет в таблицы Inbox напрямую, не обгоняет `wait_signal` |
| `provider-simulator` | Не является отправителем native receipt v1 и не собирается из этого репозитория |
| `postgres` | Не публикует порт на хост в локальной конфигурации |

Подробности границы доверия — [ADR-0001](adr/0001-trust-boundary.md). Различие HTTP-конверта и outcome функции — [ADR-0002](adr/0002-technical-vs-domain-result.md). Lease и транзакция worker — [ADR-0003](adr/0003-worker-lease-transaction.md). Антикоррупционный слой и ACL Python — [ADR-0004](adr/0004-python-anti-corruption.md).
