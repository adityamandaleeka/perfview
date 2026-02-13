using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze;

/// <summary>
/// Correlates raw Binder ETW events (AssemblyLoadStart/Stop, ResolutionAttempted,
/// KnownPathProbed, handler invocations) into per-assembly load records with
/// pre-computed diagnostics. Designed for agent consumption.
/// </summary>
public static class LoaderAnalysis
{
    private static readonly Dictionary<ResolutionAttemptedStage, string> StageDescriptions = new()
    {
        [ResolutionAttemptedStage.FindInLoadContext] = "Check if already loaded in target context",
        [ResolutionAttemptedStage.AssemblyLoadContextLoad] = "Custom AssemblyLoadContext.Load() override",
        [ResolutionAttemptedStage.ApplicationAssemblies] = "Probe application directory and deps.json paths",
        [ResolutionAttemptedStage.DefaultAssemblyLoadContextFallback] = "Fallback to default AssemblyLoadContext",
        [ResolutionAttemptedStage.ResolveSatelliteAssembly] = "Resolve satellite (resource) assembly",
        [ResolutionAttemptedStage.AssemblyLoadContextResolvingEvent] = "AssemblyLoadContext.Resolving event handlers",
        [ResolutionAttemptedStage.AppDomainAssemblyResolveEvent] = "AppDomain.AssemblyResolve event handlers (legacy fallback)",
    };

    public static LoaderAnalysisResult Analyze(Etlx.TraceLog traceLog, double? fromMs = null, double? toMs = null)
    {
        // Per-process, per-thread tracking of in-flight loads
        // Key: (ProcessID, ThreadID, AssemblyName)
        var inFlightLoads = new Dictionary<(int Pid, int Tid, string AssemblyName), AssemblyLoadRecord>();
        // Track load ordering per (PID, TID) for KnownPathProbed correlation
        var inFlightLoadStack = new Dictionary<(int Pid, int Tid), Stack<AssemblyLoadRecord>>();
        var completedLoads = new Dictionary<int, List<AssemblyLoadRecord>>(); // by ProcessID

        using var source = traceLog.Events.GetSource();
        var clr = new ClrTraceEventParser(source);

        clr.AssemblyLoaderStart += (data) =>
        {
            if (fromMs.HasValue && data.TimeStampRelativeMSec < fromMs.Value) return;
            if (toMs.HasValue && data.TimeStampRelativeMSec > toMs.Value) return;

            var key = (data.ProcessID, data.ThreadID, data.AssemblyName);
            var record = new AssemblyLoadRecord
            {
                AssemblyName = data.AssemblyName,
                AssemblyPath = data.AssemblyPath,
                RequestingAssembly = data.RequestingAssembly,
                AssemblyLoadContext = data.AssemblyLoadContext,
                RequestingAssemblyLoadContext = data.RequestingAssemblyLoadContext,
                StartTimeMs = Math.Round(data.TimeStampRelativeMSec, 3),
                ProcessId = data.ProcessID,
                ThreadId = data.ThreadID,
            };
            inFlightLoads[key] = record;

                var threadKey = (data.ProcessID, data.ThreadID);
                if (!inFlightLoadStack.TryGetValue(threadKey, out var stack))
                {
                    stack = new Stack<AssemblyLoadRecord>();
                    inFlightLoadStack[threadKey] = stack;
                }
                stack.Push(record);
        };

        clr.AssemblyLoaderResolutionAttempted += (data) =>
        {
            if (fromMs.HasValue && data.TimeStampRelativeMSec < fromMs.Value) return;
            if (toMs.HasValue && data.TimeStampRelativeMSec > toMs.Value) return;

            var key = (data.ProcessID, data.ThreadID, data.AssemblyName);
            if (!inFlightLoads.TryGetValue(key, out var record)) return;

            record.ResolutionAttempts.Add(new ResolutionAttemptRecord
            {
                StageCode = (int)data.Stage,
                Stage = data.Stage.ToString(),
                StageDescription = StageDescriptions.GetValueOrDefault(data.Stage, data.Stage.ToString()),
                ResultCode = (int)data.Result,
                Result = data.Result.ToString(),
                ResultAssemblyName = data.ResultAssemblyName,
                ResultAssemblyPath = data.ResultAssemblyPath,
                ErrorMessage = string.IsNullOrEmpty(data.ErrorMessage) ? null : data.ErrorMessage,
            });
        };

        clr.AssemblyLoaderKnownPathProbed += (data) =>
        {
            if (fromMs.HasValue && data.TimeStampRelativeMSec < fromMs.Value) return;
            if (toMs.HasValue && data.TimeStampRelativeMSec > toMs.Value) return;

            // KnownPathProbed doesn't carry AssemblyName — attach to the most recent
            // in-flight load on this (PID, TID) using temporal stack ordering.
            var threadKey = (data.ProcessID, data.ThreadID);
            if (!inFlightLoadStack.TryGetValue(threadKey, out var stack) || stack.Count == 0) return;
            var record = stack.Peek();

            record.PathsProbed.Add(new PathProbeRecord
            {
                Path = data.FilePath,
                SourceCode = (int)data.PathSource,
                Source = data.PathSource.ToString(),
                Found = data.Result == 0, // HRESULT 0 = S_OK
            });
        };

        clr.AssemblyLoaderAssemblyLoadContextResolvingHandlerInvoked += (data) =>
        {
            if (fromMs.HasValue && data.TimeStampRelativeMSec < fromMs.Value) return;
            if (toMs.HasValue && data.TimeStampRelativeMSec > toMs.Value) return;

            var key = (data.ProcessID, data.ThreadID, data.AssemblyName);
            if (!inFlightLoads.TryGetValue(key, out var record)) return;

            record.HandlersInvoked.Add(new HandlerRecord
            {
                Type = "AssemblyLoadContextResolving",
                HandlerName = data.HandlerName,
                ResultAssemblyName = string.IsNullOrEmpty(data.ResultAssemblyName) ? null : data.ResultAssemblyName,
                ResultAssemblyPath = string.IsNullOrEmpty(data.ResultAssemblyPath) ? null : data.ResultAssemblyPath,
            });
        };

        clr.AssemblyLoaderAppDomainAssemblyResolveHandlerInvoked += (data) =>
        {
            if (fromMs.HasValue && data.TimeStampRelativeMSec < fromMs.Value) return;
            if (toMs.HasValue && data.TimeStampRelativeMSec > toMs.Value) return;

            var key = (data.ProcessID, data.ThreadID, data.AssemblyName);
            if (!inFlightLoads.TryGetValue(key, out var record)) return;

            record.HandlersInvoked.Add(new HandlerRecord
            {
                Type = "AppDomainAssemblyResolve",
                HandlerName = data.HandlerName,
                ResultAssemblyName = string.IsNullOrEmpty(data.ResultAssemblyName) ? null : data.ResultAssemblyName,
                ResultAssemblyPath = string.IsNullOrEmpty(data.ResultAssemblyPath) ? null : data.ResultAssemblyPath,
            });
        };

        clr.AssemblyLoaderAssemblyLoadFromResolveHandlerInvoked += (data) =>
        {
            if (fromMs.HasValue && data.TimeStampRelativeMSec < fromMs.Value) return;
            if (toMs.HasValue && data.TimeStampRelativeMSec > toMs.Value) return;

            var key = (data.ProcessID, data.ThreadID, data.AssemblyName);
            if (!inFlightLoads.TryGetValue(key, out var record)) return;

            record.HandlersInvoked.Add(new HandlerRecord
            {
                Type = "AssemblyLoadFromResolve",
                HandlerName = $"LoadFrom(IsTracked={data.IsTrackedLoad})",
                ResultAssemblyName = null,
                ResultAssemblyPath = string.IsNullOrEmpty(data.ComputedRequestedAssemblyPath) ? null : data.ComputedRequestedAssemblyPath,
            });
        };

        clr.AssemblyLoaderStop += (data) =>
        {
            if (fromMs.HasValue && data.TimeStampRelativeMSec < fromMs.Value) return;
            if (toMs.HasValue && data.TimeStampRelativeMSec > toMs.Value) return;

            var key = (data.ProcessID, data.ThreadID, data.AssemblyName);
            if (inFlightLoads.TryGetValue(key, out var record))
            {
                record.Success = data.Success;
                record.Cached = data.Cached;
                record.ResultAssemblyName = data.ResultAssemblyName;
                record.ResultAssemblyPath = data.ResultAssemblyPath;
                record.DurationMs = Math.Round(data.TimeStampRelativeMSec - record.StartTimeMs, 3);

                if (!completedLoads.TryGetValue(data.ProcessID, out var list))
                {
                    list = new List<AssemblyLoadRecord>();
                    completedLoads[data.ProcessID] = list;
                }
                list.Add(record);
                inFlightLoads.Remove(key);

                // Pop from thread stack
                var threadKey = (data.ProcessID, data.ThreadID);
                if (inFlightLoadStack.TryGetValue(threadKey, out var stack) && stack.Count > 0)
                {
                    // Remove this record from the stack (may not be on top if events are misordered)
                    if (stack.Peek() == record)
                        stack.Pop();
                }
            }
        };

        source.Process();

        // Also add any orphaned in-flight loads (Start without Stop)
        foreach (var kvp in inFlightLoads)
        {
            var record = kvp.Value;
            record.Success = false;
            record.Orphaned = true;
            if (!completedLoads.TryGetValue(record.ProcessId, out var list))
            {
                list = new List<AssemblyLoadRecord>();
                completedLoads[record.ProcessId] = list;
            }
            list.Add(record);
        }

        // Build per-process results with diagnostics
        var processResults = new List<ProcessLoaderResult>();
        foreach (var process in traceLog.Processes)
        {
            if (!completedLoads.TryGetValue(process.ProcessID, out var loads) || loads.Count == 0)
                continue;

            processResults.Add(BuildProcessResult(process.ProcessID, process.Name, loads));
        }

        return new LoaderAnalysisResult { Processes = processResults };
    }

    private static ProcessLoaderResult BuildProcessResult(int pid, string processName, List<AssemblyLoadRecord> loads)
    {
        var succeeded = loads.Count(l => l.Success);
        var failed = loads.Count(l => !l.Success);
        var cached = loads.Count(l => l.Cached);
        var durations = loads.Where(l => l.DurationMs.HasValue).Select(l => l.DurationMs!.Value).OrderBy(d => d).ToList();
        var totalProbes = loads.Sum(l => l.PathsProbed.Count);
        var probeMisses = loads.Sum(l => l.PathsProbed.Count(p => !p.Found));

        var summary = new LoaderSummary
        {
            TotalLoads = loads.Count,
            Succeeded = succeeded,
            Failed = failed,
            Cached = cached,
            TotalDurationMs = durations.Count > 0 ? Math.Round(durations.Sum(), 3) : 0,
            AvgDurationMs = durations.Count > 0 ? Math.Round(durations.Average(), 3) : 0,
            P95DurationMs = durations.Count > 0 ? Math.Round(Percentile(durations, 0.95), 3) : 0,
            MaxDurationMs = durations.Count > 0 ? Math.Round(durations.Max(), 3) : 0,
            DistinctAssemblyLoadContexts = loads.Select(l => l.AssemblyLoadContext).Where(c => !string.IsNullOrEmpty(c)).Distinct().Count(),
            TotalPathsProbed = totalProbes,
            ProbeMissRate = totalProbes > 0 ? Math.Round((double)probeMisses / totalProbes, 2) : 0,
        };

        var diagnostics = GenerateDiagnostics(loads, summary);

        return new ProcessLoaderResult
        {
            ProcessId = pid,
            ProcessName = processName,
            Summary = summary,
            Diagnostics = diagnostics,
            Loads = loads,
        };
    }

    private static List<Diagnostic> GenerateDiagnostics(List<AssemblyLoadRecord> loads, LoaderSummary summary)
    {
        var diagnostics = new List<Diagnostic>();

        // LOADER001: load failures
        var failedLoads = loads.Where(l => !l.Success).ToList();
        if (failedLoads.Count > 0)
        {
            diagnostics.Add(new Diagnostic
            {
                DiagnosticId = "LOADER001",
                Severity = "error",
                Category = "load-failure",
                Message = $"{failedLoads.Count} assembly load(s) failed",
                AssemblyNames = failedLoads.Select(l => l.AssemblyName).Distinct().ToList(),
                Detail = "Use --failures for full resolution trace",
            });
        }

        // LOADER002: excessive probing
        if (summary.TotalPathsProbed > 0 && summary.ProbeMissRate > 0.5)
        {
            var misses = loads.Sum(l => l.PathsProbed.Count(p => !p.Found));
            diagnostics.Add(new Diagnostic
            {
                DiagnosticId = "LOADER002",
                Severity = "warning",
                Category = "excessive-probing",
                Message = $"{Math.Round(summary.ProbeMissRate * 100)}% probe miss rate ({summary.TotalPathsProbed} probes, {misses} misses)",
                Detail = "Use --probing for path analysis",
            });
        }

        // LOADER003: slow loads (> 50ms default)
        const double slowThresholdMs = 50.0;
        var slowLoads = loads.Where(l => l.DurationMs.HasValue && l.DurationMs.Value > slowThresholdMs).ToList();
        if (slowLoads.Count > 0)
        {
            var slowest = slowLoads.OrderByDescending(l => l.DurationMs).First();
            diagnostics.Add(new Diagnostic
            {
                DiagnosticId = "LOADER003",
                Severity = "info",
                Category = "slow-load",
                Message = $"{slowLoads.Count} load(s) exceeded {slowThresholdMs}ms (slowest: {slowest.AssemblyName} at {slowest.DurationMs}ms)",
                AssemblyNames = slowLoads.Select(l => l.AssemblyName).Distinct().ToList(),
                Detail = "Use --slow for details",
            });
        }

        // LOADER004: handler fallback — loads resolved via AppDomain.AssemblyResolve
        var fallbackLoads = loads.Where(l => l.HandlersInvoked.Any(h => h.Type == "AppDomainAssemblyResolve")).ToList();
        if (fallbackLoads.Count > 0)
        {
            diagnostics.Add(new Diagnostic
            {
                DiagnosticId = "LOADER004",
                Severity = "info",
                Category = "handler-fallback",
                Message = $"{fallbackLoads.Count} load(s) resolved via legacy AppDomain.AssemblyResolve handler",
                AssemblyNames = fallbackLoads.Select(l => l.AssemblyName).Distinct().ToList(),
            });
        }

        // LOADER005: same assembly in multiple ALCs
        var assemblyContexts = loads
            .Where(l => l.Success && !string.IsNullOrEmpty(l.AssemblyLoadContext))
            .GroupBy(l => l.AssemblyName)
            .Where(g => g.Select(l => l.AssemblyLoadContext).Distinct().Count() > 1)
            .ToList();
        if (assemblyContexts.Count > 0)
        {
            diagnostics.Add(new Diagnostic
            {
                DiagnosticId = "LOADER005",
                Severity = "info",
                Category = "multiple-contexts",
                Message = $"{assemblyContexts.Count} assembly(ies) loaded into multiple AssemblyLoadContexts",
                AssemblyNames = assemblyContexts.Select(g => g.Key).ToList(),
            });
        }

        // LOADER006: version mismatches
        var versionIssues = loads
            .SelectMany(l => l.ResolutionAttempts)
            .Where(r => r.ResultCode == (int)ResolutionAttemptedResult.IncompatibleVersion
                     || r.ResultCode == (int)ResolutionAttemptedResult.MismatchedAssemblyName)
            .ToList();
        if (versionIssues.Count > 0)
        {
            diagnostics.Add(new Diagnostic
            {
                DiagnosticId = "LOADER006",
                Severity = "warning",
                Category = "version-mismatch",
                Message = $"{versionIssues.Count} resolution attempt(s) had version or name mismatches",
            });
        }

        return diagnostics;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double index = p * (sorted.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = lower + 1;
        if (upper >= sorted.Count) return sorted[^1];
        double weight = index - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }
}

// --- Data model classes for JSON serialization ---

public class LoaderAnalysisResult
{
    public List<ProcessLoaderResult> Processes { get; set; } = new();
}

public class ProcessLoaderResult
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public LoaderSummary Summary { get; set; } = new();
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public List<AssemblyLoadRecord> Loads { get; set; } = new();
}

public class LoaderSummary
{
    public int TotalLoads { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Cached { get; set; }
    public double TotalDurationMs { get; set; }
    public double AvgDurationMs { get; set; }
    public double P95DurationMs { get; set; }
    public double MaxDurationMs { get; set; }
    public int DistinctAssemblyLoadContexts { get; set; }
    public int TotalPathsProbed { get; set; }
    public double ProbeMissRate { get; set; }
}

public class Diagnostic
{
    public string DiagnosticId { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string>? AssemblyNames { get; set; }
    public string? Detail { get; set; }
}

public class AssemblyLoadRecord
{
    public string AssemblyName { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string RequestingAssembly { get; set; } = "";
    public string AssemblyLoadContext { get; set; } = "";
    public string RequestingAssemblyLoadContext { get; set; } = "";
    public double StartTimeMs { get; set; }
    public double? DurationMs { get; set; }
    public bool Success { get; set; }
    public bool Cached { get; set; }
    public bool Orphaned { get; set; }
    public string? ResultAssemblyName { get; set; }
    public string? ResultAssemblyPath { get; set; }
    public List<ResolutionAttemptRecord> ResolutionAttempts { get; set; } = new();
    public List<PathProbeRecord> PathsProbed { get; set; } = new();
    public List<HandlerRecord> HandlersInvoked { get; set; } = new();

    // Internal tracking, not serialized by default
    internal int ProcessId { get; set; }
    internal int ThreadId { get; set; }
}

public class ResolutionAttemptRecord
{
    public int StageCode { get; set; }
    public string Stage { get; set; } = "";
    public string StageDescription { get; set; } = "";
    public int ResultCode { get; set; }
    public string Result { get; set; } = "";
    public string? ResultAssemblyName { get; set; }
    public string? ResultAssemblyPath { get; set; }
    public string? ErrorMessage { get; set; }
}

public class PathProbeRecord
{
    public string Path { get; set; } = "";
    public int SourceCode { get; set; }
    public string Source { get; set; } = "";
    public bool Found { get; set; }
}

public class HandlerRecord
{
    public string Type { get; set; } = "";
    public string HandlerName { get; set; } = "";
    public string? ResultAssemblyName { get; set; }
    public string? ResultAssemblyPath { get; set; }
}
