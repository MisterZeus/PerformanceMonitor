# Availability Group test fixture

A two-node SQL Server 2022 Availability Group in Docker, for validating
PerformanceMonitor's AG collectors without needing a real cluster.

It exists because the AG DMVs return nothing at all on a standalone instance. Any
change to AG collection, AG alerting, or the AG topology views should be run against
this before it ships - that is the point of it, and it belongs in the release
checklist alongside the other install validation.

The AG is **clusterless** (`CLUSTER_TYPE = NONE`, also called a read-scale AG). That
gets real replicas, real synchronization state, real send/redo queues, and real
suspend/resume behavior with no WSFC or Pacemaker involved. What it does *not* get is
a listener or automatic failover, so it is not the fixture for testing those.

## Requirements

- Docker Desktop, Linux containers, engine running.
- ~4 GB of RAM free. Two SQL Server instances are not cheap.
- `docker.exe` is **not** on PATH in a default Docker Desktop install. The scripts
  default to `C:\Program Files\Docker\Docker\resources\bin\docker.exe`; pass
  `-DockerPath` if yours lives somewhere else.

## Start

```
copy .env.example .env
```

Edit `.env` if you want your own passwords, then:

```
.\setup.ps1
```

That brings up both containers, waits for them to accept logins, builds the AG, and
prints the replica and database state. Expect it to take a couple of minutes on a
cold start, most of it waiting for SQL Server to come up.

`setup.ps1` is idempotent - every script guards its own object, so re-running it
against a live fixture just reprints the verification output. Use `-SkipCompose` to
re-run only the SQL half against containers that are already up.

## Stop

```
.\teardown.ps1
```

`docker compose down -v`: containers, network, and both data volumes. Nothing
survives, and the next `setup.ps1` rebuilds from scratch. There is no "stop but keep
the data" verb on purpose - a half-torn-down AG is worse than no AG.

## Ports and names

| What | Value |
| --- | --- |
| ag1, primary replica | `localhost,14331` |
| ag2, secondary replica | `localhost,14332` |
| Login | `sa` / `MSSQL_SA_PASSWORD` from `.env` |
| Availability group | `ag_fixture` |
| Seeded database | `AgFixtureDb` |
| Mirroring endpoint | `ag_fixture_endpoint`, TCP 5022 (container network only) |
| Compose project / network | `ag-fixture` / `ag-fixture-net` |

Port 5022 is deliberately not published to the host. Replication happens over the
compose network between `tcp://ag1:5022` and `tcp://ag2:5022`, and those names only
resolve inside it.

## Pointing Lite or Darling at it

Add two servers, one per replica. Both use SQL authentication as `sa` with the
password from your `.env`, and both need **Trust Server Certificate** turned on - the
containers present a self-signed certificate.

- Primary: server name `localhost,14331`
- Secondary: server name `localhost,14332`

Use the comma, not a backslash: `14331` is a port, not a named instance.

Add both. A monitoring tool that only ever sees the primary cannot show you a send
queue backing up on a secondary, and that asymmetry is usually the thing you are
trying to test.

If you are connecting with `sqlcmd` from the host rather than through the containers:

```
sqlcmd -S localhost,14331 -U sa -P "<password from .env>" -C -Q "SELECT ag.name FROM sys.availability_groups AS ag;"
```

## Making the queues move

An idle AG reports zeros for every queue and rate column, which looks identical to a
broken collector. To get non-zero numbers:

```
.\write-load.ps1 -Seconds 30
```

That inserts batches into `AgFixtureDb.dbo.write_load` on ag1 for the duration and
prints how many rows it wrote.

## Exercising a fault

Suspending data movement on the secondary is the cheapest realistic fault, and it is
what `is_suspended` / `suspend_reason_desc` in
`sys.dm_hadr_database_replica_states` exist to report. Run these **on ag2**:

```
sqlcmd -S localhost,14332 -U sa -P "<password from .env>" -C -Q "ALTER DATABASE AgFixtureDb SET HADR SUSPEND;"
```

```
sqlcmd -S localhost,14332 -U sa -P "<password from .env>" -C -Q "ALTER DATABASE AgFixtureDb SET HADR RESUME;"
```

While suspended, the database grain reports `is_suspended = 1` with a
`suspend_reason_desc` of `SUSPEND_FROM_USER`, and the log send queue on the primary
grows if you run the write load. `RESUME` clears both. Recorded before/after output
is in [VALIDATION.md](VALIDATION.md).

## Layout

| File | Runs on | What it does |
| --- | --- | --- |
| `docker-compose.yml` | - | Both containers, HADR enabled, pinned hostnames |
| `sql/01_master_key_and_certificate.sql` | ag1 | Master key, endpoint certificate, exports it |
| `sql/02_restore_certificate.sql` | ag2 | Master key, imports ag1's certificate |
| `sql/03_endpoint.sql` | both | Mirroring endpoint on TCP 5022 |
| `sql/04_create_availability_group.sql` | ag1 | `CREATE AVAILABILITY GROUP ag_fixture` |
| `sql/05_join_availability_group.sql` | ag2 | `JOIN` plus `GRANT CREATE ANY DATABASE` |
| `sql/06_seed_database.sql` | ag1 | `AgFixtureDb`, full backup, `ADD DATABASE` |
| `sql/07_verify.sql` | ag1 | Replica and database state |
| `sql/90_write_load.sql` | ag1 | Insert loop |

One certificate is shared by both endpoints rather than one per replica. It is owned
by `dbo` on both sides, `dbo` in master maps to `sa`, so no separate endpoint login or
`GRANT CONNECT ON ENDPOINT` is needed.

## Warning

**`sa` and the passwords in `.env` are fixture-local.** They protect two throwaway
containers published on loopback and nothing else. Do not reuse them, do not point
this fixture at anything real, and do not commit `.env` - it is gitignored, and
`.env.example` ships placeholders precisely so no working password ever lands in the
repo.

## When it does not come up

- **A container never goes healthy.** Almost always the sa password failing SQL
  Server's policy check; the container exits immediately. `docker logs ag1`.
- **Replicas connect but the database never leaves `NOT SYNCHRONIZING`.** The
  `GRANT CREATE ANY DATABASE` on ag2 did not take. Without it automatic seeding
  cannot create the database on the secondary.
- **`connected_state_desc` is `DISCONNECTED`.** Endpoint reachability. Check both
  endpoints are `STARTED`, and that they are listening on all addresses rather than a
  single container IP.
- **`CREATE CERTIFICATE` on ag2 cannot find the file.** `docker cp` lands files owned
  by root and SQL Server runs as `mssql`. `setup.ps1` chowns them; if you did the copy
  by hand, do that too.
