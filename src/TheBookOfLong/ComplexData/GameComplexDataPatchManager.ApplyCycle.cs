using System;
using System.Collections;
using System.Collections.Generic;

namespace TheBookOfLong;

internal static partial class GameComplexDataPatchManager
{
    // 这一段只负责“等待这一轮 Dump 完成，再把已加载的补丁交给执行器”。
    // 真正的对象写入逻辑放在 ComplexPatchExecutor，避免 manager 同时承担时序和写入细节。
    private static IEnumerator WaitAndApplyPatches(int applyCycleId, int dumpCycleId)
    {
        while (true)
        {
            ApplyState applyState;
            lock (Sync)
            {
                if (applyCycleId != _applyCycleId || dumpCycleId != _waitingDumpCycleId)
                {
                    yield break;
                }

                applyState = _applyState;
            }

            if (applyState is ApplyState.Completed or ApplyState.Failed or ApplyState.NoPatches)
            {
                yield break;
            }

            if (GameComplexDataDumpManager.IsExportCompleted(dumpCycleId)
                && IsPatchTargetsReady(out var worldPlotEventController, out var missionDataController))
            {
                ApplyLoadedPatchFiles(applyCycleId, dumpCycleId, worldPlotEventController!, missionDataController!);
                yield break;
            }

            yield return null;
        }
    }

    private static bool IsPatchTargetsReady(out global::Il2Cpp.WorldPlotEventController? worldPlotEventController,out global::Il2Cpp.MissionDataController? missionDataController)
    {
        worldPlotEventController = global::Il2Cpp.WorldPlotEventController.Instance;
        missionDataController = global::Il2Cpp.MissionDataController.Instance;

        if (worldPlotEventController is null || missionDataController is null)
        {
            return false;
        }

        return ComplexTypeAccessor.TryGetMemberValue(worldPlotEventController, "WorldPlotEventDataBase", out object? worldPlotEventDataBase)
               && worldPlotEventDataBase is not null
               && ComplexTypeAccessor.TryGetMemberValue(missionDataController, "bountyMissionDataBase", out object? bountyMissionDataBase)
               && bountyMissionDataBase is not null
               && ComplexTypeAccessor.TryGetMemberValue(missionDataController, "MainMissionDataBase", out object? mainMissionDataBase)
               && mainMissionDataBase is not null
               && ComplexTypeAccessor.TryGetMemberValue(missionDataController, "BranchMissionDataBase", out object? branchMissionDataBase)
               && branchMissionDataBase is not null
               && ComplexTypeAccessor.TryGetMemberValue(missionDataController, "LittleMissionDataBase", out object? littleMissionDataBase)
               && littleMissionDataBase is not null
               && ComplexTypeAccessor.TryGetMemberValue(missionDataController, "TreasureMapMissionDataBase", out object? treasureMapMissionDataBase)
               && treasureMapMissionDataBase is not null
               && ComplexTypeAccessor.TryGetMemberValue(missionDataController, "SpeKillerMissionDataBase", out object? speKillerMissionDataBase)
               && speKillerMissionDataBase is not null;
    }

    private static void ApplyLoadedPatchFiles(
        int applyCycleId,
        int dumpCycleId,
        global::Il2Cpp.WorldPlotEventController worldPlotEventController,
        global::Il2Cpp.MissionDataController missionDataController)
    {
        lock (Sync)
        {
            if (applyCycleId != _applyCycleId
                || dumpCycleId != _waitingDumpCycleId
                || _applyState != ApplyState.WaitingForSceneData)
            {
                return;
            }

            _applyState = ApplyState.Applying;
        }

        try
        {
            List<ComplexJsonPatchFile> patchFiles;
            lock (Sync)
            {
                patchFiles = new List<ComplexJsonPatchFile>(LoadedPatchFiles);
            }

            Dictionary<string, List<ComplexPatchApplyResult>> resultsByMod = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < patchFiles.Count; i += 1)
            {
                ComplexJsonPatchFile patchFile = patchFiles[i];
                object controller = patchFile.Target.ControllerKind == ComplexControllerKind.WorldPlotEvent
                    ? worldPlotEventController
                    : missionDataController;

                ComplexPatchApplyResult applyResult = ComplexPatchExecutor.ApplyPatch(controller, patchFile);

                if (!resultsByMod.TryGetValue(patchFile.ModName, out List<ComplexPatchApplyResult>? modResults))
                {
                    modResults = new List<ComplexPatchApplyResult>();
                    resultsByMod[patchFile.ModName] = modResults;
                }

                modResults.Add(applyResult);
            }

            foreach ((string modName, List<ComplexPatchApplyResult> modResults) in resultsByMod)
            {
                for (int i = 0; i < modResults.Count; i += 1)
                {
                    ComplexPatchApplyResult applyResult = modResults[i];
                    if (applyResult.PatchTargetKind == ComplexPatchTargetKind.ArrayByName)
                    {
                        MelonLoader.MelonLogger.Msg(
                            $"Game complex data mod '{modName}' patched '{applyResult.RelativePath}': added {applyResult.AddedCount}, modified {applyResult.ModifiedCount}");
                    }
                    else
                    {
                        MelonLoader.MelonLogger.Msg(
                            $"Game complex data mod '{modName}' patched '{applyResult.RelativePath}': replaced {applyResult.ReplacedCount}");
                    }
                }
            }

            lock (Sync)
            {
                if (applyCycleId == _applyCycleId && dumpCycleId == _waitingDumpCycleId)
                {
                    _applyState = ApplyState.Completed;
                }
            }
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                if (applyCycleId == _applyCycleId && dumpCycleId == _waitingDumpCycleId)
                {
                    _applyState = ApplyState.Failed;
                }
            }

            MelonLoader.MelonLogger.Warning($"Failed to apply game complex data patches: {ex}");
        }
    }
}
