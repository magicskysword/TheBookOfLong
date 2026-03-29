using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TheBookOfLong;

internal static class GameComplexDataPatchManager
{
    private const string PlotDataSourcePath = "GameData/PlotData.csv";

    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
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

    private static readonly List<ComplexJsonPatchFile> LoadedPatchFiles = new();

    private static string _modsOfLongRoot = string.Empty;
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

            global::Il2Cpp.WorldPlotEventController? worldPlotEventController = global::Il2Cpp.WorldPlotEventController.Instance;
            global::Il2Cpp.MissionDataController? missionDataController = global::Il2Cpp.MissionDataController.Instance;

            if (IsPatchTargetsReady(worldPlotEventController, missionDataController))
            {
                ApplyLoadedPatchFiles(worldPlotEventController!, missionDataController!);
                yield break;
            }

            yield return null;
        }
    }

    private static bool IsPatchTargetsReady(
        global::Il2Cpp.WorldPlotEventController? worldPlotEventController,
        global::Il2Cpp.MissionDataController? missionDataController)
    {
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
            object? newItem = ConvertJsonElementToValue(patchElement, elementType, patchFile, $"$[{patchIndex}]", memberName: null);

            if (indexByName.TryGetValue(patchName, out int existingIndex))
            {
                mergedItems[existingIndex] = newItem;
                modifiedCount += 1;
            }
            else
            {
                indexByName[patchName] = mergedItems.Count;
                mergedItems.Add(newItem);
                addedCount += 1;
            }

            patchIndex += 1;
        }

        object newList = CreateListInstance(listType, elementType);
        for (int i = 0; i < mergedItems.Count; i += 1)
        {
            AddCollectionItem(newList, mergedItems[i]);
        }

        SetMemberValue(controller, patchFile.Target.MemberName, newList);

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

        object? newValue = ConvertJsonElementToValue(patchFile.RootElement, memberType, patchFile, "$", memberName: patchFile.Target.MemberName);
        SetMemberValue(controller, patchFile.Target.MemberName, newValue);

        return new PatchApplyResult
        {
            ModName = patchFile.ModName,
            RelativePath = patchFile.RelativePath,
            PatchTargetKind = PatchTargetKind.ObjectReplace,
            ReplacedCount = 1
        };
    }

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

    private static object CreateObjectInstance(Type type)
    {
        if (type.IsValueType)
        {
            return Activator.CreateInstance(type)!;
        }

        ConstructorInfo? constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);

        if (constructor is not null)
        {
            return constructor.Invoke(Array.Empty<object>());
        }

        return RuntimeHelpers.GetUninitializedObject(type);
    }

    private static Dictionary<string, PatchableMember> GetPatchableMembers(Type type)
    {
        Dictionary<string, PatchableMember> members = new(StringComparer.OrdinalIgnoreCase);

        for (Type? currentType = type;
             currentType is not null && currentType != typeof(object);
             currentType = currentType.BaseType)
        {
            string? namespaceName = currentType.Namespace;
            if (!string.IsNullOrEmpty(namespaceName)
                && namespaceName.StartsWith("Il2CppInterop.Runtime", StringComparison.Ordinal))
            {
                break;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;

            foreach (PropertyInfo property in currentType.GetProperties(Flags))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0 || members.ContainsKey(property.Name))
                {
                    continue;
                }

                members[property.Name] = new PatchableMember(
                    property.Name,
                    property.PropertyType,
                    target => property.GetValue(target),
                    (target, value) => property.SetValue(target, value));
            }

            foreach (FieldInfo field in currentType.GetFields(Flags))
            {
                if (field.IsInitOnly || members.ContainsKey(field.Name))
                {
                    continue;
                }

                members[field.Name] = new PatchableMember(
                    field.Name,
                    field.FieldType,
                    target => field.GetValue(target),
                    (target, value) => field.SetValue(target, value));
            }
        }

        return members;
    }

    private static object CreateListInstance(Type listType, Type elementType)
    {
        Type concreteType = listType;
        if (listType.IsArray)
        {
            throw new InvalidOperationException($"Array type '{listType.FullName}' is not supported for complex data patches.");
        }

        if (listType.IsInterface || listType.IsAbstract)
        {
            concreteType = typeof(List<>).MakeGenericType(elementType);
        }

        return CreateObjectInstance(concreteType);
    }

    private static void AddCollectionItem(object collection, object? item)
    {
        MethodInfo? addMethod = null;
        foreach (MethodInfo candidate in collection.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (candidate.Name != "Add")
            {
                continue;
            }

            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length != 1)
            {
                continue;
            }

            if (item is null || parameters[0].ParameterType.IsInstanceOfType(item))
            {
                addMethod = candidate;
                break;
            }
        }

        if (addMethod is null)
        {
            throw new InvalidOperationException($"Collection type '{collection.GetType().FullName}' does not expose a usable Add method.");
        }

        addMethod.Invoke(collection, new[] { item });
    }

    private static List<object?> EnumerateCollection(object collection)
    {
        if (collection is not IEnumerable enumerable)
        {
            throw new InvalidOperationException($"Value '{collection.GetType().FullName}' is not enumerable.");
        }

        List<object?> items = new();
        foreach (object? item in enumerable)
        {
            items.Add(item);
        }

        return items;
    }

    private static Type? ResolveCollectionElementType(Type collectionType)
    {
        if (collectionType.IsArray)
        {
            return collectionType.GetElementType();
        }

        return TryResolveCollectionElementType(collectionType, out Type? elementType)
            ? elementType
            : null;
    }

    private static bool TryResolveCollectionElementType(Type collectionType, out Type? elementType)
    {
        elementType = null;

        if (collectionType == typeof(string))
        {
            return false;
        }

        if (collectionType.IsGenericType)
        {
            Type[] genericArguments = collectionType.GetGenericArguments();
            if (genericArguments.Length == 1)
            {
                elementType = genericArguments[0];
                return true;
            }
        }

        Type[] interfaces = collectionType.GetInterfaces();
        for (int i = 0; i < interfaces.Length; i += 1)
        {
            Type interfaceType = interfaces[i];
            if (!interfaceType.IsGenericType || interfaceType.GetGenericTypeDefinition() != typeof(IEnumerable<>))
            {
                continue;
            }

            elementType = interfaceType.GetGenericArguments()[0];
            return true;
        }

        return false;
    }

    private static string? GetNameValue(object target)
    {
        return TryGetMemberValue(target, "name", out object? value) ? value?.ToString() : null;
    }

    private static string GetRequiredStringProperty(JsonElement element, string propertyName, string filePath, string jsonPath)
    {
        if (element.TryGetProperty(propertyName, out JsonElement propertyValue) && propertyValue.ValueKind == JsonValueKind.String)
        {
            string text = propertyValue.GetString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException($"Patch file '{filePath}' is missing a non-empty string property '{propertyName}' at '{jsonPath}'.");
    }

    private static Type? GetMemberType(Type targetType, string memberName)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? property = targetType.GetProperty(memberName, Flags);
        if (property is not null)
        {
            return property.PropertyType;
        }

        FieldInfo? field = targetType.GetField(memberName, Flags);
        return field?.FieldType;
    }

    private static bool TryGetMemberValue(object target, string memberName, out object? value)
    {
        Type type = target.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? property = type.GetProperty(memberName, Flags);
        if (property is not null && property.GetIndexParameters().Length == 0 && property.CanRead)
        {
            value = property.GetValue(target);
            return true;
        }

        FieldInfo? field = type.GetField(memberName, Flags);
        if (field is not null)
        {
            value = field.GetValue(target);
            return true;
        }

        value = null;
        return false;
    }

    private static void SetMemberValue(object target, string memberName, object? value)
    {
        Type type = target.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo? property = type.GetProperty(memberName, Flags);
        if (property is not null && property.GetIndexParameters().Length == 0 && property.CanWrite)
        {
            property.SetValue(target, value);
            return;
        }

        FieldInfo? field = type.GetField(memberName, Flags);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        throw new InvalidOperationException($"Could not set member '{memberName}' on '{type.FullName}'.");
    }

    private static bool EnsureInitialized()
    {
        if (!string.IsNullOrWhiteSpace(_modsOfLongRoot))
        {
            return true;
        }

        string? modsRoot = null;
        try
        {
            modsRoot = MelonLoader.Utils.MelonEnvironment.ModsDirectory;
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(modsRoot))
        {
            try
            {
                string gameRoot = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
                if (!string.IsNullOrWhiteSpace(gameRoot))
                {
                    modsRoot = Path.Combine(gameRoot, "Mods");
                }
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(modsRoot))
        {
            MelonLoader.MelonLogger.Warning("Game complex data mods path is unavailable. Could not resolve Mods directory.");
            return false;
        }

        _modsOfLongRoot = Path.Combine(Path.GetFullPath(modsRoot), "ModsOfLong");
        Directory.CreateDirectory(_modsOfLongRoot);
        return true;
    }

    private static void LoadPatchFiles()
    {
        LoadedPatchFiles.Clear();

        string[] modDirectories = Directory.GetDirectories(_modsOfLongRoot, "mod*", SearchOption.TopDirectoryOnly);
        Array.Sort(modDirectories, StringComparer.OrdinalIgnoreCase);

        int loadOrder = 0;
        for (int modIndex = 0; modIndex < modDirectories.Length; modIndex += 1)
        {
            string modDirectory = modDirectories[modIndex];
            string modName = ResolveDataModDisplayName(modDirectory);
            string dataDirectory = Path.Combine(modDirectory, "Data");
            if (!Directory.Exists(dataDirectory))
            {
                continue;
            }

            string[] patchFiles = Directory.GetFiles(dataDirectory, "*.json", SearchOption.AllDirectories);
            Array.Sort(patchFiles, StringComparer.OrdinalIgnoreCase);

            for (int patchIndex = 0; patchIndex < patchFiles.Length; patchIndex += 1)
            {
                string patchFilePath = patchFiles[patchIndex];
                if (TryLoadPatchFile(modName, dataDirectory, patchFilePath, ++loadOrder, out ComplexJsonPatchFile? patchFile))
                {
                    LoadedPatchFiles.Add(patchFile!);
                }
            }
        }

        if (LoadedPatchFiles.Count > 0)
        {
            MelonLoader.MelonLogger.Msg(
                $"Game complex data patches ready: '{_modsOfLongRoot}'. Loaded {LoadedPatchFiles.Count} JSON patch file(s).");
        }
    }

    private static bool TryLoadPatchFile(
        string modName,
        string dataDirectory,
        string patchFilePath,
        int loadOrder,
        out ComplexJsonPatchFile? patchFile)
    {
        patchFile = null;

        string relativePath = NormalizeLookupKey(Path.GetRelativePath(dataDirectory, patchFilePath));
        string canonicalRelativePath = BuildCanonicalComplexDataPath(relativePath);
        string fileName = Path.GetFileName(canonicalRelativePath);

        if (!TargetDefinitionsByFileName.TryGetValue(fileName, out PatchTargetDefinition? targetDefinition))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(patchFilePath, Utf8NoBom);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement rootElement = document.RootElement.Clone();

            if (targetDefinition.PatchTargetKind == PatchTargetKind.ArrayByName && rootElement.ValueKind != JsonValueKind.Array)
            {
                MelonLoader.MelonLogger.Warning($"Skipped complex data patch '{patchFilePath}' because the root JSON value is not an array.");
                return false;
            }

            if (targetDefinition.PatchTargetKind == PatchTargetKind.ObjectReplace && rootElement.ValueKind != JsonValueKind.Object)
            {
                MelonLoader.MelonLogger.Warning($"Skipped complex data patch '{patchFilePath}' because the root JSON value is not an object.");
                return false;
            }

            patchFile = new ComplexJsonPatchFile
            {
                ModName = modName,
                FullPath = patchFilePath,
                RelativePath = canonicalRelativePath,
                LoadOrder = loadOrder,
                RootElement = rootElement,
                Target = targetDefinition
            };

            RegisterSymbolicPlotIdReferences(patchFile);
            return true;
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"Failed to load complex data patch file '{patchFilePath}': {ex.Message}");
            return false;
        }
    }

    private static void RegisterSymbolicPlotIdReferences(ComplexJsonPatchFile patchFile)
    {
        RegisterSymbolicPlotIdReferencesRecursive(patchFile.RootElement, patchFile, "$");
    }

    private static void RegisterSymbolicPlotIdReferencesRecursive(JsonElement element, ComplexJsonPatchFile patchFile, string jsonPath)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string childPath = $"{jsonPath}.{property.Name}";
                    if (string.Equals(property.Name, "plotID", StringComparison.Ordinal)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        string rawValue = property.Value.GetString()?.Trim() ?? string.Empty;
                        if (rawValue.Length > 3 && rawValue.StartsWith("mod", StringComparison.OrdinalIgnoreCase))
                        {
                            DataModManager.RegisterExternalSymbolicReference(
                                PlotDataSourcePath,
                                rawValue,
                                patchFile.ModName,
                                patchFile.RelativePath,
                                childPath);
                        }
                    }

                    RegisterSymbolicPlotIdReferencesRecursive(property.Value, patchFile, childPath);
                }

                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement childElement in element.EnumerateArray())
                {
                    RegisterSymbolicPlotIdReferencesRecursive(childElement, patchFile, $"{jsonPath}[{index}]");
                    index += 1;
                }

                break;
        }
    }

    private static string ResolveDataModDisplayName(string modDirectory)
    {
        string folderName = Path.GetFileName(modDirectory);
        string infoFilePath = Path.Combine(modDirectory, "Info.json");
        if (File.Exists(infoFilePath))
        {
            try
            {
                string json = File.ReadAllText(infoFilePath, Utf8NoBom);
                DataModInfoFile? info = JsonSerializer.Deserialize<DataModInfoFile>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (!string.IsNullOrWhiteSpace(info?.Name))
                {
                    return info.Name.Trim();
                }
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"Failed to read complex data mod info '{infoFilePath}': {ex.Message}");
            }
        }

        string fallbackName = folderName.StartsWith("mod", StringComparison.OrdinalIgnoreCase)
            ? folderName.Substring(3).TrimStart(' ', '_', '-')
            : folderName;

        return string.IsNullOrWhiteSpace(fallbackName) ? folderName : fallbackName;
    }

    private static string BuildCanonicalComplexDataPath(string path)
    {
        string normalizedPath = NormalizeLookupKey(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return string.Empty;
        }

        string withExtension = string.IsNullOrEmpty(Path.GetExtension(normalizedPath))
            ? normalizedPath + ".json"
            : normalizedPath;

        string prefix = "GameComplexData" + Path.DirectorySeparatorChar;
        if (!withExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            withExtension = Path.Combine("GameComplexData", withExtension);
        }

        return NormalizeLookupKey(withExtension);
    }

    private static string NormalizeLookupKey(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Path.DirectorySeparatorChar, segments);
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
