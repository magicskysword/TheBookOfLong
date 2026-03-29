using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;

namespace TheBookOfLong;

internal static partial class GameComplexDataPatchManager
{
    private static IEnumerator WaitAndApplyPatches()
    {
        while (true)
        {
            ApplyState applyState;
            lock (Sync)
            {
                applyState = _applyState;
            }

            if (applyState is ApplyState.Completed or ApplyState.Failed or ApplyState.NoPatches)
            {
                yield break;
            }

            if (IsPatchTargetsReady(out var worldPlotEventController, out var missionDataController))
            {
                ApplyLoadedPatchFiles(worldPlotEventController!, missionDataController!);
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

        return TryGetMemberValue(worldPlotEventController, "WorldPlotEventDataBase", out object? worldPlotEventDataBase)
               && worldPlotEventDataBase is not null
               && TryGetMemberValue(missionDataController, "bountyMissionDataBase", out object? bountyMissionDataBase)
               && bountyMissionDataBase is not null
               && TryGetMemberValue(missionDataController, "MainMissionDataBase", out object? mainMissionDataBase)
               && mainMissionDataBase is not null
               && TryGetMemberValue(missionDataController, "BranchMissionDataBase", out object? branchMissionDataBase)
               && branchMissionDataBase is not null
               && TryGetMemberValue(missionDataController, "LittleMissionDataBase", out object? littleMissionDataBase)
               && littleMissionDataBase is not null
               && TryGetMemberValue(missionDataController, "TreasureMapMissionDataBase", out object? treasureMapMissionDataBase)
               && treasureMapMissionDataBase is not null
               && TryGetMemberValue(missionDataController, "SpeKillerMissionDataBase", out object? speKillerMissionDataBase)
               && speKillerMissionDataBase is not null;
    }

    private static void ApplyLoadedPatchFiles(
        global::Il2Cpp.WorldPlotEventController worldPlotEventController,
        global::Il2Cpp.MissionDataController missionDataController)
    {
        lock (Sync)
        {
            if (_applyState != ApplyState.WaitingForSceneData)
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

            Dictionary<string, List<PatchApplyResult>> resultsByMod = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < patchFiles.Count; i += 1)
            {
                ComplexJsonPatchFile patchFile = patchFiles[i];
                object controller = patchFile.Target.ControllerKind == ControllerKind.WorldPlotEvent
                    ? worldPlotEventController
                    : missionDataController;

                PatchApplyResult applyResult = patchFile.Target.PatchTargetKind == PatchTargetKind.ArrayByName
                    ? ApplyArrayPatch(controller, patchFile)
                    : ApplyObjectPatch(controller, patchFile);

                if (!resultsByMod.TryGetValue(patchFile.ModName, out List<PatchApplyResult>? modResults))
                {
                    modResults = new List<PatchApplyResult>();
                    resultsByMod[patchFile.ModName] = modResults;
                }

                modResults.Add(applyResult);
            }

            foreach ((string modName, List<PatchApplyResult> modResults) in resultsByMod)
            {
                for (int i = 0; i < modResults.Count; i += 1)
                {
                    PatchApplyResult applyResult = modResults[i];
                    if (applyResult.PatchTargetKind == PatchTargetKind.ArrayByName)
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
                _applyState = ApplyState.Completed;
            }
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                _applyState = ApplyState.Failed;
            }

            MelonLoader.MelonLogger.Warning($"Failed to apply game complex data patches: {ex}");
        }
    }

    /// <summary>
    /// 这类补丁的合并主键是 name。
    /// 设计目标是让 mod 只声明关心的条目，已有同名条目覆盖，不存在则直接追加。
    /// </summary>
    private static PatchApplyResult ApplyArrayPatch(object controller, ComplexJsonPatchFile patchFile)
    {
        if (!TryGetMemberValue(controller, patchFile.Target.MemberName, out object? memberValue) || memberValue is null)
        {
            throw new InvalidOperationException($"Could not read member '{patchFile.Target.MemberName}' from '{controller.GetType().FullName}'.");
        }

        Type listType = memberValue.GetType();
        Type elementType = ResolveCollectionElementType(listType)
            ?? throw new InvalidOperationException($"Could not determine element type for '{listType.FullName}'.");

        List<object?> mergedItems = EnumerateCollection(memberValue);
        Dictionary<string, int> indexByName = new(StringComparer.Ordinal);
        for (int i = 0; i < mergedItems.Count; i += 1)
        {
            object? item = mergedItems[i];
            string? itemName = item is null ? null : GetNameValue(item);
            if (!string.IsNullOrWhiteSpace(itemName))
            {
                indexByName[itemName] = i;
            }
        }

        int addedCount = 0;
        int modifiedCount = 0;
        int patchIndex = 0;
        foreach (JsonElement patchElement in patchFile.RootElement.EnumerateArray())
        {
            if (patchElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Patch file '{patchFile.FullPath}' contains a non-object array item at $[{patchIndex}].");
            }

            string patchName = GetRequiredStringProperty(patchElement, "name", patchFile.FullPath, $"$[{patchIndex}]");

            if (indexByName.TryGetValue(patchName, out int existingIndex))
            {
                object? existingItem = mergedItems[existingIndex];
                if (existingItem is null)
                {
                    object? newItem = ConvertJsonElementToValue(patchElement, elementType, patchFile, $"$[{patchIndex}]", memberName: null);
                    SetCollectionItem(memberValue, existingIndex, newItem);
                    mergedItems[existingIndex] = newItem;
                }
                else
                {
                    ApplyJsonObjectToExistingValue(patchElement, existingItem, patchFile, $"$[{patchIndex}]");
                }

                modifiedCount += 1;
            }
            else
            {
                object? newItem = ConvertJsonElementToValue(patchElement, elementType, patchFile, $"$[{patchIndex}]", memberName: null);
                AddCollectionItem(memberValue, newItem);
                indexByName[patchName] = mergedItems.Count;
                mergedItems.Add(newItem);
                addedCount += 1;
            }

            patchIndex += 1;
        }

        return new PatchApplyResult
        {
            ModName = patchFile.ModName,
            RelativePath = patchFile.RelativePath,
            PatchTargetKind = PatchTargetKind.ArrayByName,
            AddedCount = addedCount,
            ModifiedCount = modifiedCount
        };
    }

    private static PatchApplyResult ApplyObjectPatch(object controller, ComplexJsonPatchFile patchFile)
    {
        Type memberType = GetMemberType(controller.GetType(), patchFile.Target.MemberName)
            ?? throw new InvalidOperationException($"Could not determine member type for '{patchFile.Target.MemberName}'.");

        if (!TryGetMemberValue(controller, patchFile.Target.MemberName, out object? existingValue) || existingValue is null)
        {
            object? newValue = ConvertJsonElementToValue(patchFile.RootElement, memberType, patchFile, "$", memberName: patchFile.Target.MemberName);
            SetMemberValue(controller, patchFile.Target.MemberName, newValue);
        }
        else
        {
            ApplyJsonObjectToExistingValue(patchFile.RootElement, existingValue, patchFile, "$");
        }

        return new PatchApplyResult
        {
            ModName = patchFile.ModName,
            RelativePath = patchFile.RelativePath,
            PatchTargetKind = PatchTargetKind.ObjectReplace,
            ReplacedCount = 1
        };
    }
}
