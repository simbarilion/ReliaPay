# ADR-0001. Граница доверия: тонкий gateway и внутренний runtime

- Статус: принято
- Дата: 2026-08-25

## Контекст

Клиент курса видит один HTTP-вход. Нужно одновременно:

- не отдавать наружу PostgreSQL, внутренние DNS-имена и каталог действий;
- не заставлять gateway знать предметные маршруты, JWT и SQL;
- сохранить возможность публиковать новый action без пересборки образов.

Если шлюз начнёт разбирать JWT, читать `api.actions` или вызывать функции PostgreSQL, граница доверия размоется: публичный процесс станет оркестратором, а внутренний api — лишней копией той же логики.

## Решение

Доверенный контур делится на три зоны.

1. **Недоверенный край — `gateway`.**  
   Принимает только whitelist: `POST /api/{module}/{action}`, OpenAPI и health.  
   Не читает JWT, catalog и PostgreSQL.  
   Формирует серверный `correlationId` и подменяет клиентский `X-Correlation-Id`.  
   Проксирует `Authorization`, `X-Action-Version`, `Idempotency-Key`, `X-Provider-Signature` и `Content-Type` без изменения смысла.  
   Liveness — сам процесс. Readiness — готовность внутреннего api.

2. **Доверенный runtime — `api`.**  
   Проверяет JWT и JSON-типы claims.  
   Собирает `TrustedContext` только из проверенного токена. При валидном `X-Provider-Signature` добавляет только `transport.signatureVerified` и `transport.signatureVersion`; HMAC считается над исходными body bytes.  
   Читает catalog ролью `course_runtime`, валидирует payload по манифесту, открывает одну транзакцию и вызывает `api.invoke`.  
   Не использует учётные данные publication.

3. **Источник истины — PostgreSQL.**  
   Каталог, идемпотентность, operation и event живут в БД.  
   `api.invoke` повторно проверяет policy и вызывает только зарегистрированный target.  
   Клиент не выбирает schema, функцию или SQL.

Course CLI стоит отдельно: publication-роль, миграции и мутации каталога. Это операторский контур, не публичный HTTP.

## Следствия

- Новый action появляется как SQL-функция + manifest + `action publish` / `action activate`. Образы gateway и api пересобирать не нужно.
- Компрометация gateway не даёт прямого доступа к таблицам `app.*` и к publication-паролю.
- Цена решения: два сетевых хопа и отдельный health-контракт. Это принимается ради проверяемой границы доверия.
- Generic worker недели 2 не расширяет gateway: он ходит в PostgreSQL ролью `workflow_worker`. См. [ADR-0003](0003-worker-lease-transaction.md).
