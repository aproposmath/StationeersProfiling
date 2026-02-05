#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Linq;
using HarmonyLib;

using UnityEngine;
using ImGuiNET;
using Assets.Scripts;
using Assets.Scripts.UI.ImGuiUi;

namespace StationeersProfiling;

public readonly struct StatSnapshot
{
    public readonly string MethodName;
    public readonly float[] values;

    public StatSnapshot(string methodName, float calls, long totalTicks, long maxTicks, int exceptions)
    {
        MethodName = methodName;

        var totalMilliseconds = (float)(totalTicks * 1000.0 / Stopwatch.Frequency);
        var maxMicroseconds = (float)(maxTicks * 1_000_000.0 / Stopwatch.Frequency);
        var avgMicroseconds = calls > 0f
            ? (float)(totalTicks * 1_000_000.0 / Stopwatch.Frequency / calls)
            : 0f;

        values = new float[5];
        values[0] = calls;
        values[1] = totalMilliseconds;
        values[2] = avgMicroseconds;
        values[3] = maxMicroseconds;
        values[4] = exceptions;
    }

    public float Calls => values[0];
    public float TotalMilliseconds => values[1];
    public float AvgMicroseconds => values[2];
    public float MaxMicroseconds => values[3];
    public float Exceptions => values[4];

    public override string ToString()
        => $"{MethodName} calls={Calls} totalMs={TotalMilliseconds:F3} maxUs={MaxMicroseconds:F0}";
}

public static class Timing
{
    public static List<StatSnapshot> LastSnapshot = [];
    public static bool needsSorting = false;
    public static int sortIndex = 0;
    public static ImGuiSortDirection sortDirection = ImGuiSortDirection.None;

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
                methodName: MethodNames[id],
                calls: calls,
                totalTicks: totalTicks,
                maxTicks: maxTicks,
                exceptions: exceptions
            ));
        }

        LastSnapshot = list;
        needsSorting = true;
        ResetStats();
    }

    public static List<StatSnapshot> SortLastSnapshot()
    {
        if (!needsSorting)
            return LastSnapshot;
        if (sortIndex == 0)
        {
            LastSnapshot = sortDirection == ImGuiSortDirection.Ascending
                ? LastSnapshot.OrderBy(s => s.MethodName).ToList()
                : LastSnapshot.OrderByDescending(s => s.MethodName).ToList();
        }
        if (sortIndex >= 1 && sortIndex <= 5)
        {
            float factor = sortDirection == ImGuiSortDirection.Ascending ? 1.0f : -1.0f;
            LastSnapshot = LastSnapshot.OrderBy(s => factor * s.values[sortIndex - 1]).ToList();
        }
        needsSorting = false;
        return LastSnapshot;
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

    public static void DrawTimings()
    {
        if (!ImGui.BeginTabItem("Custom"))
            return;

        if (ImGui.BeginTable("ImGuiProfilerTable", 6,
            ImGuiTableFlags.Sortable |
              ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.DefaultSort, 500f);
            ImGui.TableSetupColumn("Calls", ImGuiTableColumnFlags.PreferSortDescending, 100f);
            ImGui.TableSetupColumn("Avg us", ImGuiTableColumnFlags.PreferSortDescending, 100f);
            ImGui.TableSetupColumn("Max us", ImGuiTableColumnFlags.PreferSortDescending, 100f);
            ImGui.TableSetupColumn("Total ms", ImGuiTableColumnFlags.PreferSortDescending, 100f);
            ImGui.TableSetupColumn("Exceptions", ImGuiTableColumnFlags.PreferSortDescending, 150f);
            ImGui.TableHeadersRow();

            var sortSpecs = ImGui.TableGetSortSpecs();

            unsafe
            {
                if (sortSpecs.NativePtr != null && sortSpecs.SpecsDirty)
                {
                    var spec = sortSpecs.Specs;
                    needsSorting = true;
                    sortIndex = spec.ColumnIndex;
                    sortDirection = spec.SortDirection;
                    sortSpecs.SpecsDirty = false;
                }
            }


            var snapshot = SortLastSnapshot();

            for (int i = 0; i < snapshot.Count; i++)
            {
                var s = snapshot[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(s.MethodName);
                ImGui.TableSetColumnIndex(1);
                ImGuiProfiler.DrawValue(s.Calls, "0");
                ImGui.TableSetColumnIndex(2);
                ImGuiProfiler.DrawValue(s.TotalMilliseconds, "0.0");
                ImGui.TableSetColumnIndex(3);
                ImGuiProfiler.DrawValue(s.AvgMicroseconds, "0");
                ImGui.TableSetColumnIndex(4);
                ImGuiProfiler.DrawValue(s.MaxMicroseconds, "0");
                ImGui.TableSetColumnIndex(5);
                ImGuiProfiler.DrawValue(s.Exceptions, "0");
            }
        }

        ImGui.EndTable();
        ImGui.EndTabItem();
    }

}

[HarmonyPatch]
public static class TimingPatches
{
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
    [HarmonyPostfix]
    private static void ImGuiProfilerDraw()
    {
        ImGui.Begin("Stationeers Profiler");

        if (EnableInputs)
            CursorManager.SetCursor(false);

        if (ImGui.Checkbox("Capture inputs", ref EnableInputs))
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
        for (int i = 0; i < FunctionSets.Count; i++)
            FunctionSets[i].DrawConfig(i + 1);
        ImGui.End();

        ImGui.Begin("ImGuiProfilerWindow", (ImGuiWindowFlags)799685);
        ImGui.SetWindowPos(Vector2.zero, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(10f, 0f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, ImGuiProfiler._colorRowBg);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, ImGuiProfiler._colorRowBgAlt);
        ImGui.NewLine();

        StationeersProfilingPlugin.Instance.CheckConfig();
    }

    // Append a custom tab to the ImGuiProfiler window
    [HarmonyPatch(typeof(ImGuiProfiler), "Draw")]
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> ImGuiProfiler_Draw_EndTabBar_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var endTabBar = AccessTools.Method(typeof(ImGui), nameof(ImGui.EndTabBar)) ?? throw new MissingMethodException("Could not find ImGuiNET.ImGui.EndTabBar()");
        var myTab = AccessTools.Method(typeof(Timing), nameof(Timing.DrawTimings)) ?? throw new MissingMethodException("Could not find MyClass.MyTab()");
        for (int i = 0; i < codes.Count; i++)
        {
            // When we see the call to ImGui.EndTabBar(), inject our call right before it.
            if ((codes[i].opcode == OpCodes.Call || codes[i].opcode == OpCodes.Callvirt) &&
                codes[i].operand is MethodInfo mi &&
                mi == endTabBar)
            {
                codes.Insert(i, new CodeInstruction(OpCodes.Call, myTab));
                i++; // skip over the inserted instruction
                break; // only patch the first EndTabBar occurrence
            }
        }
        return codes;
    }
}
