# ADR-0003. Generic worker: lease/fencing и одна транзакция invoke+finish

- Статус: принято
- Дата: 2026-09-01

## Контекст

Неделя 2 добавляет automatic-шаги workflow. HTTP-путь недели 1 открывает runtime-транзакцию, вызывает `api.invoke` и сам делает COMMIT. Если worker повторит этот путь, сценарий `after_action_before_finish` сломается: к моменту `docker kill` effect и `action_dispatches` уже зафиксированы, а `finish_job` не выполнен.

Нужны два экземпляра одного образа (`worker-a`, `worker-b`), видимый lease для reclaim и fencing по `leaseVersion`. Worker не должен читать catalog и писать в таблицы workflow напрямую.

## Решение

1. **Отдельный вход invoke.**  
   `ActionExecutor.InvokeOnConnectionAsync` вызывает `api.invoke` на уже открытой транзакции и не делает COMMIT/ROLLBACK. HTTP по-прежнему коммитит сам.

2. **Граница роли `workflow_worker`.**  
   EXECUTE только на `workflow.claim_jobs`, `api.invoke`, `workflow.finish_job`, `workflow.fail_job`. Манифест, schema и mapping приходят в JSON claim.

3. **Две транзакции на попытку.**  
   Claim — отдельный autocommit, чтобы lease был виден второму worker. Успех: invoke + проверка конверта + `finish_job` в одной транзакции, затем COMMIT. Ошибка: ROLLBACK invoke, затем отдельный `fail_job`. `workflow.lease_stale` не останавливает цикл.

4. **Доверенный контекст worker.**  
   `principal` = `workflow-worker`, `requestId` = `executionId`, `scopes` = pinned `required_policy` шага. Это не JWT с сети: worker не является HTTP-клиентом api.

5. **Failpoints.**  
   `after_job_claim` — после claim, до invoke.  
   `after_action_before_finish` — после успешного invoke, до `finish_job`, на открытой незакоммиченной транзакции.  
   Одна JSON-строка `failpoint.reached` в stdout, затем бесконечное ожидание.

## Следствия

- Kill на границе invoke/finish откатывает effect; другой worker может reclaim по истечении lease.
- STALE-попытка не тратит `max_attempts`: это отдельный статус attempt, не failure.
- Образы api и worker после публикации неизвестных action/карт не пересобираются.
- Цена: worker не ходит в HTTP api; policy для invoke собирается из карты, а не из JWT.
