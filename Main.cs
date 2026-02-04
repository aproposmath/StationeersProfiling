namespace StationeersProfiling;

using BepInEx.Configuration;
using Assets.Scripts.Objects.Electrical;
using System;
using BepInEx;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using Util.Commands;
using ImGuiNET;
using UnityEngine;
using System.Diagnostics;

using Cysharp.Threading.Tasks;

public class FunctionSet
{
    public ConfigEntry<bool> Enabled;
    public ConfigEntry<string> Prefix;
    public ConfigEntry<string> Suffix;
    public ConfigEntry<string> Names;

    public FunctionSet(ConfigFile config, int index)
    {
        string section = "Set_" + index.ToString().PadLeft(2, '0');
        string description = "";
        string prefix = "";
        string suffix = "";
        string names = "";
        if (index == 0)
        {
            description = "Defines a set of functions to track, use multiple names separated by commas, the names will be prefixed and suffixed with the Prefix and Suffix values.";
            names = "ProgrammableChip.Execute";
        }
        if (index == 1)
        {
            prefix = "ProgrammableChip._";
            suffix = "_Operation.Execute";
            names = "ADD,MUL,SUB,DIV,SB,LB,SBN,LBN";
        }
        Enabled = config.Bind(section, "Enabled", true);
        Names = config.Bind(section, "Names", names, description);
        Prefix = config.Bind(section, "Prefix", prefix);
        Suffix = config.Bind(section, "Suffix", suffix);
    }

    public List<string> FunctionNames
    {
        get
        {
            List<string> res = new List<string>();
            if (!Enabled.Value)
                return res;
            var prefix = Prefix.Value.Trim();
            var suffix = Suffix.Value.Trim();
            foreach (var name in Names.Value.Split(','))
                if (!string.IsNullOrWhiteSpace(name))
                    res.Add($"{prefix}{name.Trim()}{suffix}");
            return res;
        }
    }

    public void DrawConfig(int index)
    {
        ImGui.Separator();

        var style = ImGui.GetStyle();

        ImGui.PushID(index);

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.x, style.FramePadding.y + 2f));
        ImGui.BeginGroup();

        bool enabled = Enabled.Value;
        if (ImGui.Checkbox("", ref enabled))
            Enabled.Value = enabled;

        ImGui.SameLine();

        ImGui.TextUnformatted($"Function Set {index:D2}");

        ImGui.TextUnformatted("Prefix:");
        ImGui.SameLine();
        string prefixVal = Prefix.Value ?? "";
        ImGui.SetNextItemWidth(270f);
        if (ImGui.InputText("##Prefix", ref prefixVal, 2048))
            Prefix.Value = prefixVal;

        ImGui.SameLine();
        ImGui.TextUnformatted(" Suffix:");
        ImGui.SameLine();
        string suffixVal = Suffix.Value ?? "";
        ImGui.SetNextItemWidth(270f);
        if (ImGui.InputText("##Suffix", ref suffixVal, 2048))
            Suffix.Value = suffixVal;

        // ImGui.TextUnformatted("Names:");
        string namesVal = Names.Value ?? "";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline($"##Names{index}", ref namesVal, 8192, new Vector2(0, ImGui.GetTextLineHeight() * 2.5f)))
            Names.Value = namesVal;

        ImGui.EndGroup();

        ImGui.PopStyleVar(2);
        ImGui.PopID();
    }
}

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class StationeersProfilingPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "aproposmath-stationeers-profiling";
    public const string PluginName = ThisAssembly.AssemblyName;
    public const string PluginVersion = ThisAssembly.AssemblyVersion;
    public const string PluginLongVersion = ThisAssembly.AssemblyInformationalVersion;
    public static Harmony _harmony;
    public static StationeersProfilingPlugin Instance = null;

    public static List<FunctionSet> FunctionSets;

    private bool HasConfigChanged = false;
    private long HasConfigChangedTick = 0;
    public const int N_FUNCTION_SETS = 20;

    private async UniTaskVoid OnConfigChanged()
    {
        var sw = new Stopwatch();
        long tick = sw.ElapsedTicks;
        HasConfigChangedTick = tick;
        await UniTask.Delay(1000);
        if (HasConfigChangedTick == tick)
            HasConfigChanged = true;
    }

    private void BindAllConfigs()
    {
        FunctionSets = [];
        for (int i = 0; i < N_FUNCTION_SETS; i++)
        {
            FunctionSets.Add(new FunctionSet(Config, i));
        }

        Config.SettingChanged += (val, ev) =>
        {
            OnConfigChanged().Forget();
        };
    }

    public void CheckConfig()
    {
        if (HasConfigChanged)
            Init();
        else
            Config.Reload();
    }

    public void Init()
    {
        Timing.Clear();

        var functions = new List<string>();
        foreach (var set in FunctionSets)
        {
            functions.AddRange(set.FunctionNames);
        }

        functions = functions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assets.Scripts.UI.ImGuiUi.ImGuiProfiler.Clear();
        if (functions.Count > 0)
        {
            var asm = typeof(ProgrammableChip).Assembly; // likely Assembly-CSharp in-game
            var types = asm.GetTypes();
            var typeByFullName = types
                .Where(t => !string.IsNullOrEmpty(t.FullName))
                .GroupBy(t => t.FullName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key.Replace("+", "."), g => g.First(), StringComparer.Ordinal);

            foreach (var func in functions)
            {
                try
                {
                    var trimmed = func.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    string typeName;
                    string methodName;

                    int sep = trimmed.LastIndexOf('.');
                    int altSep = trimmed.LastIndexOf(':');
                    if (altSep > sep) sep = altSep;

                    if (sep <= 0 || sep >= trimmed.Length - 1)
                    {
                        this.Logger.LogWarning($"TrackFunctions entry '{trimmed}' is not in the form 'Type.Method' or 'Namespace.Type.Method' - skipping");
                        continue;
                    }

                    typeName = trimmed.Substring(0, sep).Trim();
                    methodName = trimmed.Substring(sep + 1).Trim();

                    if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
                    {
                        this.Logger.LogWarning($"TrackFunctions entry '{trimmed}' has empty type or method - skipping");
                        continue;
                    }

                    // 1) If typeName looks like a full name, try direct match in asm.
                    if (!typeByFullName.TryGetValue(typeName, out Type resolvedType))
                    {
                        // 2) Try by simple name (including nested types as Outer+Inner)
                        resolvedType = types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));

                        // 3) Try with provided namespaces: ns + "." + typeName
                        if (resolvedType == null)
                        {
                            foreach (var ns in Namespaces)
                            {
                                var candidateFullName = ns + "." + typeName;
                                if (typeByFullName.TryGetValue(candidateFullName, out resolvedType))
                                    break;
                            }
                        }
                    }

                    if (resolvedType == null)
                    {
                        this.Logger.LogWarning($"Could not resolve type for '{trimmed}' (type '{typeName}') - skipping");
                        continue;
                    }

                    var hasMethod = resolvedType.GetMethods(System.Reflection.BindingFlags.Public |
                                                            System.Reflection.BindingFlags.NonPublic |
                                                            System.Reflection.BindingFlags.Instance |
                                                            System.Reflection.BindingFlags.Static)
                                                 .Any(m => string.Equals(m.Name, methodName, StringComparison.Ordinal));

                    if (!hasMethod)
                    {
                        this.Logger.LogWarning($"Resolved type '{resolvedType.FullName}' but could not find method '{methodName}' for '{trimmed}' - skipping");
                        continue;
                    }

                    this.Logger.LogInfo($"Timing {resolvedType.FullName}.{methodName}");
                    Timing.Track(resolvedType, methodName);
                }
                catch (Exception ex)
                {
                    this.Logger.LogError($"Failed to process timing function entry '{func}': {ex}");
                }
            }
        }

        HasConfigChanged = false;
    }

    private void Awake()
    {
        try
        {
            Instance = this;
            this.Logger.LogInfo(
                $"Awake ${PluginName} {PluginLongVersion}");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            BindAllConfigs();
            Init();
        }
        catch (Exception ex)
        {
            this.Logger.LogError($"Error during ${PluginName} {PluginLongVersion} init: {ex}");
        }
    }

    private void OnDestroy()
    {
        try
        {
            this.Logger.LogInfo($"OnDestroy ${PluginName} {PluginLongVersion}");
            // CutScenePatches.CleanupPrefabs();
            if (_harmony == null)
                return;
            _harmony.UnpatchSelf();

            CommandLine._commandsMap.Remove("apro");
        }
        catch
        {

        }
    }


    // hard-coded list of all namespaces in the Stationeers source code, 
    // in case a function name is not found, try prefixing with one of these
    static List<string> Namespaces = new List<string> {
        "Assets.Features.AtmosphericScattering.Code",
        "Assets.Scripts",
        "Assets.Scripts.AssetCreation",
        "Assets.Scripts.Atmospherics",
        "Assets.Scripts.Effects",
        "Assets.Scripts.Emotes",
        "Assets.Scripts.Events",
        "Assets.Scripts.FirstPerson",
        "Assets.Scripts.Genetics",
        "Assets.Scripts.GridSystem",
        "Assets.Scripts.Inventory",
        "Assets.Scripts.Leaderboard",
        "Assets.Scripts.Localization2",
        "Assets.Scripts.Networking",
        "Assets.Scripts.Networking.Transports",
        "Assets.Scripts.Networks",
        "Assets.Scripts.Objects",
        "Assets.Scripts.Objects.Appliances",
        "Assets.Scripts.Objects.Chutes",
        "Assets.Scripts.Objects.Clothing",
        "Assets.Scripts.Objects.Clothing.Suits",
        "Assets.Scripts.Objects.Electrical",
        "Assets.Scripts.Objects.Electrical.Helper",
        "Assets.Scripts.Objects.Entities",
        "Assets.Scripts.Objects.Items",
        "Assets.Scripts.Objects.Motherboards",
        "Assets.Scripts.Objects.Motherboards.Comms",
        "Assets.Scripts.Objects.Pipes",
        "Assets.Scripts.Objects.Structures",
        "Assets.Scripts.Objects.Weapons",
        "Assets.Scripts.OpenNat",
        "Assets.Scripts.PlayerInfo",
        "Assets.Scripts.Serialization",
        "Assets.Scripts.Sound",
        "Assets.Scripts.UI",
        "Assets.Scripts.UI.CustomScrollPanel",
        "Assets.Scripts.UI.Genetics",
        "Assets.Scripts.UI.HelperHints",
        "Assets.Scripts.UI.HelperHints.Extensions",
        "Assets.Scripts.UI.ImGuiUi",
        "Assets.Scripts.UI.Motherboard",
        "Assets.Scripts.Util",
        "Assets.Scripts.Vehicles",
        "Assets.Scripts.Voxel",
        "Assets.Scripts.Weather",
        "AtmosphericScatteringONeils",
        "Audio",
        "CharacterCustomisation",
        "CharacterCustomisation.Clothing",
        "ch.sycoforge.Flares",
        "ColorBlindUtility.UGUI",
        "DefaultNamespace",
        "DLC",
        "Effects",
        "GameEventBus",
        "GameEventBus.Events",
        "GameEventBus.Extensions",
        "GameEventBus.Interfaces",
        "Genetics",
        "InputSystem",
        "LeTai.Asset.TranslucentImage",
        "LeTai.Asset.TranslucentImage.Demo",
        "Messages",
        "Networking",
        "Networking.GameSessions",
        "Networking.Lobbies",
        "Networking.Servers",
        "Networks",
        "Objects",
        "Objects.Components",
        "Objects.DeviceParts",
        "Objects.Electrical",
        "Objects.Items",
        "Objects.LandingPads",
        "Objects.Pipes",
        "Objects.RoboticArm",
        "Objects.Rockets",
        "Objects.Rockets.Log",
        "Objects.Rockets.Log.RocketEvents",
        "Objects.Rockets.Mining",
        "Objects.Rockets.Scanning",
        "Objects.Rockets.UI",
        "Objects.Rockets.UI.Models",
        "Objects.Structures",
        "Open.Nat",
        "Properties",
        "Reagents",
        "Rendering",
        "Rendering.BatchRendering",
        "Rooms",
        "SimpleSpritePacker",
        "Sound",
        "StormVolumes",
        "TerrainSystem",
        "TerrainSystem.Lods",
        "ThingImport",
        "ThingImport.Thumbnails",
        "TraderUI",
        "Trading",
        "Trading.Waypoints",
        "UI",
        "UI.Dropdown",
        "UI.ImGuiUi",
        "UI.ImGuiUi.Debug",
        "UI.ImGuiUi.ImGuiWindows",
        "UI.LoadGame",
        "UI.Motherboard",
        "UI.PhaseChange",
        "UI.Tooltips",
        "UI.UIFade",
        "UnityEngine",
        "UnityEngine.Networking",
        "Util",
        "Util.Commands",
        "Weather",
        "WorldLogSystem"
        };
}
