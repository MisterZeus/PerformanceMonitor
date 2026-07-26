# AG collector validation against the fixture

**Date:** 2026-07-26
**Fixture:** `tools/ag-fixture`, two-node `CLUSTER_TYPE = NONE` availability group `ag_fixture`
**Instances:** SQL Server 2022, `16.0.4265.3` RTM, Developer Edition, `IsHadrEnabled = 1`
**Resources:** 1 CPU and 2 GB per container, engine capped at 1536 MB - see
[Resource footprint](#7-resource-footprint-measured)
**Collector source:** `feature/991-ag-health-collector` @ `530b3b66`, queries taken verbatim from
`PerformanceMonitor.Collectors/AgReplicaStatesCollector.cs` and
`PerformanceMonitor.Collectors/AgDatabaseReplicaStatesCollector.cs`

Both collector queries were run unmodified against both replicas. Both return real rows with
non-NULL queues and rates under load, and both track a suspend/resume fault correctly.

Three behaviors turned up that are not obvious from the DMV documentation. They are recorded in
full below because they change how the data should be read, and one of them contradicts MS Learn.

## Summary

| Check | Result |
| --- | --- |
| AG builds and reaches PRIMARY + SECONDARY, CONNECTED, HEALTHY | Pass |
| `setup.ps1` is idempotent (full re-run against a live fixture) | Pass |
| Replica-grain query returns rows on the primary | Pass, 2 rows |
| Database-grain query returns rows on the primary | Pass, 2 rows |
| Non-NULL queues and rates under write load | Pass |
| Suspend sets `is_suspended` + `suspend_reason_desc`, resume clears | Pass |
| Same queries from a *secondary* return the full AG | **No, 1 row - see finding 1** |
| `log_send_queue_size` grows while suspended | **No, reads NULL - see finding 2** |
| `secondary_lag_seconds` reads 0 while suspended, per MS Learn | **Neither - it accrues under load, and can read 0 for a whole outage when quiet. See finding 3** |
| A lag threshold can detect suspended data movement | **No, not on a quiet group - see finding 3** |
| `*_time` columns and drain estimates stay current while suspended | **No, all freeze - see 6b** |
| Whole fixture builds and runs within 1 CPU / 2 GB per container | Pass, no OOM kill |
| `teardown.ps1` removes containers, network, and both volumes | Pass |

Everything below was re-measured after the resource caps went on. Findings 1-3 were first
observed uncapped and reproduce identically at 1 CPU / 2 GB, which is expected - they are DMV
semantics, not resource effects.

## 1. Fixture state after setup

`sql/07_verify.sql` on ag1:

```
grain   ag_name    replica_server_name role      connected_state synchronization_health operational_state availability_mode  is_local
------- ---------- ------------------- --------- --------------- ---------------------- ----------------- ------------------ --------
replica ag_fixture ag1                 PRIMARY   CONNECTED       HEALTHY                ONLINE            SYNCHRONOUS_COMMIT 1
replica ag_fixture ag2                 SECONDARY CONNECTED       HEALTHY                NULL              SYNCHRONOUS_COMMIT 0

grain    ag_name    replica_server_name database_name role      synchronization_state is_suspended suspend_reason log_send_queue_kb redo_queue_kb
-------- ---------- ------------------- ------------- --------- --------------------- ------------ -------------- ----------------- -------------
database ag_fixture ag1                 AgFixtureDb   PRIMARY   SYNCHRONIZED          0            NULL           NULL              NULL
database ag_fixture ag2                 AgFixtureDb   SECONDARY SYNCHRONIZED          0            NULL           60                0
```

`operational_state_desc` and `recovery_health_desc` are NULL for the remote replica. That is
expected - those columns are only populated for the local replica - and it is worth knowing before
someone treats a NULL operational state as a fault.

Re-running `setup.ps1 -SkipCompose` against the live fixture completed with no errors and the same
output, so the idempotency guards in the numbered scripts hold.

## 2. Replica-grain query (`ag_replica_states`)

Run verbatim. On **ag1 (primary)** - 2 rows:

```
ag_name|replica_server_name|role_desc|operational_state_desc|connected_state_desc|recovery_health_desc|synchronization_health_desc|availability_mode_desc|failover_mode_desc|endpoint_url
ag_fixture|ag1|PRIMARY|ONLINE|CONNECTED|ONLINE|HEALTHY|SYNCHRONOUS_COMMIT|MANUAL|tcp://ag1:5022
ag_fixture|ag2|SECONDARY|NULL|CONNECTED|NULL|HEALTHY|SYNCHRONOUS_COMMIT|MANUAL|tcp://ag2:5022

(2 rows affected)
```

On **ag2 (secondary)** - 1 row:

```
ag_name|replica_server_name|role_desc|operational_state_desc|connected_state_desc|recovery_health_desc|synchronization_health_desc|availability_mode_desc|failover_mode_desc|endpoint_url
ag_fixture|ag2|SECONDARY|ONLINE|CONNECTED|ONLINE|HEALTHY|SYNCHRONOUS_COMMIT|MANUAL|tcp://ag2:5022

(1 rows affected)
```

### Finding 1: a secondary reports only itself

Row counts of each object backing the two queries:

```
                                      ag1   ag2
sys.availability_groups                 1     1
sys.availability_replicas               2     2
sys.dm_hadr_availability_replica_states 2     1
sys.dm_hadr_database_replica_states     2     1
```

The catalog view `sys.availability_replicas` knows about both replicas on both nodes. The two
*DMVs* only carry the local replica on the secondary, so the collectors' inner join to them narrows
a secondary's result to its own row. A clusterless AG has no cluster metadata store through which a
secondary could learn its partners' runtime state.

Consequences: a complete AG picture requires collecting from the primary; monitoring only a
secondary produces a one-row self-view with no primary visible in it; and after a failover the
replica that returns the full set changes. Anything that assumes "one row per replica per AG from
any monitored node" is wrong. This is also why the fixture README tells you to add both replicas as
servers.

## 3. Database-grain query (`ag_database_replica_states`)

Run verbatim, during write load. On **ag1 (primary)** - 2 rows, queues and rates non-NULL on the
remote row:

```
ag_name|database_name|replica_server_name|is_local|synchronization_state_desc|last_hardened_lsn|last_commit_lsn|log_send_queue_size|redo_queue_size|log_send_rate|redo_rate|is_suspended|suspend_reason_desc|availability_mode_desc|secondary_lag_seconds
ag_fixture|AgFixtureDb|ag1|1|SYNCHRONIZED|99000005517600001|99000005565600123|NULL|NULL|NULL|NULL|0|NULL|SYNCHRONOUS_COMMIT|NULL
ag_fixture|AgFixtureDb|ag2|0|SYNCHRONIZED|99000005517600001|99000004283200121|60|3976|484002000|95545|0|NULL|SYNCHRONOUS_COMMIT|0

(2 rows affected)
```

On **ag2 (secondary)** - 1 row (finding 1 again), local redo queue and rate populated:

```
ag_name|database_name|replica_server_name|is_local|synchronization_state_desc|last_hardened_lsn|last_commit_lsn|log_send_queue_size|redo_queue_size|log_send_rate|redo_rate|is_suspended|suspend_reason_desc|availability_mode_desc|secondary_lag_seconds
ag_fixture|AgFixtureDb|ag2|1|SYNCHRONIZED|99000007499200001|99000007275200119|60|1064|486229000|95713|0|NULL|SYNCHRONOUS_COMMIT|NULL

(1 rows affected)
```

The queue and rate columns move only with traffic on the wire. The load that produced the numbers
above was `write-load.ps1 -Seconds 130`: 5,270 batches, 2,635,000 rows.

The primary's own row (`is_local = 1`) carries NULL for every queue and rate. Those columns describe
shipping *to* a secondary, so there is nothing to report against the primary itself.
`secondary_lag_seconds` is likewise NULL on a replica's own local row and populated only on the
primary's view of a remote secondary.

## 4. Fault: suspend and resume data movement

`ALTER DATABASE [AgFixtureDb] SET HADR SUSPEND;` run **on ag2**, with write load running.

### Before suspend

```
replica_server_name|is_local|synchronization_state_desc|log_send_queue_size|redo_queue_size|log_send_rate|redo_rate|is_suspended|suspend_reason_desc|secondary_lag_seconds
ag1|1|SYNCHRONIZED|NULL|NULL|NULL|NULL|0|NULL|NULL
ag2|0|SYNCHRONIZED|60|3976|484002000|95545|0|NULL|0
```

### After suspend (from ag1, remote row)

```
replica_server_name|is_local|synchronization_state_desc|log_send_queue_size|redo_queue_size|log_send_rate|redo_rate|is_suspended|suspend_reason_desc|secondary_lag_seconds
ag1|1|SYNCHRONIZED|NULL|NULL|NULL|NULL|0|NULL|NULL
ag2|0|NOT SYNCHRONIZING|NULL|3920|488409000|95783|1|SUSPEND_FROM_USER|15
```

### After suspend (from ag2, local row)

```
replica_server_name|is_local|synchronization_state_desc|log_send_queue_size|redo_queue_size|log_send_rate|redo_rate|is_suspended|suspend_reason_desc|secondary_lag_seconds
ag2|1|NOT SYNCHRONIZING|NULL|1596|488084000|95765|1|SUSPEND_FROM_USER|NULL
```

`is_suspended` flips to 1 and `suspend_reason_desc` reads `SUSPEND_FROM_USER` on both vantage
points, and `synchronization_state_desc` goes to `NOT SYNCHRONIZING`.

### Replica grain during the same suspend (from ag1)

```
ag_name|replica_server_name|role_desc|operational_state_desc|connected_state_desc|recovery_health_desc|synchronization_health_desc|availability_mode_desc|failover_mode_desc|endpoint_url
ag_fixture|ag1|PRIMARY|ONLINE|CONNECTED|ONLINE|HEALTHY|SYNCHRONOUS_COMMIT|MANUAL|tcp://ag1:5022
ag_fixture|ag2|SECONDARY|NULL|CONNECTED|NULL|NOT_HEALTHY|SYNCHRONOUS_COMMIT|MANUAL|tcp://ag2:5022
```

The replica grain tracks the fault too: `synchronization_health_desc` on the ag2 row goes
`HEALTHY` -> `NOT_HEALTHY` while the database grain is suspended. Note `connected_state_desc` stays
`CONNECTED` - suspending data movement does not disconnect the replica, so connection state alone
does not detect this.

### After resume

```
replica_server_name|is_local|synchronization_state_desc|log_send_queue_size|redo_queue_size|log_send_rate|redo_rate|is_suspended|suspend_reason_desc|secondary_lag_seconds
ag1|1|SYNCHRONIZED|NULL|NULL|NULL|NULL|0|NULL|NULL
ag2|0|SYNCHRONIZED|60|4148|1123645|87487|0|NULL|0
```

`is_suspended` returns to 0, `suspend_reason_desc` to NULL, `synchronization_state_desc` to
`SYNCHRONIZED`.

## 5. Finding 2: `log_send_queue_size` reads NULL while suspended

Sampled on the primary's remote row across a 60-second suspend with write load running:

```
sample            is_suspended suspend_reason    sync_state       log_send_queue_size redo_queue_size
----------------- ------------ ----------------- ---------------- ------------------- ---------------
active/caught up  0            -                 SYNCHRONIZED     60                  3728
+15s suspended    1            SUSPEND_FROM_USER NOT SYNCHRONIZING NULL               4036
+30s suspended    1            SUSPEND_FROM_USER NOT SYNCHRONIZING NULL               4036
+45s suspended    1            SUSPEND_FROM_USER NOT SYNCHRONIZING NULL               4036
+60s suspended    1            SUSPEND_FROM_USER NOT SYNCHRONIZING NULL               4036
after resume      0            -                 SYNCHRONIZED     60                  388620
```

The send queue does not grow while movement is suspended - it reads NULL - and `redo_queue_size`
freezes at its last value instead of climbing. A "log send queue over N KB" rule is therefore blind
to a suspended secondary, which is one of the most common ways a secondary falls behind.
`is_suspended` / `suspend_reason_desc` / `synchronization_state_desc` are the signal for that
condition; queue thresholds only mean anything while movement is active. NULL must not be compared
as zero-and-healthy.

The `redo_queue_size` of 388,620 KB in the last row is legitimate post-resume catch-up, not a fault.
A single-sample redo-queue threshold will fire on it.

## 6. Finding 3: `secondary_lag_seconds` accrues while suspended, contradicting MS Learn

MS Learn documents this column as:

> The number of seconds that the secondary replica is behind the primary replica during
> synchronization. [...] **This value shows as `0` if the data movement is suspended.** The data
> movement needs to be in a non-suspended state in order for this value to show active lag.

Observed on the primary's remote row, same 60-second suspend:

```
sample            is_suspended sync_state        secondary_lag_seconds
----------------- ------------ ----------------- ---------------------
active/caught up  0            SYNCHRONIZED      0
+15s suspended    1            NOT SYNCHRONIZING 15
+30s suspended    1            NOT SYNCHRONIZING 31
+45s suspended    1            NOT SYNCHRONIZING 46
+60s suspended    1            NOT SYNCHRONIZING 62
after resume      0            SYNCHRONIZED      0
```

It accrues while suspended - the inverse of the documented behavior. It reads 0 when movement is
*active and caught up*, not when suspended.

So the blind spot the documentation implies does not exist here: a suspended secondary reports
growing lag, and a lag threshold fires on it unaided. Reading `is_suspended` alongside lag is still
the right thing to do, but to explain why lag is climbing rather than to catch lag that is being
masked.

### What the number actually measures, and why it is not "time since suspension"

The run above was under continuous write load, and started from 0. An independent reproduction on
this same fixture measured the identical accrual but starting near **3993**, on an AG that had been
idle (`redo_queue_size` 0 throughout). Re-testing the idle case explicitly reconciles the two:

```
sample                        is_suspended sync_state         lag  last_hardened  secs_since_hardened
----------------------------- ------------ ------------------ ---- -------------- -------------------
idle, not suspended           0            SYNCHRONIZED       0    19:00:29       373
idle, not suspended (+20s)    0            SYNCHRONIZED       0    19:00:29       393
idle, suspended +15s          1            NOT SYNCHRONIZING  0    19:00:29       409
idle, suspended +30s          1            NOT SYNCHRONIZING  450  19:00:29       424
idle, suspended +45s          1            NOT SYNCHRONIZING  465  19:00:29       440
after resume                  0            SYNCHRONIZED       0    19:07:49       15
```

While movement is **active**, lag reads 0 no matter how long the AG has been idle - 373 seconds
since the last hardening, still 0. Once **suspended**, the value latches onto roughly *how stale
the secondary's last hardened log is* (`now - last_hardened_time`, here ~450 against a measured 424)
and climbs from there second by second.

So the base is not zero in general. Under write load `last_hardened_time` is always near-now, so
lag starts at ~0 and looks like time-since-suspension; on an idle AG it starts at however long it
has been since the last write and can be thousands of seconds immediately. Both runs are the same
behavior seen from different starting points.

Two consequences for anything thresholding on this column:

- On an idle AG a lag rule can fire **instantly** on suspension with a large number that reflects
  idleness rather than data at risk. The magnitude is staleness of the last hardening, not a
  measure of how much data is behind - `log_send_queue_size` would be that, and it is NULL here
  (finding 2).
- The column does **not** update the moment movement stops, and **the delay is not bounded** - see
  the next section. A rule that treats a `0` from a suspended row as "caught up" can therefore
  clear an alarm mid-fault. Treating a suspended row as never able to *clear* an alarm - only
  raise one - is correct under both the measured and the documented behavior.

Scope of these claims: one build (`16.0.4265.3`), clusterless AG only. A WSFC-based AG was not
tested, and the fixture cannot test one. The accrual itself has now been reproduced independently
by a second party on this fixture, in both the loaded and idle cases.

### On a quiet group it may never latch at all, for the whole outage

An earlier revision of this file said the column "only latched at the +30s sample", which implied
a short bounded window. That was too generous, and it has been corrected. Two further runs on a
group with **no write load** - one by ag-alerts-builder, one reproduced here independently - found
it never latching at all:

```
elapsed  is_suspended  sync_state         lag  secs_since_hardened
-------  ------------  -----------------  ---  -------------------
healthy  0             SYNCHRONIZED       0    1757
0s       1             NOT SYNCHRONIZING  0    1757
+15s     1             NOT SYNCHRONIZING  0    1772
+30s     1             NOT SYNCHRONIZING  0    1788
+45s     1             NOT SYNCHRONIZING  0    1803
+60s     1             NOT SYNCHRONIZING  0    1818
resume   0             SYNCHRONIZED       0    16
```

Zero at every sample through a full 60-second suspension, already `NOT SYNCHRONIZING`, with the
last hardened log **nearly thirty minutes** stale. One earlier idle run did latch at +30s and
these did not, which is the point: **the timing is not dependable in either direction and nothing
should be built on it.**

The operational consequence is stronger than "there is a window":

> **A lag threshold alone cannot detect suspended data movement on a quiet group.** It can read 0
> for the entire outage.

The intuitive assumption is the opposite - that a lag alert covers suspension. It does not. A
dedicated suspended-state alert reading `is_suspended` / `synchronization_state_desc` is what owns
that case; lag thresholds only work while there is traffic to be behind on.

## 6b. Everything else on a suspended row is stale, not current

The `*_time` columns and the drain estimates were added to the collector after the original pass,
and the doc block cites this file for how they behave while suspended. Measured on the primary's
remote row.

**Under write load**, suspending the secondary and sampling every 15 seconds (ag-collector-builder's
run):

```
t    susp sync               lag send_q redo_q commit_t     hardened_t   redone_t     received_t est_redo_min est_send_min
---- ---- ------------------ --- ------ ------ ------------ ------------ ------------ ---------- ------------ ------------
0s   1    NOT SYNCHRONIZING  30  NULL   21896  19:08:26.687 19:08:26.930 19:08:26.687 NULL       0.014356     NULL
+15s 1    NOT SYNCHRONIZING  45  NULL   21896  (unchanged)  (unchanged)  (unchanged)  NULL       0.014356     NULL
+30s 1    NOT SYNCHRONIZING  60  NULL   21896  (unchanged)  (unchanged)  (unchanged)  NULL       0.014356     NULL
+45s 1    NOT SYNCHRONIZING  75  NULL   21896  (unchanged)  (unchanged)  (unchanged)  NULL       0.014356     NULL
```

**Idle**, reproduced here independently, same freeze:

```
sample   commit_t     hardened_t   redone_t     received_t est_redo_min est_send_min
-------- ------------ ------------ ------------ ---------- ------------ ------------
healthy  19:22:29.957 19:22:29.963 19:22:29.957 NULL       0            3.31e-006
0s       19:22:29.957 19:22:29.963 19:22:29.957 NULL       0            NULL
+60s     19:22:29.957 19:22:29.963 19:22:29.957 NULL       0            NULL
```

Four things, all pointing the same way - a suspended row reports the past, not the present:

- **All four `*_time` columns freeze** at their last pre-suspension instant and do not move again
  until resume. So `now - last_commit_time` computed across replicas stops growing exactly when
  replication stops, understating the problem at the moment it is worst. That is the opposite
  direction from `secondary_lag_seconds`, which is why the two must never be cross-checked against
  each other without reading `is_suspended` first.
- **`redo_queue_size` freezes** at its last value rather than growing (21896 KB, flat).
- **`est_redo_completion_time_min` is the sharpest edge.** It is queue divided by rate, and with
  both frozen it holds a small, static, reassuring number - 0.0144 min under load, 0 when idle -
  for the entire suspension, when the honest answer is "never, movement is stopped". Threshold it
  on its own and a suspended replica looks *healthier* than a working one.
  `est_send_drain_time_min` at least reads NULL, because its queue goes NULL.
- **`last_received_time` read NULL in every sample**, healthy and suspended alike, on both runs.
  Treat it as optional rather than expected.

### `last_commit_time` is not a heartbeat

Worth stating separately because it bites on *healthy* replicas, not just suspended ones. In the
idle baseline above the secondary was `SYNCHRONIZED`, `is_suspended = 0`, and
`secondary_lag_seconds = 0` - a perfectly healthy replica - while `last_commit_time` sat **1757
seconds (29 minutes)** behind wall clock, because nothing had committed on the database in that
time.

`last_commit_time` is the time of the last commit, not a liveness signal. So `now -
last_commit_time` is not a lag measure: on a quiet, entirely healthy replica it grows without
bound, and anything alerting on it will page about an idle database.

### Commit-time deltas fail in *both* directions, which is why they are not a lag measure

The two halves above are separated by a section, so the combined conclusion is easy to miss - and
the combination is the whole argument:

| Condition | What `now - last_commit_time` does | Failure |
| --- | --- | --- |
| Replica SUSPENDED | `last_commit_time` freezes, so the delta stops growing exactly when replication stops | **Silent** - understates at the moment it is worst |
| Replica healthy but database QUIET | Nothing commits, so the delta grows without bound (measured 1757s at zero real lag) | **Loud** - pages about an idle database |

Guarding only the suspended case therefore does not make a commit-delta trigger safe; it converts
a silent failure into a noisy one. And the loud half is the more likely to actually ship, because
it appears the first time anyone points the thing at a database nobody is writing to, whereas the
silent half only shows up if you happen to suspend something.

The conclusion is not "route commit deltas through the suspended-row gate" but **do not derive lag
from commit times at all**. `secondary_lag_seconds` is the lag measure, with the caveats in finding
3; the `*_time` columns are for showing an operator *when* something last happened, not for judging
whether it is late.

Source: [sys.dm_hadr_database_replica_states (Transact-SQL)](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-hadr-database-replica-states-transact-sql)

## 7. Resource footprint (measured)

The fixture is capped at **1 CPU and 2 GB per container**, with the engine held to 1536 MB by
`MSSQL_MEMORY_LIMIT_MB`, so it can share a machine with a busy VM fleet. These are measured
numbers from `docker stats --no-stream`, not the caps:

```
state                     container  CPU      memory              % of 2 GB cap
------------------------  ---------  -------  ------------------  -------------
idle, just after setup    ag1        3.82%    1.345GiB / 2GiB     67.25%
                          ag2        4.06%    1.325GiB / 2GiB     66.25%
under write load          ag1        71.69%   1.718GiB / 2GiB     85.91%
                          ag2        99.38%   1.689GiB / 2GiB     84.43%
settled, after load       ag1        2.75%    1.78GiB / 2GiB      88.98%
                          ag2        4.33%    1.798GiB / 2GiB     89.88%
```

Two containers, so roughly **3.5 GB and 2 CPUs in total** at the working peak.

Both instances built the AG, seeded, ran load, and survived a suspend/resume fault at these
limits with no OOM kill and no restart - `docker ps` showed both healthy throughout. 1536 MB was
stable, so it was not stepped up to 2048.

Memory settles near 90% of the cap after load and stays there. That is normal: SQL Server does
not hand the buffer pool back, and `MSSQL_MEMORY_LIMIT_MB` governs the engine's own budget rather
than the container's total RSS, so ~250 MB of process overhead sits on top of the 1536 MB. It is
the reason `mem_limit` is 2 GB and not lower. SQL Server on Linux also refuses to start at all
below ~2000 MB, so 2 GB is a floor, not a preference.

### Database files stay small

Fresh, and after a 70-second write load:

```
                     size_mb  used_mb
fresh after setup
  AgFixtureDb        64       2
  AgFixtureDb_log    64       0
after 70s of load
  AgFixtureDb        672      654
  AgFixtureDb_log    64       15
```

The log does not grow. That is the periodic overwriting `BACKUP LOG` in
`sql/90_write_load.sql` doing its job - the database must be in FULL recovery for the AG to
accept it, so the log cannot truncate on its own. Without that backup the same workload took the
log to **1632 MB used**, and a data file to 1376 MB, in a single 90-second run:

```
                     size_mb  used_mb
before the log trim was added
  AgFixtureDb        1376     1351
  AgFixtureDb_log    1632     1620
```

The data file still grows with the rows inserted; the start-of-run `TRUNCATE TABLE` past 250,000
rows keeps that from compounding across runs. `teardown.ps1` deletes both volumes and reclaims
everything.

## Reproducing

```
cd tools\ag-fixture
copy .env.example .env
.\setup.ps1
.\write-load.ps1 -Seconds 60
```

Then run either collector query through the container, for example:

```
docker exec -i ag1 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<password>" -C -W -s "|" -Q "SELECT ag_name = ag.name, replica_server_name = ar.replica_server_name, role_desc = ars.role_desc FROM sys.availability_replicas AS ar JOIN sys.availability_groups AS ag ON ar.group_id = ag.group_id JOIN sys.dm_hadr_availability_replica_states AS ars ON ar.replica_id = ars.replica_id;"
```

Suspend and resume, on ag2:

```
docker exec -i ag2 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<password>" -C -Q "ALTER DATABASE AgFixtureDb SET HADR SUSPEND;"
```

```
docker exec -i ag2 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "<password>" -C -Q "ALTER DATABASE AgFixtureDb SET HADR RESUME;"
```
