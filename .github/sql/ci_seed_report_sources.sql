/*
Copyright 2026 Darling Data, LLC
https://www.erikdarling.com/

CI seed rows for the report.* view execution sweep (#1669).

The existence check alone let two view bugs ship (#1635's bit->nvarchar Msg 245, #1666's binary
boxed into sql_variant): a view can compile and exist yet throw only when rows flow through its
projection. These seeds put one representative row in every collect/config table (generated from
the installed schema: every NOT NULL, non-identity, non-computed column gets a type-appropriate
value; nullable columns stay NULL so NULL branches execute too), timestamped a few minutes back so
"today"/"last hour" windows include them. History tables that feed LAG/toggle views get a
hand-authored correlated PAIR below, so the change-detection paths produce actual output rows.

Each INSERT is guarded on the table being empty: re-runnable anywhere, inert on a store that
already has data. CI runs this after a fresh install, immediately before ci_execute_report_views.sql.

Regenerating after schema changes: the generator lives in the PR that added this (see #1669) -
metadata-driven, so adding a table means re-running it or hand-adding one guarded INSERT here.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

USE PerformanceMonitor;
GO

/* ---------------- generated: one guarded row per collect/config table ---------------- */

IF NOT EXISTS (SELECT 1/0 FROM [collect].[blocked_process_xml] WHERE 1 = 1) INSERT INTO [collect].[blocked_process_xml] ([collection_time], [blocked_process_xml], [is_processed]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), CONVERT(xml, N'<ci/>'), 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[blocking_BlockedProcessReport] WHERE 1 = 1) INSERT INTO [collect].[blocking_BlockedProcessReport] ([collection_time], [blocked_process_report]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci');
IF NOT EXISTS (SELECT 1/0 FROM [collect].[blocking_deadlock_stats] WHERE 1 = 1) INSERT INTO [collect].[blocking_deadlock_stats] ([collection_time], [database_name], [blocking_event_count], [total_blocking_duration_ms], [max_blocking_duration_ms], [deadlock_count], [total_deadlock_wait_time_ms], [victim_count]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[cpu_scheduler_stats] WHERE 1 = 1) INSERT INTO [collect].[cpu_scheduler_stats] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[cpu_utilization_stats] WHERE 1 = 1) INSERT INTO [collect].[cpu_utilization_stats] ([collection_time], [sample_time], [sqlserver_cpu_utilization]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[database_size_stats] WHERE 1 = 1) INSERT INTO [collect].[database_size_stats] ([collection_time], [database_name], [database_id], [file_id], [file_type_desc], [file_name], [physical_name], [total_size_mb]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, 1, N'ci', N'ci', N'ci', 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[deadlock_xml] WHERE 1 = 1) INSERT INTO [collect].[deadlock_xml] ([collection_time], [deadlock_xml], [is_processed]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), CONVERT(xml, N'<ci/>'), 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[deadlocks] WHERE 1 = 1) INSERT INTO [collect].[deadlocks] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[default_trace_events] WHERE 1 = 1) INSERT INTO [collect].[default_trace_events] ([collection_time], [event_time], [event_name], [event_class]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[dmv_blocking_snapshots] WHERE 1 = 1) INSERT INTO [collect].[dmv_blocking_snapshots] ([collection_time], [monitor_loop], [event_time], [spid], [ecid], [blocking_spid], [blocking_ecid]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 1, DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[file_io_stats] WHERE 1 = 1) INSERT INTO [collect].[file_io_stats] ([collection_time], [server_start_time], [database_id], [file_id]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_CPUTasks] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_CPUTasks] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_IOIssues] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_IOIssues] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_MemoryBroker] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_MemoryBroker] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_MemoryConditions] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_MemoryConditions] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_MemoryNodeOOM] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_MemoryNodeOOM] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_SchedulerIssues] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_SchedulerIssues] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_SevereErrors] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_SevereErrors] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_SignificantWaits] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_SignificantWaits] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_SystemHealth] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_SystemHealth] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_WaitsByCount] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_WaitsByCount] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[HealthParser_WaitsByDuration] WHERE 1 = 1) INSERT INTO [collect].[HealthParser_WaitsByDuration] ([collection_time]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[index_object_stats] WHERE 1 = 1) INSERT INTO [collect].[index_object_stats] ([collection_time], [database_name], [database_id], [schema_name], [object_id], [table_name], [index_id]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, N'ci', 1, N'ci', 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[latch_stats] WHERE 1 = 1) INSERT INTO [collect].[latch_stats] ([collection_time], [server_start_time], [latch_class], [waiting_requests_count], [wait_time_ms], [max_wait_time_ms]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[memory_clerks_stats] WHERE 1 = 1) INSERT INTO [collect].[memory_clerks_stats] ([collection_time], [server_start_time], [clerk_type], [memory_node_id]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[memory_grant_stats] WHERE 1 = 1) INSERT INTO [collect].[memory_grant_stats] ([collection_time], [server_start_time], [resource_semaphore_id], [pool_id]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[memory_pressure_events] WHERE 1 = 1) INSERT INTO [collect].[memory_pressure_events] ([collection_time], [sample_time], [memory_notification], [memory_indicators_process], [memory_indicators_system]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[memory_stats] WHERE 1 = 1) INSERT INTO [collect].[memory_stats] ([collection_time], [buffer_pool_mb], [plan_cache_mb], [other_memory_mb], [total_memory_mb], [physical_memory_in_use_mb], [available_physical_memory_mb], [memory_utilization_percentage], [buffer_pool_pressure_warning], [plan_cache_pressure_warning]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[perfmon_stats] WHERE 1 = 1) INSERT INTO [collect].[perfmon_stats] ([collection_time], [server_start_time], [object_name], [counter_name], [instance_name], [cntr_value], [cntr_type]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', N'ci', 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[plan_cache_stats] WHERE 1 = 1) INSERT INTO [collect].[plan_cache_stats] ([collection_time], [cacheobjtype], [objtype], [total_plans], [total_size_mb], [single_use_plans], [single_use_size_mb], [multi_use_plans], [multi_use_size_mb], [avg_use_count], [avg_size_kb]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', 1, 1, 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[procedure_stats] WHERE 1 = 1) INSERT INTO [collect].[procedure_stats] ([collection_time], [server_start_time], [object_type], [database_name], [object_id], [sql_handle], [plan_handle], [cached_time], [last_execution_time], [execution_count], [total_worker_time], [min_worker_time], [max_worker_time], [total_elapsed_time], [min_elapsed_time], [max_elapsed_time], [total_logical_reads], [min_logical_reads], [max_logical_reads], [total_physical_reads], [min_physical_reads], [max_physical_reads], [total_logical_writes], [min_logical_writes], [max_logical_writes]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', 1, 0x00, 0x00, DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[procedure_stats_latest_hash] WHERE 1 = 1) INSERT INTO [collect].[procedure_stats_latest_hash] ([database_name], [object_id], [plan_handle], [row_hash], [last_seen]) VALUES (N'ci', 1, 0x00, 0x00, DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[query_stats] WHERE 1 = 1) INSERT INTO [collect].[query_stats] ([collection_time], [server_start_time], [object_type], [database_name], [sql_handle], [statement_start_offset], [statement_end_offset], [plan_generation_num], [plan_handle], [creation_time], [last_execution_time], [execution_count], [total_worker_time], [min_worker_time], [max_worker_time], [total_physical_reads], [min_physical_reads], [max_physical_reads], [total_logical_writes], [total_logical_reads], [total_clr_time], [total_elapsed_time], [min_elapsed_time], [max_elapsed_time], [total_rows], [min_rows], [max_rows], [min_dop], [max_dop], [min_grant_kb], [max_grant_kb], [min_used_grant_kb], [max_used_grant_kb], [min_ideal_grant_kb], [max_ideal_grant_kb], [min_reserved_threads], [max_reserved_threads], [min_used_threads], [max_used_threads], [total_spills], [min_spills], [max_spills]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', 0x00, 1, 1, 1, 0x00, DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[query_stats_latest_hash] WHERE 1 = 1) INSERT INTO [collect].[query_stats_latest_hash] ([sql_handle], [statement_start_offset], [statement_end_offset], [plan_handle], [row_hash], [last_seen]) VALUES (0x00, 1, 1, 0x00, 0x00, DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[query_stats_old] WHERE 1 = 1) INSERT INTO [collect].[query_stats_old] ([collection_time], [server_start_time], [object_type], [database_name], [sql_handle], [statement_start_offset], [statement_end_offset], [plan_generation_num], [plan_handle], [creation_time], [last_execution_time], [execution_count], [total_worker_time], [min_worker_time], [max_worker_time], [total_physical_reads], [min_physical_reads], [max_physical_reads], [total_logical_writes], [total_logical_reads], [total_clr_time], [total_elapsed_time], [min_elapsed_time], [max_elapsed_time], [total_rows], [min_rows], [max_rows], [min_dop], [max_dop], [min_grant_kb], [max_grant_kb], [min_used_grant_kb], [max_used_grant_kb], [min_ideal_grant_kb], [max_ideal_grant_kb], [min_reserved_threads], [max_reserved_threads], [min_used_threads], [max_used_threads], [total_spills], [min_spills], [max_spills]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', 0x00, 1, 1, 1, 0x00, DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[query_store_data] WHERE 1 = 1) INSERT INTO [collect].[query_store_data] ([collection_time], [database_name], [query_id], [plan_id], [utc_first_execution_time], [utc_last_execution_time], [server_first_execution_time], [server_last_execution_time], [count_executions], [avg_duration], [min_duration], [max_duration], [avg_cpu_time], [min_cpu_time], [max_cpu_time], [avg_logical_io_reads], [min_logical_io_reads], [max_logical_io_reads], [avg_logical_io_writes], [min_logical_io_writes], [max_logical_io_writes], [avg_physical_io_reads], [min_physical_io_reads], [max_physical_io_reads], [avg_clr_time], [min_clr_time], [max_clr_time], [min_dop], [max_dop], [avg_query_max_used_memory], [min_query_max_used_memory], [max_query_max_used_memory], [avg_rowcount], [min_rowcount], [max_rowcount], [is_forced_plan]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[query_store_data_latest_hash] WHERE 1 = 1) INSERT INTO [collect].[query_store_data_latest_hash] ([database_name], [query_id], [plan_id], [row_hash], [last_seen]) VALUES (N'ci', 1, 1, 0x00, DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [collect].[running_jobs] WHERE 1 = 1) INSERT INTO [collect].[running_jobs] ([collection_time], [server_start_time], [job_name], [job_id], [job_enabled], [start_time], [current_duration_seconds], [is_running_long]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', NEWID(), 1, DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[server_properties] WHERE 1 = 1) INSERT INTO [collect].[server_properties] ([collection_time], [server_name], [edition], [product_version], [product_level], [engine_edition], [cpu_count], [hyperthread_ratio], [physical_memory_mb]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', N'ci', N'ci', 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[session_stats] WHERE 1 = 1) INSERT INTO [collect].[session_stats] ([collection_time], [total_sessions], [running_sessions], [sleeping_sessions], [background_sessions], [dormant_sessions], [idle_sessions_over_30min], [sessions_waiting_for_memory], [databases_with_connections]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[spinlock_stats] WHERE 1 = 1) INSERT INTO [collect].[spinlock_stats] ([collection_time], [server_start_time], [spinlock_name], [collisions], [spins], [spins_per_collision], [sleep_time], [backoffs]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[tempdb_stats] WHERE 1 = 1) INSERT INTO [collect].[tempdb_stats] ([collection_time], [user_object_reserved_page_count], [internal_object_reserved_page_count], [version_store_reserved_page_count], [mixed_extent_page_count], [unallocated_extent_page_count], [total_sessions_using_tempdb], [sessions_with_user_objects], [sessions_with_internal_objects], [version_store_high_warning], [allocation_contention_warning]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1, 1, 1, 1, 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[trace_analysis] WHERE 1 = 1) INSERT INTO [collect].[trace_analysis] ([collection_time], [trace_file_name], [event_class], [event_name]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, N'ci');
IF NOT EXISTS (SELECT 1/0 FROM [collect].[wait_stats] WHERE 1 = 1) INSERT INTO [collect].[wait_stats] ([collection_time], [server_start_time], [wait_type], [waiting_tasks_count], [wait_time_ms], [signal_wait_time_ms]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', 1, 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [collect].[waiting_tasks] WHERE 1 = 1) INSERT INTO [collect].[waiting_tasks] ([collection_time], [session_id], [wait_type], [wait_duration_ms], [blocking_session_id]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 1, N'ci', 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [config].[collection_log] WHERE 1 = 1) INSERT INTO [config].[collection_log] ([collection_time], [collector_name], [collection_status], [rows_collected], [duration_ms]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [config].[collection_schedule] WHERE 1 = 1) INSERT INTO [config].[collection_schedule] ([collector_name], [enabled], [frequency_minutes], [max_duration_minutes], [retention_days], [collect_query], [collect_plan], [created_date], [modified_date]) VALUES (N'ci', 1, 1, 1, 1, 1, 1, DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [config].[collector_database_exclusions] WHERE 1 = 1) INSERT INTO [config].[collector_database_exclusions] ([database_name], [excluded_at]) VALUES (N'ci', DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [config].[critical_issues] WHERE 1 = 1) INSERT INTO [config].[critical_issues] ([log_date], [severity], [problem_area], [source_collector], [message]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'CRITICAL', N'ci', N'ci', N'ci');
IF NOT EXISTS (SELECT 1/0 FROM [config].[database_configuration_history] WHERE 1 = 1) INSERT INTO [config].[database_configuration_history] ([collection_time], [database_id], [database_name], [setting_type], [setting_name]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 1, N'ci', N'ci', N'ci');
IF NOT EXISTS (SELECT 1/0 FROM [config].[ignored_wait_types] WHERE 1 = 1) INSERT INTO [config].[ignored_wait_types] ([wait_type], [is_enabled], [created_date], [modified_date]) VALUES (N'ci', 1, DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()));
IF NOT EXISTS (SELECT 1/0 FROM [config].[installation_history] WHERE 1 = 1) INSERT INTO [config].[installation_history] ([installation_date], [installer_version], [sql_server_version], [sql_server_edition], [installation_type], [installation_status]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', N'ci', N'ci', N'ci');
IF NOT EXISTS (SELECT 1/0 FROM [config].[server_configuration_history] WHERE 1 = 1) INSERT INTO [config].[server_configuration_history] ([collection_time], [configuration_id], [configuration_name], [is_dynamic], [is_advanced]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 1, N'ci', 1, 1);
IF NOT EXISTS (SELECT 1/0 FROM [config].[server_info_history] WHERE 1 = 1) INSERT INTO [config].[server_info_history] ([collection_time], [sqlserver_start_time], [server_name], [sql_version], [edition], [physical_memory_mb], [cpu_count], [environment_type]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), DATEADD(MINUTE, -5, SYSDATETIME()), N'ci', N'ci', N'ci', 1, 1, N'ci');
IF NOT EXISTS (SELECT 1/0 FROM [config].[trace_flags_history] WHERE 1 = 1) INSERT INTO [config].[trace_flags_history] ([collection_time], [trace_flag], [status], [is_global], [is_session]) VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 1, 1, 1, 1);

GO

/* ---------------- hand-authored history PAIRS for the LAG/toggle views ---------------- */

/* Trace flag toggled OFF -> ON across two collections: report.trace_flag_changes' LAG path emits a
   row, executing the bit -> status-text projection that regressed in #1635/#1660. */
IF NOT EXISTS (SELECT 1/0 FROM config.trace_flags_history WHERE trace_flag = 8675)
BEGIN
    INSERT INTO config.trace_flags_history (collection_time, trace_flag, status, is_global, is_session)
    VALUES (DATEADD(MINUTE, -10, SYSDATETIME()), 8675, 0, 1, 0);

    INSERT INTO config.trace_flags_history (collection_time, trace_flag, status, is_global, is_session)
    VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 8675, 1, 1, 0);
END;

/* Server-level sp_configure value changed between collections (sql_variant old/new projection). */
IF NOT EXISTS (SELECT 1/0 FROM config.server_configuration_history WHERE configuration_name = N'ci seed knob')
BEGIN
    INSERT INTO config.server_configuration_history (collection_time, configuration_id, configuration_name, value_configured, value_in_use, is_dynamic, is_advanced)
    VALUES (DATEADD(MINUTE, -10, SYSDATETIME()), 999, N'ci seed knob', CONVERT(sql_variant, 0), CONVERT(sql_variant, 0), 1, 0);

    INSERT INTO config.server_configuration_history (collection_time, configuration_id, configuration_name, value_configured, value_in_use, is_dynamic, is_advanced)
    VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 999, N'ci seed knob', CONVERT(sql_variant, 1), CONVERT(sql_variant, 1), 1, 0);
END;

/* Database-scoped setting changed between collections. */
IF NOT EXISTS (SELECT 1/0 FROM config.database_configuration_history WHERE database_name = N'ci_seed_db')
BEGIN
    INSERT INTO config.database_configuration_history (collection_time, database_id, database_name, setting_type, setting_name, setting_value)
    VALUES (DATEADD(MINUTE, -10, SYSDATETIME()), 999, N'ci_seed_db', 'SCOPED', N'ci seed setting', CONVERT(sql_variant, 0));

    INSERT INTO config.database_configuration_history (collection_time, database_id, database_name, setting_type, setting_name, setting_value)
    VALUES (DATEADD(MINUTE, -5, SYSDATETIME()), 999, N'ci_seed_db', 'SCOPED', N'ci seed setting', CONVERT(sql_variant, 1));
END;
GO
