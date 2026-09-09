# ADR-0004. Антикоррупционный слой Python и ACL доставки

- Статус: принято
- Дата: 2026-09-09

## Контекст

Неделя 3 подключает выданный provider-simulator v0.2.0. Он говорит на legacy callback (`providerPaymentId`, `operationId`, `result`, `message`, `occurredAt`) и не подписывает receipt v1. Ядро ReliaPay уже умеет generic HTTP, HMAC над исходными body bytes и Inbox/Outbox в PostgreSQL.

Если Python начнёт выбирать flow, limit, transition или финальный status, граница недели 1–2 размоется: периметр станет оркестратором, а карты в PostgreSQL — декорацией. Если adapter получит учётные данные БД, callback-контур сможет писать в таблицы напрямую. Если dispatcher или reconciler получат table DML, checksum-роли `outbox_dispatcher` / `inbox_reconciler` перестанут быть ACL.

Нужен антикоррупционный слой: перевод провайдера во внутренний контракт и узкий SQL-доступ только к фиксированным функциям `delivery.*`.

## Решение

1. **Три процесса, один образ.**  
   `outbox-dispatcher`, `receipt-adapter`, `inbox-reconciler` собираются из `reliapay-python-perimeter:local`. PID 1 — `python -m perimeter`. Host-портов нет. Авторитетное состояние живёт в PostgreSQL: force-recreate трёх сервисов продолжает работу с Outbox/Inbox, а не из памяти процесса.

2. **Adapter — переводчик, не источник истины.**  
   Слушает `POST /callbacks/provider-v02/{PROVIDER_CALLBACK_CAPABILITY}`. Неверный capability даёт 404 с пустым телом; битое тело — 400 с пустым телом. Валидный callback мапится в compact sorted receipt v1, HMAC-SHA256 считается над этими байтами ключом `PROVIDER_HMAC_SECRET` без trim. Затем `POST /api/receipt/accept` через gateway. Adapter не держит PostgreSQL credentials, не ACK-ает провайдера до ответа API и не хранит callback как истину.

3. **Подпись на границе C#.**  
   Gateway передаёт `X-Provider-Signature` без изменения тела. Runtime считает HMAC над исходными HTTP body bytes. В `TrustedContext` попадают только `transport.signatureVerified` и `transport.signatureVersion`. Секрет и полная подпись в context и в логи не входят. `receipt.accept` без verified transport не меняет Inbox.

4. **SQL ACL доставки.**  
   Роль `outbox_dispatcher` вызывает только `delivery.claim_outbox`, `delivery.succeed_outbox`, `delivery.fail_outbox`. Роль `inbox_reconciler` вызывает только `delivery.reconcile_inbox`. Table DML, memberships, `api.invoke` и функции `app.*` / `workflow.*` этим ролям не выдаются. Python не выбирает flow, limit, transition и финальный status операции.

5. **Журналы.**  
   Периметр не пишет application-лог: ни полный callback JSON, ни `message` провайдера, ни reason ручного решения, ни секреты. HTTP-трассировка C# — метод, маршрут и статус.

## Следствия

- Пересборка только Python-образа не меняет image id контейнеров `api` / `worker-a` / `worker-b`.
- Ранний callback остаётся Inbox `RECEIVED`, пока process не войдёт в `wait_signal`; reconciler не обгоняет карту.
- Цена: лишний HTTP-хоп adapter → gateway → api и отдельный poll reconciler. Это принимается ради проверяемой антикоррупционной границы.

См. также [ADR-0001](0001-trust-boundary.md): gateway по-прежнему не читает JWT и catalog.
