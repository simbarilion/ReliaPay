-- Runtime читает опубликованный каталог для HTTP-исполнителя и OpenAPI, без DML.

GRANT SELECT ON TABLE api.actions TO course_runtime;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE api.actions FROM course_runtime;
