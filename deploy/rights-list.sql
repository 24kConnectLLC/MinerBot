SELECT CONCAT_WS(' ', 'global', PRIVILEGE_TYPE) FROM information_schema.USER_PRIVILEGES WHERE GRANTEE = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'schema', PRIVILEGE_TYPE, TABLE_SCHEMA) FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'table', PRIVILEGE_TYPE, TABLE_SCHEMA, TABLE_NAME) FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'column', PRIVILEGE_TYPE, TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME) FROM information_schema.COLUMN_PRIVILEGES WHERE GRANTEE = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'routine', Routine_type, Db, Routine_name, Proc_priv) FROM mysql.procs_priv WHERE CONCAT('''', User, '''@''', Host, '''') = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'proxy', Proxied_user, Proxied_host) FROM mysql.proxies_priv WHERE CONCAT('''', User, '''@''', Host, '''') = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'role', Role) FROM mysql.roles_mapping WHERE CONCAT('''', User, '''@''', Host, '''') = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'limits', max_user_connections, max_statement_time, max_questions, max_updates, max_connections) FROM mysql.user WHERE CONCAT('''', User, '''@''', Host, '''') = '{{WHO}}';
