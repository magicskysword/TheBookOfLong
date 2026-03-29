using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TheBookOfLong;

/// <summary>
/// 负责把 ModsOfLong 中的复杂 JSON 补丁回写到运行时控制器实例。
/// 这层只处理“游戏场景内才存在”的对象型数据，并接入 plotID 的 modXXX 符号 ID 解析。
/// </summary>
internal static partial class GameComplexDataPatchManager
{
    private const string PlotDataSourcePath = "GameData/PlotData.csv";

    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 这里定义了允许被复杂数据补丁接管的目标文件，以及它们映射到的控制器字段和合并策略。
    private static readonly Dictionary<string, PatchTargetDefinition> TargetDefinitionsByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MissionDataController_bountyMissionDataBase.json"] = new PatchTargetDefinition(ControllerKind.MissionData, "bountyMissionDataBase", PatchTargetKind.ArrayByName),
        ["MissionDataController_BranchMissionDataBase.json"] = new PatchTargetDefinition(ControllerKind.MissionData, "BranchMissionDataBase", PatchTargetKind.ArrayByName),
        ["MissionDataController_LittleMissionDataBase.json"] = new PatchTargetDefinition(ControllerKind.MissionData, "LittleMissionDataBase", PatchTargetKind.ArrayByName),
        ["MissionDataController_MainMissionDataBase.json"] = new PatchTargetDefinition(ControllerKind.MissionData, "MainMissionDataBase", PatchTargetKind.ArrayByName),
        ["MissionDataController_SpeKillerMissionDataBase.json"] = new PatchTargetDefinition(ControllerKind.MissionData, "SpeKillerMissionDataBase", PatchTargetKind.ObjectReplace),
        ["MissionDataController_TreasureMapMissionDataBase.json"] = new PatchTargetDefinition(ControllerKind.MissionData, "TreasureMapMissionDataBase", PatchTargetKind.ObjectReplace),
        ["WorldPlotEventController_WorldPlotEventDataBase.json"] = new PatchTargetDefinition(ControllerKind.WorldPlotEvent, "WorldPlotEventDataBase", PatchTargetKind.ArrayByName)
    };

    // 先把文件解析并缓存起来，等场景内控制器就绪后再统一应用，避免过早触发 IL2CPP 空引用。
    private static readonly List<ComplexJsonPatchFile> LoadedPatchFiles = new();

    private static string _modsOfLongRoot = string.Empty;

    // 导出流程会依赖这个状态，确保复杂数据补丁先完成，再导出最终生效后的结果。
    private static ApplyState _applyState;
    private static bool _isInitialized;

    internal static bool IsApplyCompleted
    {
        get
        {
            lock (Sync)
            {
                return _applyState is ApplyState.Completed or ApplyState.Failed or ApplyState.NoPatches;
            }
        }
    }

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (_isInitialized)
            {
                return;
            }

            if (!EnsureInitialized())
            {
                return;
            }

            LoadPatchFiles();
            _isInitialized = true;
        }
    }

    internal static void TryStartApply()
    {
        lock (Sync)
        {
            if (!_isInitialized && !EnsureInitialized())
            {
                _applyState = ApplyState.Failed;
                return;
            }

            if (!_isInitialized)
            {
                LoadPatchFiles();
                _isInitialized = true;
            }

            if (_applyState != ApplyState.NotStarted)
            {
                return;
            }

            _applyState = LoadedPatchFiles.Count == 0
                ? ApplyState.NoPatches
                : ApplyState.WaitingForSceneData;
        }

        if (LoadedPatchFiles.Count > 0)
        {
            MelonLoader.MelonCoroutines.Start(WaitAndApplyPatches());
        }
    }

    private enum ApplyState
    {
        NotStarted,
        NoPatches,
        WaitingForSceneData,
        Applying,
        Completed,
        Failed
    }

    private enum ControllerKind
    {
        MissionData,
        WorldPlotEvent
    }

    private enum PatchTargetKind
    {
        ArrayByName,
        ObjectReplace
    }

    private sealed class PatchTargetDefinition
    {
        internal PatchTargetDefinition(ControllerKind controllerKind, string memberName, PatchTargetKind patchTargetKind)
        {
            ControllerKind = controllerKind;
            MemberName = memberName;
            PatchTargetKind = patchTargetKind;
        }

        internal ControllerKind ControllerKind { get; }

        internal string MemberName { get; }

        internal PatchTargetKind PatchTargetKind { get; }
    }

    private sealed class ComplexJsonPatchFile
    {
        public string ModName { get; set; } = string.Empty;

        public string FullPath { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public int LoadOrder { get; set; }

        public JsonElement RootElement { get; set; }

        public PatchTargetDefinition Target { get; set; } = null!;
    }

    private sealed class PatchApplyResult
    {
        public string ModName { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public PatchTargetKind PatchTargetKind { get; set; }

        public int AddedCount { get; set; }

        public int ModifiedCount { get; set; }

        public int ReplacedCount { get; set; }
    }

    /// <summary>
    /// 反射写入时统一缓存字段/属性元数据，避免在递归反序列化时到处散落反射分支判断。
    /// </summary>
    private sealed class PatchableMember
    {
        internal PatchableMember(
            string name,
            Type valueType,
            Func<object, object?> getter,
            Action<object, object?> setter)
        {
            Name = name;
            ValueType = valueType;
            Getter = getter;
            Setter = setter;
        }

        internal string Name { get; }

        internal Type ValueType { get; }

        internal Func<object, object?> Getter { get; }

        internal Action<object, object?> Setter { get; }
    }

    private sealed class DataModInfoFile
    {
        public string? Name { get; set; }
    }
}
