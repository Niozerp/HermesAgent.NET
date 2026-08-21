using System.Diagnostics;

namespace HermesAgent.Tools;

/// <summary>
/// Verbose console debug tracer for DI registration, tool resolution, and tool execution.
///
/// Enable: HERMES_DEBUG=1 (env var). Disable by unsetting it.
///
/// How to read the output when hunting a hang:
///   every traced step logs "START ..." before the work and "OK ..." after it.
///   If the app freezes, scroll to the bottom: the LAST line is either
///     - a "START" with no matching "OK"  -> that component is the one hanging, or
///     - a "TOOL START" with no "TOOL DONE"/"TOOL ERROR" -> that tool call is hanging.
/// Timestamps (seconds since process start) show how long each step took.
/// </summary>
public static class HermesDebug
{
    public static bool Enabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("HERMES_DEBUG"), "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("HERMES_DEBUG"), "true", StringComparison.OrdinalIgnoreCase);

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Write a timestamped debug line to the console (no-op when disabled).</summary>
    public static void Log(string message)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{Clock.Elapsed.TotalSeconds,8:0.000}s] [DBG] ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }

    /// <summary>Trace a synchronous step: logs START, runs the work, logs OK.</summary>
    public static T Step<T>(string name, Func<T> work)
    {
        Log($"START  {name}");
        var result = work();
        Log($"OK     {name}");
        return result;
    }

    /// <summary>Trace a synchronous step with no return value.</summary>
    public static void Step(string name, Action work)
    {
        Log($"START  {name}");
        work();
        Log($"OK     {name}");
    }
}
