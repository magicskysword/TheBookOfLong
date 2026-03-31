using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TheBookOfLong;

/// <summary>
/// 负责把单个 ComplexData 补丁真正写入到运行时控制器对象。
/// 这里不处理时序、不处理等待，只关心“拿到目标对象后怎么改值”。
/// </summary>
internal static class ComplexPatchExecutor
{
    internal static ComplexPatchApplyResult ApplyPatch(object controller, ComplexJsonPatchFile patchFile)
    {
        return patchFile.Target.PatchTargetKind == ComplexPatchTargetKind.ArrayByName
            ? ApplyArrayPatch(controller, patchFile)
            : ApplyObjectPatch(controller, patchFile);
    }

    private static ComplexPatchApplyResult ApplyArrayPatch(object controller, ComplexJsonPatchFile patchFile)
    {
        if (!ComplexTypeAccessor.TryGetMemberValue(controller, patchFile.Target.MemberName, out object? memberValue) || memberValue is null)
        {
            throw new InvalidOperationException($"Could not read member '{patchFile.Target.MemberName}' from '{controller.GetType().FullName}'.");
        }

        Type listType = memberValue.GetType();
        Type elementType = ComplexTypeAccessor.ResolveCollectionElementType(listType)
            ?? throw new InvalidOperationException($"Could not determine element type for '{listType.FullName}'.");

        List<object?> mergedItems = ComplexTypeAccessor.EnumerateCollection(memberValue);
        Dictionary<string, int> indexByName = new(StringComparer.Ordinal);
        for (int i = 0; i < mergedItems.Count; i += 1)
        {
            object? item = mergedItems[i];
            string? itemName = item is null ? null : ComplexTypeAccessor.GetNameValue(item);
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

            string patchName = ComplexTypeAccessor.GetRequiredStringProperty(patchElement, "name", patchFile.FullPath, $"$[{patchIndex}]");

            if (indexByName.TryGetValue(patchName, out int existingIndex))
            {
                object? existingItem = mergedItems[existingIndex];
                if (existingItem is null)
                {
                    object? newItem = ComplexJsonValuePatcher.ConvertJsonElementToValue(patchElement, elementType, patchFile, $"$[{patchIndex}]", memberName: null);
                    ComplexTypeAccessor.SetCollectionItem(memberValue, existingIndex, newItem);
                    mergedItems[existingIndex] = newItem;
                }
                else
                {
                    ComplexJsonValuePatcher.ApplyJsonObjectToExistingValue(patchElement, existingItem, patchFile, $"$[{patchIndex}]");
                }

                modifiedCount += 1;
            }
            else
            {
                object? newItem = ComplexJsonValuePatcher.ConvertJsonElementToValue(patchElement, elementType, patchFile, $"$[{patchIndex}]", memberName: null);
                ComplexTypeAccessor.AddCollectionItem(memberValue, newItem);
                indexByName[patchName] = mergedItems.Count;
                mergedItems.Add(newItem);
                addedCount += 1;
            }

            patchIndex += 1;
        }

        return new ComplexPatchApplyResult
        {
            ModName = patchFile.ModName,
            RelativePath = patchFile.RelativePath,
            PatchTargetKind = ComplexPatchTargetKind.ArrayByName,
            AddedCount = addedCount,
            ModifiedCount = modifiedCount
        };
    }

    private static ComplexPatchApplyResult ApplyObjectPatch(object controller, ComplexJsonPatchFile patchFile)
    {
        Type memberType = ComplexTypeAccessor.GetMemberType(controller.GetType(), patchFile.Target.MemberName)
            ?? throw new InvalidOperationException($"Could not determine member type for '{patchFile.Target.MemberName}'.");

        if (!ComplexTypeAccessor.TryGetMemberValue(controller, patchFile.Target.MemberName, out object? existingValue) || existingValue is null)
        {
            object? newValue = ComplexJsonValuePatcher.ConvertJsonElementToValue(patchFile.RootElement, memberType, patchFile, "$", memberName: patchFile.Target.MemberName);
            ComplexTypeAccessor.SetMemberValue(controller, patchFile.Target.MemberName, newValue);
        }
        else
        {
            ComplexJsonValuePatcher.ApplyJsonObjectToExistingValue(patchFile.RootElement, existingValue, patchFile, "$");
        }

        return new ComplexPatchApplyResult
        {
            ModName = patchFile.ModName,
            RelativePath = patchFile.RelativePath,
            PatchTargetKind = ComplexPatchTargetKind.ObjectReplace,
            ReplacedCount = 1
        };
    }
}
