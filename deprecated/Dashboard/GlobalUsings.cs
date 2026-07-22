/*
 * Performance Monitor Dashboard
 * Copyright (c) 2026 Darling Data, LLC
 * Licensed under the MIT License - see LICENSE file for details
 */

/* Phase-5 A0: the six alert row types moved to the shared PerformanceMonitor.Alerting library
   (one canonical copy instead of member-identical Lite/Dashboard twins). These aliases keep every
   existing call site compiling unchanged against the shared types. */
global using AnomalousJobInfo = PerformanceMonitor.Alerting.AnomalousJobInfo;
global using FailedJobInfo = PerformanceMonitor.Alerting.FailedJobInfo;
global using LongRunningQueryInfo = PerformanceMonitor.Alerting.LongRunningQueryInfo;
global using PoisonWaitDelta = PerformanceMonitor.Alerting.PoisonWaitDelta;
global using TempDbSpaceInfo = PerformanceMonitor.Alerting.TempDbSpaceInfo;
global using VolumeFreeSpaceInfo = PerformanceMonitor.Alerting.VolumeFreeSpaceInfo;
