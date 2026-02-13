using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze.Commands;

public static class LoaderCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Create()
    {
        var traceFileArg = new Argument<FileInfo>("trace-file", "Path to the .nettrace file to analyze");
        var formatOption = new Option<OutputFormat>("--format", () => OutputFormat.Json, "Output format (json is default for agent consumption)");
        var processOption = new Option<string?>("--process", "Filter by process name (substring match)");
        var failuresOption = new Option<bool>("--failures", "Show only failed assembly loads with full resolution trace");
        var slowOption = new Option<bool>("--slow", "Show only slow assembly loads");
        var thresholdOption = new Option<double>("--threshold", () => 50.0, "Duration threshold in ms for --slow (default: 50ms)");
        var assemblyOption = new Option<string?>("--assembly", "Show full correlated timeline for a specific assembly (substring match)");
        var contextOption = new Option<string?>("--context", "Filter by AssemblyLoadContext name (substring match)");
        var probingOption = new Option<bool>("--probing", "Show path probing analysis");
        var rawOption = new Option<bool>("--raw", "Include raw event data in output (verbose)");
        var fromOption = new Option<double?>("--from", "Start time in milliseconds");
        var toOption = new Option<double?>("--to", "End time in milliseconds");

        var command = new Command("loader", "Analyze assembly loader/binder events with pre-correlated diagnostics")
        {
            traceFileArg,
            formatOption,
            processOption,
            failuresOption,
            slowOption,
            thresholdOption,
            assemblyOption,
            contextOption,
            probingOption,
            rawOption,
            fromOption,
            toOption,
        };

        command.SetHandler(async (context) =>
        {
            var traceFile = context.ParseResult.GetValueForArgument(traceFileArg);
            var format = context.ParseResult.GetValueForOption(formatOption);
            var processFilter = context.ParseResult.GetValueForOption(processOption);
            var failures = context.ParseResult.GetValueForOption(failuresOption);
            var slow = context.ParseResult.GetValueForOption(slowOption);
            var threshold = context.ParseResult.GetValueForOption(thresholdOption);
            var assembly = context.ParseResult.GetValueForOption(assemblyOption);
            var alcContext = context.ParseResult.GetValueForOption(contextOption);
            var probing = context.ParseResult.GetValueForOption(probingOption);
            var raw = context.ParseResult.GetValueForOption(rawOption);
            var fromMs = context.ParseResult.GetValueForOption(fromOption);
            var toMs = context.ParseResult.GetValueForOption(toOption);
            Execute(traceFile, format, processFilter, failures, slow, threshold, assembly, alcContext, probing, raw, fromMs, toMs);
        });
        return command;
    }

    private static void Execute(FileInfo traceFile, OutputFormat format, string? processFilter,
        bool failures, bool slow, double threshold, string? assemblyFilter,
        string? contextFilter, bool probing, bool raw, double? fromMs, double? toMs)
    {
        if (!traceFile.Exists)
        {
            Console.Error.WriteLine($"Error: File not found: {traceFile.FullName}");
            return;
        }

        try
        {
            string etlxPath = Etlx.TraceLog.CreateFromEventPipeDataFile(traceFile.FullName);

            using var traceLog = new Etlx.TraceLog(etlxPath);
            var result = LoaderAnalysis.Analyze(traceLog, fromMs, toMs);

            // Apply process filter
            var processes = result.Processes;
            if (processFilter != null)
            {
                processes = processes
                    .Where(p => p.ProcessName.Contains(processFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (processes.Count == 0)
            {
                Console.Error.WriteLine("No processes with assembly loader events found in trace.");
                Console.Error.WriteLine("Hint: Collect with binder events enabled: dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:4");
                try { File.Delete(etlxPath); } catch { }
                return;
            }

            // Route to the appropriate view
            if (failures)
                OutputFailures(processes, format, raw);
            else if (slow)
                OutputSlow(processes, format, threshold, raw);
            else if (assemblyFilter != null)
                OutputAssembly(processes, format, assemblyFilter, raw);
            else if (contextFilter != null)
                OutputContext(processes, format, contextFilter);
            else if (probing)
                OutputProbing(processes, format);
            else
                OutputSummary(processes, format);

            try { File.Delete(etlxPath); } catch { }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error analyzing trace: {ex.Message}");
        }
    }

    // --- Default: summary + diagnostics ---
    private static void OutputSummary(List<ProcessLoaderResult> processes, OutputFormat format)
    {
        if (format == OutputFormat.Json)
        {
            // Emit summary + diagnostics only (no per-load details)
            var output = processes.Select(p => new
            {
                p.ProcessId,
                p.ProcessName,
                p.Summary,
                p.Diagnostics,
            });
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
        }
        else
        {
            foreach (var proc in processes)
            {
                Console.WriteLine($"=== Loader Stats for {proc.ProcessName} (PID {proc.ProcessId}) ===");
                Console.WriteLine();
                var s = proc.Summary;
                Console.WriteLine($"  Total Loads:         {s.TotalLoads}");
                Console.WriteLine($"    Succeeded:         {s.Succeeded}");
                Console.WriteLine($"    Failed:            {s.Failed}");
                Console.WriteLine($"    Cached:            {s.Cached}");
                Console.WriteLine();
                Console.WriteLine($"  Duration (ms):       avg={s.AvgDurationMs:F1}  p95={s.P95DurationMs:F1}  max={s.MaxDurationMs:F1}");
                Console.WriteLine($"  Load Contexts:       {s.DistinctAssemblyLoadContexts} distinct");
                Console.WriteLine($"  Path Probing:        {s.TotalPathsProbed} probes ({Math.Round(s.ProbeMissRate * 100)}% miss rate)");
                Console.WriteLine();

                if (proc.Diagnostics.Count > 0)
                {
                    Console.WriteLine("  Diagnostics:");
                    foreach (var d in proc.Diagnostics)
                    {
                        var icon = d.Severity switch { "error" => "✗", "warning" => "⚠", _ => "ℹ" };
                        Console.WriteLine($"    {icon} [{d.DiagnosticId}] {d.Message}");
                        if (d.Detail != null)
                            Console.WriteLine($"      {d.Detail}");
                    }
                }
                else
                {
                    Console.WriteLine("  No issues detected.");
                }
                Console.WriteLine();
            }
        }
    }

    // --- --failures: failed loads with full resolution trace ---
    private static void OutputFailures(List<ProcessLoaderResult> processes, OutputFormat format, bool raw)
    {
        var failedByProcess = processes
            .Select(p => new
            {
                p.ProcessId,
                p.ProcessName,
                FailedLoads = FilterLoads(p.Loads.Where(l => !l.Success).ToList(), raw),
            })
            .Where(p => p.FailedLoads.Count > 0)
            .ToList();

        if (format == OutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(failedByProcess, JsonOptions));
        }
        else
        {
            if (failedByProcess.Count == 0)
            {
                Console.WriteLine("No failed assembly loads found.");
                return;
            }
            foreach (var proc in failedByProcess)
            {
                Console.WriteLine($"=== Failed Loads for {proc.ProcessName} (PID {proc.ProcessId}) ===");
                foreach (var load in proc.FailedLoads)
                {
                    PrintLoadRecord(load);
                }
            }
        }
    }

    // --- --slow: slow loads ---
    private static void OutputSlow(List<ProcessLoaderResult> processes, OutputFormat format, double threshold, bool raw)
    {
        var slowByProcess = processes
            .Select(p => new
            {
                p.ProcessId,
                p.ProcessName,
                SlowLoads = FilterLoads(
                    p.Loads.Where(l => l.DurationMs.HasValue && l.DurationMs.Value > threshold)
                        .OrderByDescending(l => l.DurationMs).ToList(), raw),
                ThresholdMs = threshold,
            })
            .Where(p => p.SlowLoads.Count > 0)
            .ToList();

        if (format == OutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(slowByProcess, JsonOptions));
        }
        else
        {
            if (slowByProcess.Count == 0)
            {
                Console.WriteLine($"No assembly loads exceeding {threshold}ms found.");
                return;
            }
            foreach (var proc in slowByProcess)
            {
                Console.WriteLine($"=== Slow Loads for {proc.ProcessName} (PID {proc.ProcessId}) [>{threshold}ms] ===");
                foreach (var load in proc.SlowLoads)
                {
                    PrintLoadRecord(load);
                }
            }
        }
    }

    // --- --assembly <name>: full correlated timeline for one assembly ---
    private static void OutputAssembly(List<ProcessLoaderResult> processes, OutputFormat format, string assemblyFilter, bool raw)
    {
        var matchingLoads = processes
            .SelectMany(p => p.Loads.Select(l => new { p.ProcessId, p.ProcessName, Load = l }))
            .Where(x => x.Load.AssemblyName.Contains(assemblyFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (format == OutputFormat.Json)
        {
            var output = new
            {
                AssemblyFilter = assemblyFilter,
                Loads = FilterLoads(matchingLoads.Select(x => x.Load).ToList(), raw),
            };
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
        }
        else
        {
            if (matchingLoads.Count == 0)
            {
                Console.WriteLine($"No assembly loads matching \"{assemblyFilter}\" found.");
                return;
            }
            Console.WriteLine($"=== Assembly Loads matching \"{assemblyFilter}\" ===");
            foreach (var match in matchingLoads)
            {
                Console.WriteLine($"  Process: {match.ProcessName} (PID {match.ProcessId})");
                PrintLoadRecord(match.Load);
            }
        }
    }

    // --- --context <alc>: per-ALC breakdown ---
    private static void OutputContext(List<ProcessLoaderResult> processes, OutputFormat format, string contextFilter)
    {
        var matchingByContext = processes
            .Select(p => new
            {
                p.ProcessId,
                p.ProcessName,
                Loads = p.Loads
                    .Where(l => l.AssemblyLoadContext.Contains(contextFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
            })
            .Where(p => p.Loads.Count > 0)
            .Select(p => new
            {
                p.ProcessId,
                p.ProcessName,
                ContextFilter = contextFilter,
                TotalLoads = p.Loads.Count,
                Succeeded = p.Loads.Count(l => l.Success),
                Failed = p.Loads.Count(l => !l.Success),
                Assemblies = p.Loads.Select(l => new { l.AssemblyName, l.Success, l.DurationMs }).ToList(),
            })
            .ToList();

        if (format == OutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(matchingByContext, JsonOptions));
        }
        else
        {
            if (matchingByContext.Count == 0)
            {
                Console.WriteLine($"No loads in AssemblyLoadContext matching \"{contextFilter}\" found.");
                return;
            }
            foreach (var proc in matchingByContext)
            {
                Console.WriteLine($"=== Context \"{contextFilter}\" in {proc.ProcessName} (PID {proc.ProcessId}) ===");
                Console.WriteLine($"  Total: {proc.TotalLoads}  Succeeded: {proc.Succeeded}  Failed: {proc.Failed}");
                foreach (var asm in proc.Assemblies)
                {
                    var status = asm.Success ? "OK" : "FAIL";
                    var dur = asm.DurationMs.HasValue ? $"{asm.DurationMs:F1}ms" : "n/a";
                    Console.WriteLine($"    [{status}] {asm.AssemblyName} ({dur})");
                }
                Console.WriteLine();
            }
        }
    }

    // --- --probing: path probing analysis ---
    private static void OutputProbing(List<ProcessLoaderResult> processes, OutputFormat format)
    {
        var probingByProcess = processes
            .Select(p =>
            {
                var allProbes = p.Loads.SelectMany(l => l.PathsProbed).ToList();
                var bySource = allProbes
                    .GroupBy(pr => pr.Source)
                    .Select(g => new
                    {
                        SourceCode = g.First().SourceCode,
                        Source = g.Key,
                        Total = g.Count(),
                        Hits = g.Count(pr => pr.Found),
                        Misses = g.Count(pr => !pr.Found),
                    })
                    .OrderByDescending(s => s.Misses)
                    .ToList();

                return new
                {
                    p.ProcessId,
                    p.ProcessName,
                    TotalProbes = allProbes.Count,
                    TotalHits = allProbes.Count(pr => pr.Found),
                    TotalMisses = allProbes.Count(pr => !pr.Found),
                    MissRate = allProbes.Count > 0 ? Math.Round((double)allProbes.Count(pr => !pr.Found) / allProbes.Count, 2) : 0.0,
                    BySource = bySource,
                    MissedPaths = allProbes.Where(pr => !pr.Found).Select(pr => pr.Path).Distinct().Take(20).ToList(),
                };
            })
            .Where(p => p.TotalProbes > 0)
            .ToList();

        if (format == OutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(probingByProcess, JsonOptions));
        }
        else
        {
            if (probingByProcess.Count == 0)
            {
                Console.WriteLine("No path probing data found in trace.");
                return;
            }
            foreach (var proc in probingByProcess)
            {
                Console.WriteLine($"=== Path Probing for {proc.ProcessName} (PID {proc.ProcessId}) ===");
                Console.WriteLine($"  Total: {proc.TotalProbes}  Hits: {proc.TotalHits}  Misses: {proc.TotalMisses}  Miss Rate: {Math.Round(proc.MissRate * 100)}%");
                Console.WriteLine();
                Console.WriteLine($"  {"Source",-30} {"Total",6} {"Hits",6} {"Misses",6}");
                Console.WriteLine($"  {new string('-', 54)}");
                foreach (var src in proc.BySource)
                {
                    Console.WriteLine($"  {src.Source,-30} {src.Total,6} {src.Hits,6} {src.Misses,6}");
                }
                if (proc.MissedPaths.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("  Sample missed paths:");
                    foreach (var path in proc.MissedPaths)
                    {
                        Console.WriteLine($"    {path}");
                    }
                }
                Console.WriteLine();
            }
        }
    }

    // --- Helpers ---

    private static List<AssemblyLoadRecord> FilterLoads(List<AssemblyLoadRecord> loads, bool _)
    {
        // Always return loads as-is. Empty collections serialize as [] for schema consistency.
        return loads;
    }

    private static void PrintLoadRecord(AssemblyLoadRecord load)
    {
        var status = load.Success ? "OK" : "FAIL";
        var dur = load.DurationMs.HasValue ? $"{load.DurationMs:F1}ms" : "incomplete";
        Console.WriteLine($"  [{status}] {load.AssemblyName} ({dur})");
        Console.WriteLine($"    Requesting: {load.RequestingAssembly}  Context: {load.AssemblyLoadContext}");
        if (load.ResultAssemblyPath != null)
            Console.WriteLine($"    Loaded from: {load.ResultAssemblyPath}");

        if (load.ResolutionAttempts?.Count > 0)
        {
            Console.WriteLine($"    Resolution attempts:");
            foreach (var attempt in load.ResolutionAttempts)
            {
                var marker = attempt.ResultCode == 0 ? "✓" : "✗";
                Console.WriteLine($"      {marker} Stage {attempt.StageCode}: {attempt.StageDescription} → {attempt.Result}");
                if (attempt.ErrorMessage != null)
                    Console.WriteLine($"        Error: {attempt.ErrorMessage}");
            }
        }

        if (load.PathsProbed?.Count > 0)
        {
            Console.WriteLine($"    Paths probed ({load.PathsProbed.Count}):");
            foreach (var probe in load.PathsProbed.Take(10))
            {
                var found = probe.Found ? "✓" : "✗";
                Console.WriteLine($"      {found} [{probe.Source}] {probe.Path}");
            }
            if (load.PathsProbed.Count > 10)
                Console.WriteLine($"      ... and {load.PathsProbed.Count - 10} more");
        }

        if (load.HandlersInvoked?.Count > 0)
        {
            Console.WriteLine($"    Handlers invoked:");
            foreach (var handler in load.HandlersInvoked)
            {
                var result = handler.ResultAssemblyPath ?? handler.ResultAssemblyName ?? "(no result)";
                Console.WriteLine($"      [{handler.Type}] {handler.HandlerName} → {result}");
            }
        }
        Console.WriteLine();
    }
}
