#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;

using UnityEngine;
using ImGuiNET;
using Assets.Scripts;
using Assets.Scripts.UI.ImGuiUi;

namespace StationeersProfiling;

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
    public static List<StatSnapshot> LastSnapshot = [];

    public static void Start() => Volatile.Write(ref _enabled, 1);

    public static void Stop()
    {
        if (Volatile.Read(ref _enabled) == 0)
            return;

        Volatile.Write(ref _enabled, 0);

        if (ImGuiProfiler.Enabled)
            SaveSnapshot();
    }

    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    // separate from the plugin Harmony instance to allow unpatching on config changes
    // without affecting other patches.
    private static Harmony _harmony = null;

    public static void Track(MethodBase method, string nameSuffix = "")
    {
        if (method == null) throw new ArgumentNullException(nameof(method));

        // Avoid double patching.
        if (!_tracked.TryAdd(method, 0))
            return;

        if (_harmony == null)
            _harmony = new Harmony("aproposmath-stationeers-profiling-timings");

        // Use class name + method name for clearer identification.
        var methodName = method.DeclaringType != null
            ? $"{method.DeclaringType.Name}.{method.Name}"
            : method.Name;

        AssignMemory(method.MetadataToken, methodName + nameSuffix);

        var h = _harmony;

        var prefix = new HarmonyMethod(typeof(Timing), nameof(TimingPrefix));
        var postfix = new HarmonyMethod(typeof(Timing), nameof(TimingPostfix));
        var finalizer = new HarmonyMethod(typeof(Timing), nameof(TimingFinalizer));

        h.Patch(method, prefix: prefix, postfix: postfix, finalizer: finalizer);
    }

    /// <summary>Convenience overload: find and track by type method name (will use all overloads).</summary>
    public static void Track(Type declaringType, string methodName)
    {
        if (declaringType == null) throw new ArgumentNullException(nameof(declaringType));
        if (string.IsNullOrWhiteSpace(methodName)) throw new ArgumentException("Method name is required.", nameof(methodName));

        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        var methods = declaringType.GetMethods(bf);

        var matchingMethods = new List<MethodBase>();

        for (int i = 0; i < methods.Length; i++)
        {
            var m = methods[i];
            if (m.Name != methodName)
                continue;

            matchingMethods.Add(m);
        }

        if (matchingMethods.Count == 0)
            throw new MissingMethodException(declaringType.FullName, methodName);

        foreach (var m in matchingMethods)
        {
            string suffix = "";
            if (matchingMethods.Count > 1)
            {
                var parameters = m.GetParameters();
                if (parameters.Length == 0)
                {
                    suffix = "()";
                }
                else
                {
                    var parts = new string[parameters.Length];
                    for (int pi = 0; pi < parameters.Length; pi++)
                        parts[pi] = parameters[pi].ParameterType.Name;

                    suffix = $"({string.Join(", ", parts)})";
                }
            }
            Track(m, suffix);
        }
    }

    public static void SaveSnapshot()
    {
        // Snapshot uses the stable method name list length as the authoritative range.
        var n = MethodNames.Count;
        var list = new List<StatSnapshot>(n);

        for (int id = 0; id < n; id++)
        {
            var s = Stats[id];
            if (s == null)
                continue;

            var calls = Volatile.Read(ref s.Calls);
            var totalTicks = Volatile.Read(ref s.TotalTicks);
            var maxTicks = Volatile.Read(ref s.MaxTicks);
            var exceptions = Volatile.Read(ref s.NumExceptions);

            list.Add(new StatSnapshot(
                methodId: id,
                methodName: MethodNames[id],
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
        ResetStats();
    }

    public static void ResetStats()
    {
        for (int i = 0; i < Stats.Count; i++)
            Stats[i]?.Reset();
    }

    public static void Clear()
    {
        _harmony?.UnpatchSelf();
        _tracked.Clear();
        _methodTokenToId.Clear();
        MethodNames.Clear();
        Stats.Clear();
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

    private static readonly List<string> MethodNames = new(1024);
    private static readonly List<Stat?> Stats = new(1024);

    private static void AssignMemory(int methodToken, string methodName)
    {
        if (_methodTokenToId.ContainsKey(methodToken))
            return;
        var id = MethodNames.Count;

        MethodNames.Add(methodName);
        Stats.Add(new Stat());
        _methodTokenToId[methodToken] = id;
    }

    private static void Finish(int methodId, long startTimestamp, bool hasException)
    {
        var end = Stopwatch.GetTimestamp();
        var delta = end - startTimestamp;
        if (delta < 0) delta = 0;

        if (methodId >= Stats.Count)
            return;

        var stat = Stats[methodId];
        stat?.Add(delta, hasException);
    }

    private static double TicksToMilliseconds(long stopwatchTicks)
        => stopwatchTicks * 1000.0 / Stopwatch.Frequency;

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
            if (hasException)
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

        public CallState(int methodId, long startTimestamp, bool started, bool hasException = false)
        {
            MethodId = methodId;
            StartTimestamp = startTimestamp;
            Started = started;
            HasException = hasException;
        }
    }
}

[HarmonyPatch]
public static class TimingPatches
{
    static bool ShowVanillaTable = true;
    static bool EnableInputs = false;

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

    [HarmonyPatch(typeof(ImGuiProfiler))]
    [HarmonyPatch(nameof(ImGuiProfiler.Draw))]
    [HarmonyPrefix]
    private static bool ImGuiProfilerDraw()
    {
        ImGui.Begin("Stationeers Profiler");
        
        ImGui.Checkbox("Show Vanilla Table", ref ShowVanillaTable);
        ImGui.SameLine();
        ImGui.TextUnformatted("    ");
        ImGui.SameLine();
        
        if(EnableInputs)
            CursorManager.SetCursor(false);

        if (ImGui.Checkbox("Capture inputs", ref EnableInputs) )
        {
            if (EnableInputs)
            {
                KeyManager.SetInputState("stationeersprofiler", KeyInputState.Typing);
            }
            else
            {
                CursorManager.SetCursor(true);
                KeyManager.RemoveInputState("stationeersprofiler");
            }
        }
        
        
        var FunctionSets = StationeersProfilingPlugin.FunctionSets;
        for(int i =0; i < FunctionSets.Count; i++)
            FunctionSets[i].DrawConfig(i+1);
        ImGui.End();

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

        if (ShowVanillaTable)
        {
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
        }
        StationeersProfilingPlugin.Instance.CheckConfig();
        return false;
    }
}
