// Timing.cs
// Thread-safe Harmony-based timing utility for Unity / Stationeers mods.
//
// Assumptions:
// - Methods have no overloads.
// - Method names are unique across the code you track.
//
// Design:
// - At Track() time, each method is assigned an integer MethodId.
// - Hot-path timing uses MethodId (int) in __state to avoid string/dictionary work.
// - Aggregation is array-backed: _stats[methodId].
// - Snapshot() stable-reads stats and also returns the associated method name.
//
// Notes:
// - Thread safe for concurrent calls.
// - Start()/Stop() toggles collection with a cheap volatile flag.
// - Finalizer ensures time is recorded even when the target throws.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Util.Commands;

using UnityEngine;
using ImGuiNET;
using Assets.Scripts;
using Assets.Scripts.UI.ImGuiUi;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Atmospherics;

namespace StationeersPrivatePatches;

public readonly struct StatSnapshot
{
    public readonly int MethodId;
    public readonly string MethodName;
    public readonly long Calls;
    public readonly long TotalTicks;
    public readonly long MaxTicks;
    public readonly double TotalMilliseconds;
    public readonly double MaxMilliseconds;
    public readonly int Exceptions;
    
    public float MaxMicroseconds => (float)MaxMilliseconds * 1000;
    public float AvgMicroseconds => (float)TotalMilliseconds * 1000;

    public StatSnapshot(int methodId, string methodName, long calls, long totalTicks, long maxTicks, double totalMilliseconds, double maxMilliseconds, int exceptions)
    {
        MethodId = methodId;
        MethodName = methodName;
        Calls = calls;
        TotalTicks = totalTicks;
        MaxTicks = maxTicks;
        TotalMilliseconds = totalMilliseconds;
        MaxMilliseconds = maxMilliseconds;
        Exceptions = exceptions;
    }

    public override string ToString()
        => $"[{MethodId}] {MethodName} calls={Calls} totalMs={TotalMilliseconds:F3} maxMs={MaxMilliseconds:F3}";

}


public static class Timing
{
    // ---------------------------------
    // Public API
    // ---------------------------------
    public static List<StatSnapshot> LastSnapshot = [];

    /// <summary>Enable timing collection. Patches still run but do almost no work when disabled.</summary>
    public static void Start() => Volatile.Write(ref _enabled, 1);

    /// <summary>
    /// Disable timing collection, wait one second for in-flight calls to finish, then print a snapshot of all timings.
    /// </summary>
    public static void Stop()
    {
        if (Volatile.Read(ref _enabled) == 0)
            return;
        Volatile.Write(ref _enabled, 0);

        // Give in-flight calls a moment to complete so they can still record timings.
        // await Cysharp.Threading.Tasks.UniTask.Delay(100);

        if (ImGuiProfiler.Enabled)
            AddDebugTimes();
        // PrintSnapshot();
    }

    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    /// <summary>
    /// Patch a specific method. Assigns a MethodId at track time so the hot path doesn't need lookups.
    /// </summary>

    private static Harmony _harmony = null;
    
    public static void Track(MethodBase method)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));

        // Avoid double patching.
        if (!_tracked.TryAdd(method, 0))
            return;
            
        if(_harmony == null)
            _harmony = new Harmony("stationeers.private.patches.timings");

        // Assign MethodId now (also ensures arrays are grown before patch runs).
        // Use DeclaringType + method name for clearer identification.
        var methodName = method.DeclaringType != null
            ? $"{method.DeclaringType.Name}.{method.Name}"
            : method.Name;
        var methodId = GetOrCreateMethodId(method.MetadataToken, methodName);

        var h = _harmony;

        var prefix = new HarmonyMethod(typeof(Timing), nameof(TimingPrefix));
        var postfix = new HarmonyMethod(typeof(Timing), nameof(TimingPostfix));
        var finalizer = new HarmonyMethod(typeof(Timing), nameof(TimingFinalizer));

        h.Patch(method, prefix: prefix, postfix: postfix, finalizer: finalizer);
    }

    /// <summary>Convenience overload: find and track by type + unique method name.</summary>
    public static void Track(Type declaringType, string methodName)
    {
        if (declaringType == null) throw new ArgumentNullException(nameof(declaringType));
        if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("Method name is required.", nameof(methodName));

        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var mb = declaringType.GetMethod(methodName, bf);
        if (mb == null)
            throw new MissingMethodException(declaringType.FullName, methodName);

        Track(mb);
    }

    /// <summary>
    /// Returns a point-in-time snapshot of the aggregated timings.
    /// Safe to call while timings are being collected.
    /// </summary>
    public static IReadOnlyList<StatSnapshot> Snapshot()
    {
        // Snapshot uses the stable method name list length as the authoritative range.
        var names = Volatile.Read(ref _methodNames);
        var stats = Volatile.Read(ref _stats);

        var n = names.Length;
        var list = new List<StatSnapshot>(n);

        for (int id = 0; id < n; id++)
        {
            // stats array should be at least as large as names array; be defensive.
            if (id >= stats.Length)
                break;

            var s = stats[id];
            if (s == null)
                continue;

            var calls = Volatile.Read(ref s.Calls);
            var totalTicks = Volatile.Read(ref s.TotalTicks);
            var maxTicks = Volatile.Read(ref s.MaxTicks);
            var exceptions = Volatile.Read(ref s.NumExceptions);

            list.Add(new StatSnapshot(
                methodId: id,
                methodName: names[id],
                calls: calls,
                totalTicks: totalTicks,
                maxTicks: maxTicks,
                totalMilliseconds: TicksToMilliseconds(totalTicks),
                maxMilliseconds: TicksToMilliseconds(maxTicks),
                exceptions: exceptions
            ));
        }

        // Most useful ordering: total time descending.
        list.Sort((a, b) => b.TotalTicks.CompareTo(a.TotalTicks));
        LastSnapshot = list;
        return list;
    }

    /// <summary>Clears collected stats (does not unpatch and does not clear MethodId assignments).</summary>
    public static void Reset()
    {
        var stats = Volatile.Read(ref _stats);
        for (int i = 0; i < stats.Length; i++)
        {
            stats[i]?.Reset();
        }
    }

    public static void Clear() {
        if(_harmony != null)
            _harmony.UnpatchSelf();
        _tracked.Clear();
        _methodTokenToId.Clear();
        _methodNames = Array.Empty<string>();
        _stats = Array.Empty<Stat?>();
    }

    private static void TimingPrefix(MethodBase __originalMethod, ref CallState __state)
    {
        if (Volatile.Read(ref _enabled) == 0)
        {
            __state = default;
            return;
        }
        
        var id = _methodTokenToId[__originalMethod.MetadataToken];
        __state = new CallState(id, Stopwatch.GetTimestamp(), started: true);
    }

    private static void TimingPostfix(ref CallState __state)
    {
        if (!__state.Started)
            return;

        Finish(__state.MethodId, __state.StartTimestamp, false);
    }

    private static Exception? TimingFinalizer(Exception? __exception, ref CallState __state)
    {
        if (__state.Started)
            Finish(__state.MethodId, __state.StartTimestamp, __exception != null);

        return __exception;
    }
    
    // ---------------------------------
    // Internal implementation
    // ---------------------------------

    private static int _enabled = 0;

    private static readonly ConcurrentDictionary<MethodBase, byte> _tracked = new ConcurrentDictionary<MethodBase, byte>();
    private static readonly ConcurrentDictionary<int, int> _methodTokenToId = new ConcurrentDictionary<int, int>();

    // Array-backed storage. Only grown under _resizeLock.
    // MethodId is an index into both arrays.
    private static readonly object _resizeLock = new object();
    private static string[] _methodNames = Array.Empty<string>();
    private static Stat?[] _stats = Array.Empty<Stat?>();
    
    private static int GetOrCreateMethodId(int methodToken, string methodName)
    {
        // Fast path: already registered.
        if (_methodTokenToId.TryGetValue(methodToken, out var existing))
            return existing;

        // Slow path: allocate a new id and grow arrays under lock.
        lock (_resizeLock)
        {
            if (_methodTokenToId.TryGetValue(methodToken, out existing))
                return existing;

            var id = _methodNames.Length;

            // grow names
            var newNames = new string[id + 1];
            Array.Copy(_methodNames, newNames, _methodNames.Length);
            newNames[id] = methodName;

            // grow stats
            var newStats = new Stat?[id + 1];
            Array.Copy(_stats, newStats, _stats.Length);
            newStats[id] = new Stat();

            // publish arrays
            Volatile.Write(ref _methodNames, newNames);
            Volatile.Write(ref _stats, newStats);

            // publish mapping
            _methodTokenToId[methodToken] = id;

            return id;
        }
    }

    private static void Finish(int methodId, long startTimestamp, bool hasException)
    {
        var end = Stopwatch.GetTimestamp();
        var delta = end - startTimestamp;
        if (delta < 0) delta = 0;

        var stats = Volatile.Read(ref _stats);
        if ((uint)methodId >= (uint)stats.Length)
            return;

        var stat = stats[methodId];
        stat?.Add(delta, hasException);
    }

    private static void AddDebugTimes()
    {
        var times = ImGuiProfiler._debugLines;
        var snapshot = Snapshot();
        /* 
        for (int i = 0; i < snapshot.Count; i++)
        {
            var s = snapshot[i];
            var avgMs = s.Calls > 0 ? (s.TotalMilliseconds / s.Calls) : 0.0;
            var key = " " + s.MethodName;

            if (!times.TryGetValue(key, out var value))
            {
                value = new DebugLine();
                times.Add(key, value);
            }
            value.Set((long)s.TotalMilliseconds);

            // L.Debug(
            //     $"{i + 1,3}. [{s.MethodId}] {s.MethodName.PadRight(40, ' ')}  calls={s.Calls.ToString().PadLeft(6, ' ')}  totalMs={s.TotalMilliseconds:F3}  avgMs={avgMs:F6}  maxMs={s.MaxMilliseconds:F3}"
            // );
        }
        */
        Reset();
    }

    private static double TicksToMilliseconds(long stopwatchTicks)
        => (stopwatchTicks * 1000.0) / Stopwatch.Frequency;

    // ---------------------------------
    // Data structures
    // ---------------------------------

    private sealed class Stat
    {
        public long TotalTicks;
        public long MaxTicks;
        public int Calls;
        public int NumExceptions;

        public void Add(long deltaTicks, bool hasException)
        {
            Interlocked.Increment(ref Calls);
            Interlocked.Add(ref TotalTicks, deltaTicks);
            if(hasException)
                Interlocked.Increment(ref NumExceptions);

            long current;
            while (deltaTicks > (current = Volatile.Read(ref MaxTicks)))
            {
                if (Interlocked.CompareExchange(ref MaxTicks, deltaTicks, current) == current)
                    break;
            }
        }

        public void Reset()
        {
            Volatile.Write(ref Calls, 0);
            Volatile.Write(ref TotalTicks, 0);
            Volatile.Write(ref MaxTicks, 0);
            Volatile.Write(ref NumExceptions, 0);
        }
    }

    private readonly struct CallState
    {
        public readonly int MethodId;
        public readonly long StartTimestamp;
        public readonly bool Started;
        public readonly bool HasException;

        public CallState(int methodId, long startTimestamp, bool started, bool hasException=false)
        {
            MethodId = methodId;
            StartTimestamp = startTimestamp;
            Started = started;
            HasException = hasException;
        }
    }
}

class TimingCommand : CommandBase
{
    public override string HelpText => "timing";

    public override string[] Arguments { get; } = new string[] { };

    public override bool IsLaunchCmd { get; }

    public override string Execute(string[] args)
    {
        Timing.Start();
        return "timing started";
    }
}

[HarmonyPatch]
public static class TimingPatches
{
    static int i = 0;
    static Dictionary<long, Atmosphere> lookup = new Dictionary<long, Atmosphere>(65536);

    [HarmonyPatch(typeof(ImGuiProfiler))]
    [HarmonyPatch(nameof(ImGuiProfiler.Begin))]
    [HarmonyPrefix]
    static void BeginPrefix()
    {
        if (!ImGuiProfiler.Enabled)
            return;
        Timing.Start();
    }

    [HarmonyPatch(typeof(ImGuiProfiler))]
    [HarmonyPatch(nameof(ImGuiProfiler.End))]
    [HarmonyPrefix]
    static void EndPrefix()
    {
        Timing.Stop();
    }

    [HarmonyPatch(typeof(AtmosphericsManager))]
    [HarmonyPatch(nameof(AtmosphericsManager.Find))]
    [HarmonyPrefix]
    static bool FindPrefix(ref Atmosphere __result, ref WorldGrid worldGrid)
    {
        return true;
        if (lookup.Count == 0)
            return true;

        var g = worldGrid.Value;
        long index = ((long)g.x << 42) + ((long)g.y << 21) + (long)g.z;
        lock (AtmosphericsManager.AllWorldAtmospheresLookUp)
        {
            if (lookup.TryGetValue(index, out __result))
                return false;
        }
        return true;
    }

    [HarmonyPatch(typeof(ImGuiProfiler))]
    [HarmonyPatch(nameof(ImGuiProfiler.Draw))]
    [HarmonyPrefix]
    private static bool ImGuiProfilerDraw()
    {
        ImGui.Begin("ImGuiProfilerWindow", (ImGuiWindowFlags)799685);
        ImGui.SetWindowPos(Vector2.zero, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(10f, 0f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, ImGuiProfiler._colorRowBg);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, ImGuiProfiler._colorRowBgAlt);
        if (ImGui.BeginTable("ImGuiProfilerTable", 6, (ImGuiTableFlags)10320))
        {
            ImGui.TableSetupColumn("Custom timings (accumulated per thread)", 500f);
            ImGui.TableSetupColumn("Calls", 100f);
            ImGui.TableSetupColumn("Avg us", 100f);
            ImGui.TableSetupColumn("Max us", 100f);
            ImGui.TableSetupColumn("Total ms", 100f);
            ImGui.TableSetupColumn("Exceptions", 150f);
            ImGui.TableHeadersRow();

            var snapshot = Timing.LastSnapshot;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var s = snapshot[i];
                var avgUs = 1000.0f * (float)(s.Calls > 0 ? (s.TotalMilliseconds / s.Calls) : 0.0);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(s.MethodName);
                ImGui.TableSetColumnIndex(1);
                ImGuiProfiler.DrawValue((float)s.Calls, "0");
                ImGui.TableSetColumnIndex(2);
                ImGuiProfiler.DrawValue(avgUs, "0");
                ImGui.TableSetColumnIndex(3);
                ImGuiProfiler.DrawValue(s.MaxMicroseconds, "0");
                ImGui.TableSetColumnIndex(4);
                ImGuiProfiler.DrawValue((float)s.TotalMilliseconds, "0");
                ImGui.TableSetColumnIndex(5);
                ImGuiProfiler.DrawValue((float)s.Exceptions, "0");
            }
        }

        ImGui.EndTable();
        ImGui.NewLine();

        if (ImGui.BeginTable("ImGuiProfilerTable", 3, (ImGuiTableFlags)10320))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Cur ms", 100f);
            ImGui.TableSetupColumn("Avg ms", 100f);
            ImGui.TableHeadersRow();

            foreach (KeyValuePair<string, DebugLine> debugLine in ImGuiProfiler._debugLines)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(debugLine.Key);
                ImGui.TableSetColumnIndex(1);
                ImGuiProfiler.DrawValue(debugLine.Value.GetCurrent(), "0.00");
                ImGui.TableSetColumnIndex(2);
                ImGuiProfiler.DrawValue(debugLine.Value.GetAverage(), "0.000");
            }

            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        ImGui.PopStyleColor();
        ImGui.End();
        PrivatePatchesPlugin.Instance.CheckConfig();
        return false;
    }
}
