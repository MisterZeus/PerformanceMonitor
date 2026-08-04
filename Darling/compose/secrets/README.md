One secret per file, no trailing content beyond the value (a trailing newline is fine — references trim):

- `store_connection` — the full store connection string, e.g. `Host=store;Port=5432;Username=darling;Password=<store password>;Database=darling`
- `store_password.txt` — the same store password on its own, for the TimescaleDB container's `POSTGRES_PASSWORD_FILE`
- `sql_password.txt` — the monitoring login's SQL Server password
- `web_token.txt` / `mcp_token.txt` — the dashboard/MCP access tokens (generate long random values)

Keep this directory out of version control and readable only by the deploying user (`chmod 700 secrets`, `chmod 600 secrets/*`).
