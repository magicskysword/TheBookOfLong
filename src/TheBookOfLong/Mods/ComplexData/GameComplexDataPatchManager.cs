using System;
using System.Collections.Generic;
using System.Text;

namespace TheBookOfLong;

/// <summary>
/// 负责把 ModsOfLong 中的复杂 JSON 补丁回写到运行时控制器实例。
/// 这层只处理“游戏场景内才存在”的对象型数据，并接入 plotID 的 modXXX 符号 ID 解析。
/// </summary>
internal static partial class GameComplexDataPatchManager
{
    internal const string PlotDataSourcePath = "GameData/PlotData.csv";

    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 这里定义了允许被复杂数据补丁接管的目标文件，以及它们映射到的控制器字段和合并策略。
    private static readonly Dictionary<string, ComplexPatchTargetDefinition> TargetDefinitionsByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MissionDataController_bountyMissionDataBase.json"] = new ComplexPatchTargetDefinition(ComplexControllerKind.MissionData, "bountyMissionDataBase", ComplexPatchTargetKind.ArrayByName),
        ["MissionDataController_BranchMissionDataBase.json"] = new ComplexPatchTargetDefinition(ComplexControllerKind.MissionData, "BranchMissionDataBase", ComplexPatchTargetKind.ArrayByName),
        ["MissionDataController_LittleMissionDataBase.json"] = new ComplexPatchTargetDefinition(ComplexControllerKind.MissionData, "LittleMissionDataBase", ComplexPatchTargetKind.ArrayByName),
        ["MissionDataController_MainMissionDataBase.json"] = new ComplexPatchTargetDefinition(ComplexControllerKind.MissionData, "MainMissionDataBase", ComplexPatchTargetKind.ArrayByName),
        ["MissionDataController_SpeKillerMissionDataBase.json"] = new ComplexPatchTargetDefinition(ComplexControllerKind.MissionData, "SpeKillerMissionDataBase", ComplexPatchTargetKind.ObjectReplace),
        ["MissionDataController_TreasureMapMissionDataBase.json"] = new ComplexPatchTargetDefinition(ComplexControllerKind.MissionData, "TreasureMapMissionDataBase", ComplexPatchTargetKind.ObjectReplace),
        ["WorldPlotEventController_WorldPlotEventDataBase.json"] = new ComplexPatchTargetDefinition(ComplexControllerKind.WorldPlotEvent, "WorldPlotEventDataBase", ComplexPatchTargetKind.ArrayByName)
    };

    // 先把文件解析并缓存起来，等场景内控制器就绪后再统一应用，避免过早触发 IL2CPP 空引用。
    private static readonly List<ComplexJsonPatchFile> LoadedPatchFiles = new();

    // 当前实际流程是先导出原始复杂数据，再等导出完成后应用补丁。
    // 这个状态只负责协调补丁何时开始，不再让调用方自己猜时序。
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

}
