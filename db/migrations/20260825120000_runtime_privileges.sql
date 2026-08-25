-- Привилегии runtime без прямого DML. DDL выполняется от имени course_owner.
-- Повтор с тем же checksum безопасен; правки этого файла после apply дадут conflict.

REVOKE ALL ON ALL TABLES IN SCHEMA api FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA app FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA autocheck FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA api
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA app
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA autocheck
    REVOKE ALL ON TABLES FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE course_owner IN SCHEMA app
    REVOKE ALL ON SEQUENCES FROM PUBLIC;

GRANT SELECT ON TABLE api.schema_migrations TO course_publication;

COMMENT ON SCHEMA api IS 'Каталог действий, api.invoke и журнал миграций';
COMMENT ON SCHEMA app IS 'Предметные таблицы; изменение только через зарегистрированные функции';
COMMENT ON SCHEMA autocheck IS 'Read-only проекции для black-box проверки';
