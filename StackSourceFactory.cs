using System.Globalization;
using Diagnostics.Tracing.StackSources;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Stacks;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;

namespace PVAnalyze;

public enum StackSourceKind
{
    Cpu,
    ThreadTime,
    Activity
}

internal static class StackSourceFactory
{
    public static StackSource Create(
        Etlx.TraceLog traceLog,
        StackSourceKind kind,
        double? fromMs,
        double? toMs)
    {
        var capabilities = TraceCapabilityDetector.Detect(traceLog);
        ValidateRequirements(capabilities, kind);

        if (kind == StackSourceKind.Cpu)
        {
            var events = TraceAnalyzer.GetCpuSampleEvents(traceLog, fromMs, toMs);
            return CopyStackSource.Clone(new TraceEventStackSource(events));
        }

        var stackSource = new MutableTraceEventStackSource(traceLog);
        using var symbolReader = new SymbolReader(TextWriter.Null);
#pragma warning disable CS0618 // ThreadTimeStackComputer is marked experimental, not obsolete.
        var computer = new ThreadTimeStackComputer(traceLog, symbolReader)
        {
            ExcludeReadyThread = true,
            UseTasks = kind == StackSourceKind.Activity,
            GroupByStartStopActivity = kind == StackSourceKind.Activity
        };
#pragma warning restore CS0618
        computer.GenerateThreadTimeStacks(stackSource);

        if (!fromMs.HasValue && !toMs.HasValue)
            return stackSource;

        var filter = new FilterParams
        {
            StartTimeRelativeMSec = fromMs?.ToString(CultureInfo.CurrentCulture) ?? "",
            EndTimeRelativeMSec = toMs?.ToString(CultureInfo.CurrentCulture) ?? ""
        };
        return new FilterStackSource(filter, stackSource, ScalingPolicyKind.TimeMetric);
    }

    private static void ValidateRequirements(TraceCapabilities capabilities, StackSourceKind kind)
    {
        if (kind == StackSourceKind.Cpu && !capabilities.SupportsCpuStacks)
        {
            throw new InvalidOperationException(
                "CPU stack analysis requires sampled-profile events. " +
                "Collect with PerfView CPU sampling or dotnet-trace's sample profiler.");
        }

        if (kind is StackSourceKind.ThreadTime or StackSourceKind.Activity &&
            !capabilities.SupportsThreadTime)
        {
            throw new InvalidOperationException(
                $"{GetDisplayName(kind)} analysis requires context-switch events. " +
                "Collect the trace with PerfView /ThreadTime.");
        }

        if (kind == StackSourceKind.Activity && capabilities.StartStopEventCount == 0)
        {
            throw new InvalidOperationException(
                "Start/Stop activity analysis requires EventSource Start/Stop events with activity IDs. " +
                "Enable the relevant application providers when collecting with PerfView /ThreadTime.");
        }
    }

    public static string GetName(StackSourceKind kind) => kind switch
    {
        StackSourceKind.ThreadTime => "thread-time",
        StackSourceKind.Activity => "activity",
        _ => "cpu"
    };

    public static string GetDisplayName(StackSourceKind kind) => kind switch
    {
        StackSourceKind.ThreadTime => "Thread Time",
        StackSourceKind.Activity => "Start/Stop Activity",
        _ => "CPU"
    };
}
