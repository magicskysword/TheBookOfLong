using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TheBookOfLong;

internal static class ComplexJsonValuePatcher
{
    internal static void ApplyJsonObjectToExistingValue(
        JsonElement element,
        object target,
        ComplexJsonPatchFile patchFile,
        string jsonPath)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected a JSON object for '{jsonPath}', but got '{element.ValueKind}'.");
        }

        Dictionary<string, ComplexPatchableMember> members = ComplexTypeAccessor.GetPatchableMembers(target.GetType());
        foreach (JsonProperty jsonProperty in element.EnumerateObject())
        {
            if (!members.TryGetValue(jsonProperty.Name, out ComplexPatchableMember? member))
            {
                continue;
            }

            object? currentValue = member.Getter(target);
            object? patchedValue = PatchOrConvertJsonElement(
                jsonProperty.Value,
                member.ValueType,
                currentValue,
                patchFile,
                $"{jsonPath}.{jsonProperty.Name}",
                member.Name);

            member.Setter(target, patchedValue);
        }
    }

    internal static object? ConvertJsonElementToValue(
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
                : ComplexTypeAccessor.CreateObjectInstance(effectiveType);
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

        if (ComplexTypeAccessor.TryResolveCollectionElementType(effectiveType, out Type? elementType))
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Expected a JSON array for '{jsonPath}', but got '{element.ValueKind}'.");
            }

            object listInstance = ComplexTypeAccessor.CreateListInstance(effectiveType, elementType!);
            int itemIndex = 0;
            foreach (JsonElement itemElement in element.EnumerateArray())
            {
                object? itemValue = ConvertJsonElementToValue(itemElement, elementType!, patchFile, $"{jsonPath}[{itemIndex}]", memberName: null);
                ComplexTypeAccessor.AddCollectionItem(listInstance, itemValue);
                itemIndex += 1;
            }

            return listInstance;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected a JSON object for '{jsonPath}', but got '{element.ValueKind}'.");
        }

        object instance = ComplexTypeAccessor.CreateObjectInstance(effectiveType);
        Dictionary<string, ComplexPatchableMember> members = ComplexTypeAccessor.GetPatchableMembers(effectiveType);
        foreach (JsonProperty jsonProperty in element.EnumerateObject())
        {
            if (!members.TryGetValue(jsonProperty.Name, out ComplexPatchableMember? member))
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

    private static object? PatchOrConvertJsonElement(
        JsonElement element,
        Type targetType,
        object? existingValue,
        ComplexJsonPatchFile patchFile,
        string jsonPath,
        string? memberName)
    {
        Type? nullableUnderlyingType = Nullable.GetUnderlyingType(targetType);
        Type effectiveType = nullableUnderlyingType ?? targetType;

        if (element.ValueKind == JsonValueKind.Null)
        {
            return ConvertJsonElementToValue(element, targetType, patchFile, jsonPath, memberName);
        }

        if (string.Equals(memberName, "plotID", StringComparison.Ordinal) && effectiveType == typeof(int))
        {
            return ResolvePlotIdValue(element, patchFile, jsonPath);
        }

        if (effectiveType == typeof(string)
            || effectiveType.IsEnum
            || effectiveType == typeof(bool)
            || effectiveType == typeof(byte)
            || effectiveType == typeof(sbyte)
            || effectiveType == typeof(short)
            || effectiveType == typeof(ushort)
            || effectiveType == typeof(int)
            || effectiveType == typeof(uint)
            || effectiveType == typeof(long)
            || effectiveType == typeof(ulong)
            || effectiveType == typeof(float)
            || effectiveType == typeof(double)
            || effectiveType == typeof(decimal)
            || effectiveType == typeof(char)
            || effectiveType == typeof(DateTime)
            || effectiveType == typeof(Guid))
        {
            return ConvertJsonElementToValue(element, targetType, patchFile, jsonPath, memberName);
        }

        if (ComplexTypeAccessor.TryResolveCollectionElementType(effectiveType, out Type? elementType))
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Expected a JSON array for '{jsonPath}', but got '{element.ValueKind}'.");
            }

            object collection = existingValue ?? ComplexTypeAccessor.CreateListInstance(effectiveType, elementType!);
            ApplyJsonArrayToExistingCollection(element, collection, elementType!, patchFile, jsonPath);
            return collection;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return ConvertJsonElementToValue(element, targetType, patchFile, jsonPath, memberName);
        }

        object instance = existingValue ?? ComplexTypeAccessor.CreateObjectInstance(effectiveType);
        ApplyJsonObjectToExistingValue(element, instance, patchFile, jsonPath);
        return instance;
    }

    private static void ApplyJsonArrayToExistingCollection(
        JsonElement element,
        object collection,
        Type elementType,
        ComplexJsonPatchFile patchFile,
        string jsonPath)
    {
        List<object?> existingItems = ComplexTypeAccessor.EnumerateCollection(collection);
        int patchCount = 0;

        foreach (JsonElement itemElement in element.EnumerateArray())
        {
            string itemPath = $"{jsonPath}[{patchCount}]";
            if (patchCount < existingItems.Count)
            {
                object? currentItem = existingItems[patchCount];
                object? patchedItem = PatchOrConvertJsonElement(
                    itemElement,
                    elementType,
                    currentItem,
                    patchFile,
                    itemPath,
                    memberName: null);

                if (!ReferenceEquals(currentItem, patchedItem) || currentItem is null)
                {
                    ComplexTypeAccessor.SetCollectionItem(collection, patchCount, patchedItem);
                }
            }
            else
            {
                object? newItem = PatchOrConvertJsonElement(
                    itemElement,
                    elementType,
                    existingValue: null,
                    patchFile,
                    itemPath,
                    memberName: null);

                ComplexTypeAccessor.AddCollectionItem(collection, newItem);
            }

            patchCount += 1;
        }

        for (int index = existingItems.Count - 1; index >= patchCount; index -= 1)
        {
            ComplexTypeAccessor.RemoveCollectionItemAt(collection, index);
        }
    }

    private static object ResolvePlotIdValue(JsonElement element, ComplexJsonPatchFile patchFile, string jsonPath)
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
