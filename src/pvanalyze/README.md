# pvanalyze

A cross-platform command-line tool for analyzing .NET performance traces (`.nettrace` files).

## Overview

`pvanalyze` is a companion tool to PerfView that runs on **Mac, Linux, and Windows**. It provides command-line access to trace analysis capabilities, making it ideal for:

- Automation and scripting
- CI/CD pipelines
- AI/LLM agent integration
- Developers on non-Windows platforms

## Installation

```bash
# Build from source
cd src/pvanalyze
dotnet build -c Release

# Or publish as a self-contained executable
dotnet publish -c Release -r osx-arm64 --self-contained
```

## Usage

### Collect a Trace

Use `dotnet-trace` to collect traces on any platform:

```bash
# Install dotnet-trace (one-time)
dotnet tool install --global dotnet-trace

# Collect a trace from a running process
dotnet-trace collect --process-id <PID> --output trace.nettrace

# Or collect while running an app
dotnet-trace collect -- dotnet run

# Include assembly loader/binder events for loader diagnostics
dotnet-trace collect --providers "Microsoft-Windows-DotNETRuntime:0xC" -- dotnet run
```

### Analyze with pvanalyze

```bash
# Show trace information
pvanalyze info trace.nettrace

# GC statistics (summary)
pvanalyze gcstats trace.nettrace
pvanalyze gcstats trace.nettrace --format json

# GC timeline (per-GC breakdown)
pvanalyze gcstats trace.nettrace --timeline
pvanalyze gcstats trace.nettrace --longest 5   # Top 5 longest pauses

# GC with time filtering
pvanalyze gcstats trace.nettrace --from 1000 --to 2000 --timeline

# JIT compilation statistics
pvanalyze jitstats trace.nettrace
pvanalyze jitstats trace.nettrace --format json

# CPU stacks analysis
pvanalyze cpustacks trace.nettrace --top 20
pvanalyze cpustacks trace.nettrace --format json

# Export to SpeedScope for flame graph visualization
pvanalyze cpustacks trace.nettrace --format speedscope
# Then open at https://www.speedscope.app/

# List all event types in the trace
pvanalyze events trace.nettrace --list

# Filter events by type or provider
pvanalyze events trace.nettrace --type GCStart
pvanalyze events trace.nettrace --provider DotNETRuntime --limit 50

# Filter by PID, TID, or payload content
pvanalyze events trace.nettrace --pid 1234
pvanalyze events trace.nettrace --payload "ConnectionReset"

# Time-filtered events
pvanalyze events trace.nettrace --from 1000 --to 2000

# Exception analysis
pvanalyze exceptions trace.nettrace
pvanalyze exceptions trace.nettrace --type NullReference

# Assembly loader/binder diagnostics
pvanalyze loader trace.nettrace                        # Summary + diagnostics (JSON default)
pvanalyze loader trace.nettrace --failures             # Failed loads with full resolution trace
pvanalyze loader trace.nettrace --slow --threshold 100 # Loads exceeding 100ms
pvanalyze loader trace.nettrace --assembly "MyLib"     # Correlated timeline for one assembly
pvanalyze loader trace.nettrace --probing              # Path probing hit/miss analysis
pvanalyze loader trace.nettrace --format text           # Human-readable output

# CPU call tree analysis
pvanalyze calltree trace.nettrace --depth 5
pvanalyze calltree trace.nettrace --hot-path
pvanalyze calltree trace.nettrace --caller-callee "WriteAsJsonAsync"
pvanalyze calltree trace.nettrace --hot-path --format json

# Point-in-time snapshot
pvanalyze snapshot trace.nettrace --at 1500
pvanalyze snapshot trace.nettrace --at 1500 --window 200
pvanalyze snapshot trace.nettrace --at 1500 --format text

# Unified timeline with bucketed event lanes
pvanalyze timeline trace.nettrace
pvanalyze timeline trace.nettrace --lanes gc,cpu --buckets 100
pvanalyze timeline trace.nettrace --from 1000 --to 3000

# Start an API server for tooling integration
pvanalyze serve
pvanalyze serve --port 8080
```

## Commands

### `info <trace-file>`

Display basic trace metadata:
- Duration, event count, processes

### `gcstats <trace-file>`

Analyze garbage collection performance:
- Summary stats: total GCs, allocations, pause times
- Timeline mode (`--timeline`): per-GC breakdown
- Longest pauses (`--longest N`)
- Time filtering (`--from`, `--to`)

Options:
- `--format text|json` - Output format
- `--process <name>` - Filter by process
- `--timeline` - Show per-GC events
- `--longest <N>` - Show N longest pauses
- `--from <ms>` / `--to <ms>` - Time range filter

### `jitstats <trace-file>`

Analyze JIT compilation.

### `cpustacks <trace-file>`

Analyze CPU profiling stacks:
- Top methods by exclusive CPU time
- Group by module or namespace
- SpeedScope export for flame graphs

Options:
- `--format text|json|speedscope`
- `--top <N>` - Number of entries to show
- `--group-by method|module|namespace` - Aggregation level
- `--inclusive` - Sort by inclusive time instead of exclusive
- `--from <ms>` / `--to <ms>` - Time range filter
- `--output <file>` - Output file

Examples:
```bash
# Top 20 methods
pvanalyze cpustacks trace.nettrace --top 20

# Group by module (assembly)
pvanalyze cpustacks trace.nettrace --group-by module --top 10

# Group by namespace, sorted by inclusive time
pvanalyze cpustacks trace.nettrace --group-by namespace --inclusive

# Analyze specific time window
pvanalyze cpustacks trace.nettrace --from 1000 --to 2000 --top 10
```

### `alloc <trace-file>`

Analyze memory allocations by type:
- Shows top allocating types with count, total bytes, and average size
- Identifies Large Object Heap (LOH) allocations
- Group by type, namespace, or module

**Note:** Requires trace collected with allocation events:
```bash
dotnet-trace collect --providers "Microsoft-Windows-DotNETRuntime:0x200001:5" -- dotnet run
```

Options:
- `--format text|json`
- `--top <N>` - Number of types to show
- `--group-by type|namespace|module` - Aggregation level
- `--from <ms>` / `--to <ms>` - Time range filter

### `events <trace-file>`

List and filter events:
- List unique event types (`--list`)
- Filter by type, provider, PID, TID, or payload content
- Time range filtering

Options:
- `--list` - Show event type summary only
- `--type <name>` - Filter by event type
- `--provider <name>` - Filter by provider
- `--pid <id>` - Filter by process ID
- `--tid <id>` - Filter by thread ID
- `--payload <text>` - Search event payload content
- `--limit <N>` - Max events to show
- `--from <ms>` / `--to <ms>` - Time range

### `exceptions <trace-file>`

List exceptions thrown during the trace:
- Summary by exception type
- Individual exception details

Options:
- `--type <name>` - Filter by exception type
- `--from <ms>` / `--to <ms>` - Time range
- `--limit <N>` - Max exceptions to show

### `loader <trace-file>`

Analyze assembly loader/binder events with pre-correlated diagnostics. Correlates `AssemblyLoadStart/Stop`, `ResolutionAttempted`, `KnownPathProbed`, and handler invocation events into per-assembly load records. Outputs JSON by default for agent consumption.

**Note:** Requires trace collected with Binder events (keyword `0x4`):
```bash
# Binder events only
dotnet-trace collect --providers "Microsoft-Windows-DotNETRuntime:4" -- dotnet run

# Binder + Loader events
dotnet-trace collect --providers "Microsoft-Windows-DotNETRuntime:0xC" -- dotnet run
```

Options:
- `--format json|text` - Output format (default: json)
- `--process <name>` - Filter by process name
- `--failures` - Show only failed loads with full resolution trace
- `--slow` - Show only slow loads
- `--threshold <ms>` - Duration threshold for `--slow` (default: 50ms)
- `--assembly <name>` - Correlated timeline for a specific assembly
- `--context <name>` - Filter by AssemblyLoadContext name
- `--probing` - Path probing analysis (hit rate, missed paths)
- `--raw` - Include underlying raw event data
- `--from <ms>` / `--to <ms>` - Time range filter

Diagnostics (stable IDs for automation):

| ID | Severity | Condition |
|----|----------|-----------|
| `LOADER001` | error | Assembly load failures |
| `LOADER002` | warning | Probe miss rate > 50% |
| `LOADER003` | info | Loads exceeding duration threshold |
| `LOADER004` | info | Loads resolved via legacy AppDomain.AssemblyResolve |
| `LOADER005` | info | Same assembly loaded into multiple ALCs |
| `LOADER006` | warning | Version or name mismatches during resolution |

Examples:
```bash
# Triage: summary + diagnostics
pvanalyze loader trace.nettrace

# Drill into failures
pvanalyze loader trace.nettrace --failures

# Why is this assembly failing to load?
pvanalyze loader trace.nettrace --assembly "Contoso.Plugins.Auth"

# What paths are being probed (and missed)?
pvanalyze loader trace.nettrace --probing

# Slow loads over 100ms
pvanalyze loader trace.nettrace --slow --threshold 100

# Human-readable text output
pvanalyze loader trace.nettrace --failures --format text
```

### `calltree <trace-file>`

CPU call tree analysis with hot path detection:
- Aggregated call tree with inclusive/exclusive metrics
- Hot path follows the dominant call chain
- Caller/callee view for any method (supports substring matching)

Options:
- `--depth <N>` - Max tree depth to display (default: 3)
- `--hot-path` - Follow the dominant call chain (child ≥80% of parent)
- `--caller-callee <method>` - Show callers and callees for a method
- `--format text|json` - Output format
- `--from <ms>` / `--to <ms>` - Time range filter

Examples:
```bash
# Call tree to depth 5
pvanalyze calltree trace.nettrace --depth 5

# Hot path — find where CPU time actually goes
pvanalyze calltree trace.nettrace --hot-path

# Who calls a method and what does it call?
pvanalyze calltree trace.nettrace --caller-callee "Serialize"

# JSON output for agent consumption
pvanalyze calltree trace.nettrace --hot-path --format json

# Analyze a specific time window
pvanalyze calltree trace.nettrace --hot-path --from 1000 --to 2000
```

### `snapshot <trace-file>`

Show what was happening at a specific point in time. Provides a cross-cutting view of GC events, CPU samples, exceptions, and event activity within a time window around a given timestamp.

Options:
- `--at <ms>` (required) - Center timestamp in milliseconds
- `--window <ms>` - Half-window size in ms (default: ±100ms)
- `--format text|json` - Output format (default: json)

Examples:
```bash
# What was happening at 1.5 seconds into the trace?
pvanalyze snapshot trace.nettrace --at 1500

# Wider window (±200ms)
pvanalyze snapshot trace.nettrace --at 1500 --window 200

# Human-readable output
pvanalyze snapshot trace.nettrace --at 1500 --format text
```

### `timeline <trace-file>`

Show a unified timeline with multiple event lanes bucketed over time. Useful for correlating different kinds of activity (GC pauses, CPU hotspots, exceptions) across the trace duration.

Options:
- `--lanes <list>` - Comma-separated lanes to include: gc, cpu, exceptions, alloc, jit, events (default: gc,cpu,exceptions)
- `--buckets <N>` - Number of time buckets (default: 50)
- `--from <ms>` / `--to <ms>` - Time range filter
- `--format text|json` - Output format (default: json)

Examples:
```bash
# Default timeline (gc, cpu, exceptions lanes)
pvanalyze timeline trace.nettrace

# Only GC and CPU lanes, higher resolution
pvanalyze timeline trace.nettrace --lanes gc,cpu --buckets 100

# All lanes for a specific time window
pvanalyze timeline trace.nettrace --lanes gc,cpu,exceptions,alloc,jit --from 1000 --to 3000

# Human-readable output
pvanalyze timeline trace.nettrace --format text
```

### `serve`

Start an HTTP server exposing trace analysis as REST API endpoints. Useful for building tooling, dashboards, or integrating with automation workflows.

Options:
- `--port <N>` - Port to listen on (default: 5001)
- `--cors` - Enable CORS for browser-based clients (default: true)

Examples:
```bash
# Start on default port
pvanalyze serve

# Start on a custom port
pvanalyze serve --port 8080
```

#### API Endpoints

Session management:

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/health` | Health check |
| `POST` | `/api/traces/open` | Open a trace file (body: `{ "filePath": "..." }`) |
| `GET` | `/api/traces` | List open trace sessions |
| `GET` | `/api/traces/{id}/info` | Trace metadata |
| `DELETE` | `/api/traces/{id}` | Close a trace session |

Analysis (all scoped to a trace session):

| Method | Endpoint | Query Parameters |
|--------|----------|-----------------|
| `GET` | `/api/traces/{id}/gcstats` | `timeline`, `longest`, `from`, `to`, `process` |
| `GET` | `/api/traces/{id}/jitstats` | `process` |
| `GET` | `/api/traces/{id}/cpustacks` | `top`, `groupBy`, `inclusive`, `from`, `to` |
| `GET` | `/api/traces/{id}/events` | `type`, `provider`, `list`, `limit`, `from`, `to`, `pid`, `tid`, `payload` |
| `GET` | `/api/traces/{id}/exceptions` | `type`, `from`, `to`, `limit` |
| `GET` | `/api/traces/{id}/allocations` | `top`, `groupBy`, `from`, `to` |
| `GET` | `/api/traces/{id}/calltree` | `depth` |
| `GET` | `/api/traces/{id}/calltree/hotpath` | `path` |
| `GET` | `/api/traces/{id}/calltree/children` | `path`, `depth` |
| `GET` | `/api/traces/{id}/calltree/callercallee` | `method` |
| `GET` | `/api/traces/{id}/timeline` | `from`, `to`, `buckets`, `lanes` |
| `GET` | `/api/traces/{id}/snapshot` | `at`, `window` |

WebSocket:

| Endpoint | Description |
|----------|-------------|
| `/ws` | WebSocket connection for streaming analysis |

## JSON Output for Agents

All commands support `--format json` for machine-readable output:

```bash
pvanalyze gcstats trace.nettrace --format json
pvanalyze events trace.nettrace --list --format json
```

## Time Range Filtering

Most commands support `--from` and `--to` for analyzing specific time windows:

```bash
# Analyze GCs between 1-2 seconds into the trace
pvanalyze gcstats trace.nettrace --from 1000 --to 2000

# List events in a time window
pvanalyze events trace.nettrace --from 500 --to 1000 --type GC
```

## Requirements

- .NET 8.0 or later

## Related Tools

- [dotnet-trace](https://docs.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) - Cross-platform trace collection
- [PerfView](https://github.com/microsoft/perfview) - Full-featured Windows GUI for trace analysis
- [SpeedScope](https://www.speedscope.app/) - Interactive flame graph visualization
