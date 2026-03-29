using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;

namespace TheBookOfLong;

internal static class GameComplexDataDumpManager
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly string[] MissionDataFieldNames =
    {
        "bountyMissionDataBase",
        "MainMissionDataBase",
        "BranchMissionDataBase",
        "LittleMissionDataBase",
        "TreasureMapMissionDataBase",
        "SpeKillerMissionDataBase"
    };

    private static string _gameRoot = string.Empty;
    private static string _dumpRoot = string.Empty;
    private static string _latestRoot = string.Empty;
    private static ExportState _exportState;

    internal static void Initialize()
    {
        lock (Sync)
        {
            EnsureInitialized();
        }
    }

    internal static void TryStartExport()
    {
        lock (Sync)
        {
            if (!EnsureInitialized() || _exportState != ExportState.NotStarted)
            {
                return;
            }

            _exportState = ExportState.WaitingForSceneData;
        }

        MelonLoader.MelonCoroutines.Start(WaitAndExportComplexData());
    }

    private static IEnumerator WaitAndExportComplexData()
    {
        while (true)
        {
            ExportState exportState;
            lock (Sync)
            {
                exportState = _exportState;
            }

            if (exportState is ExportState.Completed or ExportState.Failed)
            {
                yield break;
            }

            global::Il2Cpp.WorldPlotEventController? worldPlotEventController = global::Il2Cpp.WorldPlotEventController.Instance;
            global::Il2Cpp.MissionDataController? missionDataController = global::Il2Cpp.MissionDataController.Instance;

            if (GameComplexDataPatchManager.IsApplyCompleted
                && IsComplexDataReady(worldPlotEventController, missionDataController))
            {
                ExportComplexData(worldPlotEventController!, missionDataController!);
                yield break;
            }

            yield return null;
        }
    }

    private static bool IsComplexDataReady(
        global::Il2Cpp.WorldPlotEventController? worldPlotEventController,
        global::Il2Cpp.MissionDataController? missionDataController)
    {
        if (worldPlotEventController is null || missionDataController is null)
        {
            return false;
        }

        if (!TryGetMemberValue(worldPlotEventController, "WorldPlotEventDataBase", out object? worldPlotEventDataBase)
            || worldPlotEventDataBase is null)
        {
            return false;
        }

        for (int i = 0; i < MissionDataFieldNames.Length; i += 1)
        {
            if (!TryGetMemberValue(missionDataController, MissionDataFieldNames[i], out object? missionDataValue)
                || missionDataValue is null)
            {
                return false;
            }
        }

        return true;
    }

    private static void ExportComplexData(
        global::Il2Cpp.WorldPlotEventController worldPlotEventController,
        global::Il2Cpp.MissionDataController missionDataController)
    {
        lock (Sync)
        {
            if (_exportState != ExportState.WaitingForSceneData)
            {
                return;
            }

            _exportState = ExportState.Exporting;
        }

        try
        {
            string complexDataRoot;
            lock (Sync)
            {
                if (!EnsureInitialized())
                {
                    _exportState = ExportState.Failed;
                    return;
                }

                complexDataRoot = PrepareComplexDataRoot();
            }

            int exportedFileCount = 0;

            exportedFileCount += ExportControllerMember(
                complexDataRoot,
                worldPlotEventController,
                "WorldPlotEventController",
                "WorldPlotEventDataBase");

            foreach (string fieldName in MissionDataFieldNames)
            {
                exportedFileCount += ExportControllerMember(
                    complexDataRoot,
                    missionDataController,
                    "MissionDataController",
                    fieldName);
            }

            lock (Sync)
            {
                _exportState = ExportState.Completed;
            }

            MelonLoader.MelonLogger.Msg(
                $"Game complex data export complete. Wrote {exportedFileCount} files to {complexDataRoot}");
        }
        catch (Exception ex)
        {
            lock (Sync)
            {
                _exportState = ExportState.Failed;
            }

            MelonLoader.MelonLogger.Warning($"Failed to export game complex data: {ex}");
        }
    }

    private static int ExportControllerMember(string complexDataRoot, object controller, string controllerName, string memberName)
    {
        if (!TryGetMemberValue(controller, memberName, out object? memberValue))
        {
            MelonLoader.MelonLogger.Warning(
                $"Could not find '{controllerName}.{memberName}' while exporting game complex data.");
            return 0;
        }

        string fileName = $"{SanitizePathSegment(controllerName)}_{SanitizePathSegment(memberName)}.json";
        string filePath = Path.Combine(complexDataRoot, fileName);
        object? serializableValue = ToSerializableValue(
            memberValue,
            depth: 0,
            visited: new HashSet<object>(ReferenceComparer.Instance));

        string json = JsonSerializer.Serialize(serializableValue, JsonOptions);
        File.WriteAllText(filePath, json, Utf8NoBom);
        return 1;
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

    private static object? ToSerializableValue(object? value, int depth, HashSet<object> visited)
    {
        if (value is null)
        {
            return null;
        }

        if (depth > 32)
        {
            return "<MaxDepthReached>";
        }

        if (value is string text)
        {
            return text;
        }

        if (value is global::UnityEngine.Object unityObject)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = unityObject.name,
                ["type"] = unityObject.GetType().FullName
            };
        }

        Type type = value.GetType();
        if (TryConvertSimpleValue(value, type, out object? simpleValue))
        {
            return simpleValue;
        }

        bool trackReference = !type.IsValueType;
        if (trackReference && !visited.Add(value))
        {
            return "<CycleDetected>";
        }

        try
        {
            if (TryConvertEnumerable(value, depth, visited, out List<object?>? listValue))
            {
                return listValue;
            }

            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            bool hasSerializableMember = false;

            foreach (SerializableMember member in GetSerializableMembers(type))
            {
                hasSerializableMember = true;

                object? memberValue;
                try
                {
                    memberValue = member.Getter(value);
                }
                catch (Exception ex)
                {
                    memberValue = $"<ReadFailed: {ex.GetType().Name}>";
                }

                result[member.Name] = ToSerializableValue(memberValue, depth + 1, visited);
            }

            return hasSerializableMember ? result : value.ToString();
        }
        finally
        {
            if (trackReference)
            {
                visited.Remove(value);
            }
        }
    }

    private static bool TryConvertSimpleValue(object value, Type type, out object? simpleValue)
    {
        if (type.IsEnum)
        {
            Type underlyingType = Enum.GetUnderlyingType(type);
            object numericValue = underlyingType == typeof(byte)
                                  || underlyingType == typeof(ushort)
                                  || underlyingType == typeof(uint)
                                  || underlyingType == typeof(ulong)
                ? Convert.ToUInt64(value)
                : Convert.ToInt64(value);

            simpleValue = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = value.ToString(),
                ["value"] = numericValue
            };
            return true;
        }

        if (type == typeof(bool)
            || type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal)
            || type == typeof(char))
        {
            simpleValue = value;
            return true;
        }

        if (type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan)
            || type == typeof(Guid))
        {
            simpleValue = value.ToString();
            return true;
        }

        if (type == typeof(IntPtr) || type == typeof(UIntPtr))
        {
            simpleValue = value.ToString();
            return true;
        }

        string? fullName = type.FullName;
        if (fullName is "Il2CppSystem.String" or "Il2CppSystem.Char")
        {
            simpleValue = value.ToString();
            return true;
        }

        simpleValue = null;
        return false;
    }

    private static bool TryConvertEnumerable(object value, int depth, HashSet<object> visited, out List<object?>? listValue)
    {
        if (value is IEnumerable enumerable)
        {
            listValue = new List<object?>();
            foreach (object? item in enumerable)
            {
                listValue.Add(ToSerializableValue(item, depth + 1, visited));
            }

            return true;
        }

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? countProperty = value.GetType().GetProperty("Count", Flags);
        PropertyInfo? itemProperty = value.GetType().GetProperty("Item", Flags, null, null, new[] { typeof(int) }, null);
        if (countProperty is null || itemProperty is null || !countProperty.CanRead || !itemProperty.CanRead)
        {
            listValue = null;
            return false;
        }

        object? rawCount = countProperty.GetValue(value);
        int count = rawCount is null ? 0 : Convert.ToInt32(rawCount);

        listValue = new List<object?>(count);
        for (int i = 0; i < count; i += 1)
        {
            object? item = itemProperty.GetValue(value, new object[] { i });
            listValue.Add(ToSerializableValue(item, depth + 1, visited));
        }

        return true;
    }

    private static IEnumerable<SerializableMember> GetSerializableMembers(Type type)
    {
        HashSet<string> seenNames = new(StringComparer.Ordinal);

        for (Type? currentType = type;
             currentType is not null && currentType != typeof(object);
             currentType = currentType.BaseType)
        {
            string? namespaceName = currentType.Namespace;
            if (!string.IsNullOrEmpty(namespaceName)
                && namespaceName.StartsWith("Il2CppInterop.Runtime", StringComparison.Ordinal))
            {
                yield break;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;

            foreach (PropertyInfo property in currentType.GetProperties(Flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0 || !seenNames.Add(property.Name))
                {
                    continue;
                }

                yield return new SerializableMember(property.Name, target => property.GetValue(target));
            }

            foreach (FieldInfo field in currentType.GetFields(Flags))
            {
                if (!seenNames.Add(field.Name))
                {
                    continue;
                }

                yield return new SerializableMember(field.Name, target => field.GetValue(target));
            }
        }
    }

    private static bool EnsureInitialized()
    {
        if (!string.IsNullOrWhiteSpace(_latestRoot))
        {
            return true;
        }

        string? gameRoot = ResolveGameRoot();
        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            MelonLoader.MelonLogger.Warning("Game complex data dump path is unavailable. Could not resolve game root directory.");
            return false;
        }

        _gameRoot = Path.GetFullPath(gameRoot);
        _dumpRoot = Path.Combine(_gameRoot, "DataDump");
        _latestRoot = Path.Combine(_dumpRoot, "Latest");

        Directory.CreateDirectory(_dumpRoot);
        Directory.CreateDirectory(_latestRoot);
        return true;
    }

    private static string PrepareComplexDataRoot()
    {
        string complexDataRoot = Path.Combine(_latestRoot, "GameComplexData");
        if (Directory.Exists(complexDataRoot))
        {
            Directory.Delete(complexDataRoot, recursive: true);
        }

        Directory.CreateDirectory(complexDataRoot);
        return complexDataRoot;
    }

    private static string? ResolveGameRoot()
    {
        string? gameRoot = null;
        try
        {
            gameRoot = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            return gameRoot;
        }

        string? dataPath = null;
        try
        {
            dataPath = global::UnityEngine.Application.dataPath;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(dataPath))
        {
            DirectoryInfo? parent = Directory.GetParent(dataPath);
            if (parent is not null && !string.IsNullOrWhiteSpace(parent.FullName))
            {
                return parent.FullName;
            }
        }

        return null;
    }

    private static string SanitizePathSegment(string value)
    {
        if (value is "." or "..")
        {
            return "_";
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        StringBuilder builder = new(value.Length);

        foreach (char ch in value)
        {
            builder.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
        }

        return builder.ToString();
    }

    private enum ExportState
    {
        NotStarted,
        WaitingForSceneData,
        Exporting,
        Completed,
        Failed
    }

    private sealed class SerializableMember
    {
        internal SerializableMember(string name, Func<object, object?> getter)
        {
            Name = name;
            Getter = getter;
        }

        internal string Name { get; }

        internal Func<object, object?> Getter { get; }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
