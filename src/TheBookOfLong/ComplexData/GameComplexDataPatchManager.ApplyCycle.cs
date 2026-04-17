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
                && ComplexDataTargets.TryGetReadyControllers(out var worldPlotEventController, out var missionDataController))
            {
                ApplyLoadedPatchFiles(applyCycleId, dumpCycleId, worldPlotEventController!, missionDataController!);
                yield break;
            }

            yield return null;
        }
    }

    private static void ApplyLoadedPatchFiles(
        int applyCycleId,
        int dumpCycleId,
        global::Il2Cpp.WorldPlotEventController worldPlotEventController,
        global::Il2Cpp.MissionDataController missionDataController)
    {
        string targetSignature = ComplexDataTargets.BuildTargetSignature(worldPlotEventController, missionDataController);
        bool skipDuplicate = false;

        lock (Sync)
        {
            if (applyCycleId != _applyCycleId
                || dumpCycleId != _waitingDumpCycleId
                || _applyState != ApplyState.WaitingForSceneData)
            {
                return;
            }

            if (string.Equals(targetSignature, _lastAppliedTargetSignature, StringComparison.Ordinal))
            {
                _applyState = ApplyState.Completed;
                skipDuplicate = true;
            }
            else
            {
                _applyState = ApplyState.Applying;
            }
        }

        if (skipDuplicate)
        {
            MelonLoader.MelonLogger.Msg(
                $"Skipped duplicate game complex data patch cycle {applyCycleId} for dump cycle {dumpCycleId} because target object graph is unchanged.");
            return;
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
                    _lastAppliedTargetSignature = targetSignature;
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
