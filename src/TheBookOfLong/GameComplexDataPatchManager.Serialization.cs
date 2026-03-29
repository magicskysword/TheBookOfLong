using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TheBookOfLong;

internal static partial class GameComplexDataPatchManager
{
    /// <summary>
    /// 把 JSON 递归转换为目标 IL2CPP 对象图。
    /// 这里不是通用序列化器，而是面向补丁场景的“按成员名定向赋值”，因此会额外处理 plotID 这样的特殊字段。
    /// </summary>
    private static object? ConvertJsonElementToValue(
        JsonElement element,
        Type targetType,
        ComplexJsonPatchFile patchFile,
        string jsonPath,
        string? memberName)
    {
        Type? nullableUnderlyingType = Nullable.GetUnderlyingType(targetType);
        Type effectiveType = nullableUnderlyingType ?? targetType;

        if (element.ValueKind == JsonValueKind.Null)
        {
            return nullableUnderlyingType is not null || !effectiveType.IsValueType
                ? null
                : CreateObjectInstance(effectiveType);
        }

        if (string.Equals(memberName, "plotID", StringComparison.Ordinal) && effectiveType == typeof(int))
        {
            return ResolvePlotIdValue(element, patchFile, jsonPath);
        }

        if (effectiveType == typeof(string))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
        }

        if (effectiveType.IsEnum)
        {
            return ReadEnumValue(element, effectiveType);
        }

        if (effectiveType == typeof(bool))
        {
            return element.GetBoolean();
        }

        if (effectiveType == typeof(byte)
            || effectiveType == typeof(sbyte)
            || effectiveType == typeof(short)
            || effectiveType == typeof(ushort)
            || effectiveType == typeof(int)
            || effectiveType == typeof(uint)
            || effectiveType == typeof(long)
            || effectiveType == typeof(ulong)
            || effectiveType == typeof(float)
            || effectiveType == typeof(double)
            || effectiveType == typeof(decimal))
        {
            return ReadNumericValue(element, effectiveType, jsonPath);
        }

        if (effectiveType == typeof(char))
        {
            string text = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
            if (text.Length != 1)
            {
                throw new InvalidOperationException($"Expected a single character at '{jsonPath}', but got '{text}'.");
            }

            return text[0];
        }

        if (effectiveType == typeof(DateTime))
        {
            return element.ValueKind == JsonValueKind.String
                ? DateTime.Parse(element.GetString() ?? string.Empty, System.Globalization.CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"Expected a string DateTime at '{jsonPath}'.");
        }

        if (effectiveType == typeof(Guid))
        {
            return element.ValueKind == JsonValueKind.String
                ? Guid.Parse(element.GetString() ?? string.Empty)
                : throw new InvalidOperationException($"Expected a string Guid at '{jsonPath}'.");
        }

        if (TryResolveCollectionElementType(effectiveType, out Type? elementType))
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Expected a JSON array for '{jsonPath}', but got '{element.ValueKind}'.");
            }

            object listInstance = CreateListInstance(effectiveType, elementType!);
            int itemIndex = 0;
            foreach (JsonElement itemElement in element.EnumerateArray())
            {
                object? itemValue = ConvertJsonElementToValue(itemElement, elementType!, patchFile, $"{jsonPath}[{itemIndex}]", memberName: null);
                AddCollectionItem(listInstance, itemValue);
                itemIndex += 1;
            }

            return listInstance;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected a JSON object for '{jsonPath}', but got '{element.ValueKind}'.");
        }

        object instance = CreateObjectInstance(effectiveType);
        Dictionary<string, PatchableMember> members = GetPatchableMembers(effectiveType);
        foreach (JsonProperty jsonProperty in element.EnumerateObject())
        {
            if (!members.TryGetValue(jsonProperty.Name, out PatchableMember? member))
            {
                continue;
            }

            object? memberValue = ConvertJsonElementToValue(
                jsonProperty.Value,
                member.ValueType,
                patchFile,
                $"{jsonPath}.{jsonProperty.Name}",
                member.Name);

            member.Setter(instance, memberValue);
        }

        return instance;
    }

    /// <summary>
    /// JSON 补丁里的 plotID 允许直接写 modXXX。
    /// 这里复用 CSV 补丁已经分配好的符号 ID，保证两套补丁对 PlotData 的引用落到同一组实际 ID。
    /// </summary>
    private static object ResolvePlotIdValue(JsonElement element, ComplexJsonPatchFile patchFile, string jsonPath)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string rawValue = element.GetString()?.Trim() ?? string.Empty;
            if (int.TryParse(rawValue, out int numericId))
            {
                return numericId;
            }

            if (DataModManager.TryResolveSymbolicIdForSource(PlotDataSourcePath, rawValue, out int assignedId))
            {
                return assignedId;
            }

            throw new InvalidOperationException(
                $"Could not resolve symbolic plotID '{rawValue}' referenced by '{patchFile.FullPath}' at '{jsonPath}'.");
        }

        return ReadNumericValue(element, typeof(int), jsonPath);
    }

    private static object ReadNumericValue(JsonElement element, Type targetType, string jsonPath)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string stringValue = element.GetString()?.Trim() ?? string.Empty;
            return ConvertStringToNumericValue(stringValue, targetType, jsonPath);
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException($"Expected a numeric value at '{jsonPath}', but got '{element.ValueKind}'.");
        }

        return targetType == typeof(byte) ? element.GetByte()
            : targetType == typeof(sbyte) ? element.GetSByte()
            : targetType == typeof(short) ? element.GetInt16()
            : targetType == typeof(ushort) ? element.GetUInt16()
            : targetType == typeof(int) ? element.GetInt32()
            : targetType == typeof(uint) ? element.GetUInt32()
            : targetType == typeof(long) ? element.GetInt64()
            : targetType == typeof(ulong) ? element.GetUInt64()
            : targetType == typeof(float) ? element.GetSingle()
            : targetType == typeof(double) ? element.GetDouble()
            : targetType == typeof(decimal) ? element.GetDecimal()
            : throw new InvalidOperationException($"Unsupported numeric target type '{targetType.FullName}' at '{jsonPath}'.");
    }

    private static object ConvertStringToNumericValue(string value, Type targetType, string jsonPath)
    {
        try
        {
            return targetType == typeof(byte) ? byte.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(sbyte) ? sbyte.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(short) ? short.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(ushort) ? ushort.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(int) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(uint) ? uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(long) ? long.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(ulong) ? ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(float) ? float.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(double) ? double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : targetType == typeof(decimal) ? decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"Unsupported numeric target type '{targetType.FullName}' at '{jsonPath}'.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse numeric value '{value}' at '{jsonPath}' as '{targetType.Name}': {ex.Message}", ex);
        }
    }

    private static object ReadEnumValue(JsonElement element, Type enumType)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return Enum.Parse(enumType, element.GetString() ?? string.Empty, ignoreCase: true);
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            Type underlyingType = Enum.GetUnderlyingType(enumType);
            object numericValue = ReadNumericValue(element, underlyingType, "$enum");
            return Enum.ToObject(enumType, numericValue);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                string enumName = nameElement.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(enumName))
                {
                    return Enum.Parse(enumType, enumName, ignoreCase: true);
                }
            }

            if (element.TryGetProperty("value", out JsonElement valueElement))
            {
                Type underlyingType = Enum.GetUnderlyingType(enumType);
                object numericValue = ReadNumericValue(valueElement, underlyingType, "$enum.value");
                return Enum.ToObject(enumType, numericValue);
            }
        }

        throw new InvalidOperationException($"Could not parse enum '{enumType.FullName}' from '{element.ValueKind}'.");
    }
}
