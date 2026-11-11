SELECT CONCAT_WS(' ', 'global', PRIVILEGE_TYPE, IF(IS_GRANTABLE = 'YES', 'grantable', NULL)) FROM information_schema.USER_PRIVILEGES WHERE GRANTEE = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'schema', PRIVILEGE_TYPE, TABLE_SCHEMA, IF(IS_GRANTABLE = 'YES', 'grantable', NULL)) FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'table', PRIVILEGE_TYPE, TABLE_SCHEMA, TABLE_NAME, IF(IS_GRANTABLE = 'YES', 'grantable', NULL)) FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'column', PRIVILEGE_TYPE, TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, IF(IS_GRANTABLE = 'YES', 'grantable', NULL)) FROM information_schema.COLUMN_PRIVILEGES WHERE GRANTEE = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'routine', Routine_type, Db, Routine_name, Proc_priv) FROM mysql.procs_priv WHERE CONCAT('''', User, '''@''', Host, '''') = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'proxy', Proxied_user, Proxied_host, IF(With_grant, 'grantable', NULL)) FROM mysql.proxies_priv WHERE CONCAT('''', User, '''@''', Host, '''') = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'role', Role, IF(Admin_option = 'Y', 'admin', NULL)) FROM mysql.roles_mapping WHERE CONCAT('''', User, '''@''', Host, '''') = '{{WHO}}'
UNION ALL SELECT CONCAT_WS(' ', 'limits', max_user_connections, max_statement_time, max_questions, max_updates, max_connections) FROM mysql.user WHERE CONCAT('''', User, '''@''', Host, '''') = '{{WHO}}';
