# Задание ModuleDev, неделя 1

Сжатая формулировка курса для репозитория. Реализация — секция [Решение](README.md#решение) в корневом README.

## Цель

Собрать универсальный C# runtime, который публикует PostgreSQL-функции как HTTP-actions. Новый action появляется через catalog и manifest, без пересборки API и gateway. Клиент выбирает только действие и payload.

Паттерн: Database-first. Целостность и уникальность гарантирует PostgreSQL. C# исполняет общий пайплайн: маршрут, JWT, schema, транзакция, `api.invoke`. Предметные контроллеры на каждый action запрещены.

## Стек и контур

- C# / .NET 10 LTS, ASP.NET Core, PostgreSQL, Docker Compose
- четыре сервиса: `gateway`, `api`, `cli`, `postgres`
- единственный host-порт: gateway `8080`
- БД `course`, данные в named volume
- после `docker compose up -d --build` ручной SQL и ручная публикация встроенных actions не нужны

## HTTP

Внешний клиент ходит только на `http://localhost:8080`.

- gateway: whitelist actions, OpenAPI и health; без catalog и без PostgreSQL
- внутренний вызов api — по Compose DNS, не через localhost хоста
- один generic route: `POST /api/{module}/{action}`
- версия только в `X-Action-Version`
- `GET /health/live` — процесс gateway
- `GET /health/ready` — готовность внутреннего API
- JWT, credentials и полный payload не попадают в логи

## JWT и context

API читает `COURSE_JWT_ISSUER`, `COURSE_JWT_AUDIENCE`, `COURSE_JWT_SIGNING_KEY` (HS256, ≥ 32 байт).

Claims: `sub`, `consumer`, `scope`, `iat`, `exp`. Неверный JSON-тип claim → `401 auth.invalid`.

В `api.invoke` уходят `principal`, `consumer`, `scopes` только из проверенного JWT; `correlationId` — серверный UUID; `requestId` — Idempotency-Key; `deadline` — из `timeout_ms`.

## CLI

```
migration apply <directory>
action validate <manifest>
action publish <manifest>
action list
action activate <module.action> --version <version>
action disable <module.action> --version <version> [--replacement-version <version>]
```

Stdout — один JSON. Ошибка — ненулевой exit code. Publish той же версии с другим телом — conflict. Миграции — лексикографически, checksum, одна транзакция на файл.

## Обязательные actions

- `payment.request` v1: `payment:write`, идемпотентность required / `principal_action`, outcome `CREATED`
- `operation.get` v1: `payment:read`, идемпотентность none, outcome `FOUND`

Клиент не передаёт имя workflow и финальный status. `process_id` на неделе 1 равен `null`.

## Проверки курса

- OpenAPI default и per-version JSON
- readiness после восстановления PostgreSQL без пересоздания API
- пять read-only views `autocheck`
- runtime не меняет operations и не удаляет events в обход invoke
- owner SECURITY DEFINER имеет `NOLOGIN`
- повтор с тем же идемпотентным ключом не создаёт второй эффект
- новый зарегистрированный action не требует пересборки API

## Документы сдачи

В корневом README секция `## Решение` с подразделами:

1. Архитектура
2. Запуск
3. Конфигурация
4. Миграции
5. Проверка
6. Диагностика
7. Ограничения

Из секции Решение открываются C4 и два ADR.

## Незачёт

- контур не запускается по README
- основной API не на C#
- авторитетное состояние не в PostgreSQL
- предметные endpoints отдельными C# controllers
- клиент выбирает БД, schema, функцию или SQL
- повтор создаёт новый предметный эффект
- operation и event расходятся
- новый action требует пересборки API
- в репозитории или журналах реальные секреты

## Вне недели 1

workflow maps, process instances, worker, payment.submit, provider-simulator, Outbox/Inbox, receipts, manual actions, lease, retries, failpoints, Consul, CORS transformations, micro-cache, rate limiting, защитные очереди gateway.
