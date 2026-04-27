using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TheBookOfLong;

/// <summary>
/// 统一登记 ComplexData 里哪些字段需要接入 modXXX 符号 ID。
/// 这里故意只按“字段名 + 统一分隔符列表”做处理，不再按函数名判定，便于后续集中维护与扩展。
/// </summary>
internal static class ComplexSymbolicFieldRules
{
    private static readonly Dictionary<string, ComplexSymbolicFieldRule> RulesByMemberName = new(StringComparer.Ordinal)
    {
        // 直接引用 PlotData 的数值 ID 字段。
        ["plotID"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DirectIntId, "json-complex-plotID"),
        ["missionTargetFinishCallPlotID"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DirectIntId, "json-complex-missionTargetFinishCallPlotID"),

        // 这些字符串字段里可能包含完整函数、函数参数或复合目标字符串。
        // 当前规则是：只要被常见分隔符拆出来的片段是 modXXX，就替换成最终数字 ID。
        ["plotCallFuc"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-plotCallFuc"),
        ["eventOutTimeCallFuc"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-eventOutTimeCallFuc"),
        ["clickCallFuc"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-clickCallFuc"),
        ["callParam"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-callParam"),
        ["startCallSpeFuc"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-startCallSpeFuc"),
        ["outtimeCallSpeFuc"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-outtimeCallSpeFuc"),
        ["needTarget"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-needTarget"),
        ["triggerTargetID"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-triggerTargetID"),
        ["tirggerTargetID"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-tirggerTargetID"),
        ["missionTargetID"] = new ComplexSymbolicFieldRule(ComplexSymbolicFieldKind.DelimitedString, "json-complex-missionTargetID")
    };

    internal static void RegisterReferencesForJsonProperty(JsonProperty property, ComplexJsonPatchFile patchFile, string jsonPath)
    {
        if (!RulesByMemberName.TryGetValue(property.Name, out ComplexSymbolicFieldRule? rule))
        {
            return;
        }

        if (rule.Kind == ComplexSymbolicFieldKind.DirectIntId)
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string rawValue = property.Value.GetString()?.Trim() ?? string.Empty;
            if (!SymbolicIdService.TryGetSymbolicId(rawValue, out string symbolicId))
            {
                return;
            }

            RegisterReference(symbolicId, patchFile, jsonPath, rule.ReferenceType);
            return;
        }

        if (property.Value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        DelimitedSymbolicIdRewriter.Visit(
            property.Value.GetString() ?? string.Empty,
            symbolicId => RegisterReference(symbolicId, patchFile, jsonPath, rule.ReferenceType));
    }

    internal static bool TryConvertValue(
        JsonElement element,
        Type effectiveType,
        string? memberName,
        ComplexJsonPatchFile patchFile,
        string jsonPath,
        out object? convertedValue)
    {
        convertedValue = null;
        if (string.IsNullOrWhiteSpace(memberName)
            || !RulesByMemberName.TryGetValue(memberName, out ComplexSymbolicFieldRule? rule))
        {
            return false;
        }

        if (rule.Kind == ComplexSymbolicFieldKind.DirectIntId)
        {
            if (effectiveType != typeof(int))
            {
                return false;
            }

            convertedValue = ResolveDirectIntIdValue(element, patchFile, jsonPath, memberName);
            return true;
        }

        if (effectiveType != typeof(string))
        {
            return false;
        }

        string rawValue = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
        convertedValue = DelimitedSymbolicIdRewriter.Rewrite(
            rawValue,
            symbolicId => ResolveDelimitedSymbolicId(symbolicId, patchFile, jsonPath, memberName));
        return true;
    }

    private static object ResolveDirectIntIdValue(JsonElement element, ComplexJsonPatchFile patchFile, string jsonPath, string memberName)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string rawValue = element.GetString()?.Trim() ?? string.Empty;
            if (int.TryParse(rawValue, out int numericId))
            {
                return numericId;
            }

            if (SymbolicIdService.TryResolveIdForSource(GameComplexDataPatchManager.PlotDataSourcePath, rawValue, out int assignedId))
            {
                return assignedId;
            }

            throw new InvalidOperationException(
                $"Could not resolve symbolic ID '{rawValue}' for '{memberName}' referenced by '{patchFile.FullPath}' at '{jsonPath}'.");
        }

        return ComplexJsonValuePatcher.ReadNumericValue(element, typeof(int), jsonPath);
    }

    private static string? ResolveDelimitedSymbolicId(string symbolicId, ComplexJsonPatchFile patchFile, string jsonPath, string memberName)
    {
        if (!SymbolicIdService.TryResolveIdForSource(GameComplexDataPatchManager.PlotDataSourcePath, symbolicId, out int assignedId))
        {
            throw new InvalidOperationException(
                $"Could not resolve symbolic ID '{symbolicId}' for '{memberName}' referenced by '{patchFile.FullPath}' at '{jsonPath}'.");
        }

        return assignedId.ToString();
    }

    private static void RegisterReference(string symbolicId, ComplexJsonPatchFile patchFile, string jsonPath, string referenceType)
    {
        SymbolicIdService.RegisterExternalReference(
            GameComplexDataPatchManager.PlotDataSourcePath,
            symbolicId,
            patchFile.ModName,
            patchFile.RelativePath,
            jsonPath,
            referenceType);
    }

    private enum ComplexSymbolicFieldKind
    {
        DirectIntId,
        DelimitedString
    }

    private sealed class ComplexSymbolicFieldRule
    {
        public ComplexSymbolicFieldRule(ComplexSymbolicFieldKind kind, string referenceType)
        {
            Kind = kind;
            ReferenceType = referenceType;
        }

        public ComplexSymbolicFieldKind Kind { get; }

        public string ReferenceType { get; }
    }
}
