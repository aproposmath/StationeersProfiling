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

[HarmonyPatch]
public static class ImGuiProfilerPatches
{
    class Times
    {
        public float[] _times;
        public string Name;
        public const int MaxSamples = 60;
        public DebugLine Line = null;
        public Times(string name)
        {
            Name = name;
            _times = new float[MaxSamples];
            for (int i = 0; i < _times.Length; i++)
                _times[i] = 0f;
        }
        public void Update()
        {
            for (int i = 0; i < _times.Length - 1; i++)
                _times[i] = _times[i + 1];
            _times[MaxSamples - 1] = Line?.GetCurrent() ?? 0f;
        }
    }
    static Dictionary<string, Times> DebugTimes = [];

    public static void UpdateTimes()
    {
        foreach (var times in DebugTimes.Values)
            times.Update();
    }

    static void DrawPlot(string name, DebugLine line)
    {
        if(!DebugTimes.ContainsKey(name) || DebugTimes[name].Line != line)
            DebugTimes[name] = new Times(name);
            
        var times = DebugTimes[name];
        // if (!DebugTimes.TryGetValue(name, out var times))
        // {
        //     times = new Times(name);
            // DebugTimes[name] = times;
        // }
        times.Line = line;
        ImGui.PlotLines("##" + name, ref times._times, Times.MaxSamples, 0, 0, new Vector2(100, 0));
    }

    public static bool needsSorting = false;
    public static int sortIndex = 0;
    public static ImGuiSortDirection sortDirection = ImGuiSortDirection.None;
    public static List<KeyValuePair<string, DebugLine>> sorted = [];

    [HarmonyPatch(typeof(ImGuiProfiler))]
    [HarmonyPatch(nameof(ImGuiProfiler.DrawTab))]
    [HarmonyPrefix]
    private static bool DrawTab(int tabId, string groupKey, DebugGroup group)
    {
        if (!ImGui.BeginTabItem($"{groupKey}###ImGuiProfilerTab{tabId}"))
            return false;

        ImGuiTableFlags flags =
        ImGuiTableFlags.Sortable |
        ImGuiTableFlags.SortMulti |
        ImGuiTableFlags.SortTristate |
        (ImGuiTableFlags)10320;

        if (ImGui.BeginTable($"ImGuiProfilerTable{tabId}", 4, flags))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.None, 0.0f, 0);
            ImGui.TableSetupColumn("Cur ms", ImGuiTableColumnFlags.DefaultSort, 100f, 1);
            ImGui.TableSetupColumn("Avg ms", ImGuiTableColumnFlags.None, 100f, 2);
            ImGui.TableSetupColumn("History", ImGuiTableColumnFlags.NoSort, 100f, 3);
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

            if (sorted.Count == 0 || needsSorting)
            {

                sorted = group.Lines.ToList();
                sorted.Sort((a, b) =>
                    {
                        float aVal = 0, bVal = 0;
                        switch (sortIndex)
                        {
                            case 0: // Name
                                return sortDirection == ImGuiSortDirection.Ascending
                            ? string.Compare(a.Key, b.Key)
                            : string.Compare(b.Key, a.Key);

                            case 1: // Cur ms
                                aVal = a.Value.GetCurrent();
                                bVal = b.Value.GetCurrent();
                                break;

                            case 2: // Avg ms
                                aVal = a.Value.GetAverage();
                                bVal = b.Value.GetAverage();
                                break;
                        }

                        int result = aVal.CompareTo(bVal);
                        return sortDirection == ImGuiSortDirection.Ascending ? result : -result;
                    });
            }

            foreach (var line in sorted)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(line.Key);
                ImGui.TableSetColumnIndex(1);
                ImGuiProfiler.DrawValue(line.Value.GetCurrent(), "0");
                ImGui.TableSetColumnIndex(2);
                ImGuiProfiler.DrawValue(line.Value.GetAverage(), "0.00");
                ImGui.TableSetColumnIndex(3);
                DrawPlot(line.Key, line.Value);
            }
            ImGui.EndTable();
        }
        ImGui.EndTabItem();
        return false;
    }






}
