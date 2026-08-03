/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Ui;

public class ProcedureStatsComparisonItem : ComparisonItemBase
{
    public string SchemaName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string FullName => string.IsNullOrEmpty(SchemaName) ? ObjectName : $"{SchemaName}.{ObjectName}";

    /// <summary>#1981: a REPRESENTATIVE statement captured from inside the procedure (latest
    /// query_stats text sharing the module's normalized sql_handle — the #1568 join), giving this
    /// grid the text column its two comparison siblings carry. procedure_stats stores no text of
    /// its own, so this is a statement, not the definition; empty when none was captured.</summary>
    public string QueryText { get; set; } = "";
}
