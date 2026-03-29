using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheBookOfLong;

internal static class ConfigDumpManager
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly List<DumpEntry> Entries = new();
    private static readonly HashSet<string> WrittenRelativePaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> ResourcePathsByInstanceId = new();
    private static readonly Dictionary<string, Queue<PendingSource>> PendingSourcesByHash = new(StringComparer.Ordinal);
    private static readonly HashSet<int> ConsumedPendingSourceIds = new();
    private static readonly Dictionary<string, string> SpecialExportExtensionsBySourcePath = new(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine("Data", "PoetryData")] = ".json"
    };

    private static string _gameRoot = string.Empty;
    private static string _dumpRoot = string.Empty;
    private static string _latestRoot = string.Empty;
    private static int _captureDepth;
    private static int _anonymousTableIndex;
    private static int _pendingSourceId;
    private static string _activeTrigger = string.Empty;
    private static DateTime _captureStartedUtc;
    internal static string DumpRoot => _dumpRoot;

    internal static void Initialize()
    {
        lock (Sync)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            EnsureInitialized();
        }
    }

    internal static void BeginCapture(string trigger)
    {
        lock (Sync)
        {
            if (!EnsureInitialized())
            {
                return;
            }

            if (_captureDepth == 0)
            {
                ResetLatestDump();
                _captureStartedUtc = DateTime.UtcNow;
                _activeTrigger = trigger;
                _anonymousTableIndex = 0;
                Entries.Clear();
                WrittenRelativePaths.Clear();
                ResourcePathsByInstanceId.Clear();
                PendingSourcesByHash.Clear();
                ConsumedPendingSourceIds.Clear();
                _pendingSourceId = 0;
            }

            _captureDepth += 1;
        }
    }

    internal static void EndCapture(string trigger)
    {
        lock (Sync)
        {
            if (_captureDepth == 0)
            {
                return;
            }

            _captureDepth -= 1;

            if (_captureDepth != 0)
            {
                return;
            }

            WriteManifest(trigger);
        }
    }

    internal static void CaptureLoadedResource(string? resourcePath, object? asset)
    {
        if (!IsCaptureActive() || string.IsNullOrWhiteSpace(resourcePath) || asset is not global::UnityEngine.Object unityObject)
        {
            return;
        }

        int instanceId;
        try
        {
            instanceId = unityObject.GetInstanceID();
        }
        catch
        {
            return;
        }

        lock (Sync)
        {
            ResourcePathsByInstanceId[instanceId] = NormalizeSourcePath(resourcePath);
        }
    }

    internal static void CaptureTextAssetRead(global::UnityEngine.TextAsset textAsset, string? fallbackText)
    {
        if (!IsCaptureActive())
        {
            return;
        }

        string sourcePath = ResolveTextAssetSourcePath(textAsset);
        string content = ResolveBestTextAssetContent(textAsset, fallbackText, out string encodingName);

        SaveNamedDump("TextAsset", sourcePath, content, encodingName);

        lock (Sync)
        {
            PendingSource pendingSource = new()
            {
                Id = ++_pendingSourceId,
                SourcePath = sourcePath,
                EncodingName = encodingName
            };

            EnqueuePendingSource(ComputeHash(content), pendingSource);

            if (!string.IsNullOrEmpty(fallbackText))
            {
                string fallbackHash = ComputeHash(fallbackText);
                string correctedHash = ComputeHash(content);
                if (!string.Equals(fallbackHash, correctedHash, StringComparison.Ordinal))
                {
                    EnqueuePendingSource(fallbackHash, pendingSource);
                }
            }
        }
    }

    internal static string ResolveBestTextAssetContent(global::UnityEngine.TextAsset textAsset, string? fallbackText, out string encodingName)
    {
        byte[]? rawBytes = TryGetTextAssetBytes(textAsset);
        return DecodeBest(rawBytes, fallbackText ?? string.Empty, out encodingName);
    }

    internal static void RegisterAdditionalTextAssetContent(global::UnityEngine.TextAsset textAsset, string? content)
    {
        if (!IsCaptureActive() || string.IsNullOrEmpty(content))
        {
            return;
        }

        string sourcePath = ResolveTextAssetSourcePath(textAsset);
        lock (Sync)
        {
            PendingSource pendingSource = new()
            {
                Id = ++_pendingSourceId,
                SourcePath = sourcePath,
                EncodingName = "patched-string"
            };

            EnqueuePendingSource(ComputeHash(content), pendingSource);
        }
    }

    internal static void CaptureLoaderInput(string loaderMethod, string? content)
    {
        if (!IsCaptureActive() || string.IsNullOrEmpty(content))
        {
            return;
        }

        lock (Sync)
        {
            string contentHash = ComputeHash(content);
            if (TryConsumePendingSource(contentHash, out PendingSource? pendingSource))
            {
                return;
            }

            _anonymousTableIndex += 1;

            string inferredName = InferAnonymousTableName(content, _anonymousTableIndex);

            string relativePath = Path.Combine(
                "_Anonymous",
                SanitizePathSegment(loaderMethod),
                inferredName + ".csv");

            SaveContent(relativePath, content, "LTCSVLoader", loaderMethod, contentHash, "unknown");
        }
    }

    internal static void CaptureLoaderFile(string loaderMethod, string? fileName)
    {
        if (!IsCaptureActive() || string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
        {
            return;
        }

        string content;
        try
        {
            Encoding gbk = Encoding.GetEncoding("GBK");
            content = File.ReadAllText(fileName, gbk);
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"Failed to read config file '{fileName}': {ex.Message}");
            return;
        }

        string relativeSourcePath = BuildSafeRelativePath(fileName);
        SaveNamedDump("ReadFile", relativeSourcePath, content, "GBK");
    }

    private static bool IsCaptureActive()
    {
        lock (Sync)
        {
            return _captureDepth > 0 && EnsureInitialized();
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
            MelonLoader.MelonLogger.Warning("Config dump path is unavailable. Could not resolve game root directory.");
            return false;
        }

        _gameRoot = Path.GetFullPath(gameRoot);
        _dumpRoot = Path.Combine(_gameRoot, "DataDump");
        _latestRoot = Path.Combine(_dumpRoot, "Latest");

        Directory.CreateDirectory(_dumpRoot);
        return true;
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

    private static void SaveNamedDump(string dumpType, string sourcePath, string content, string encodingName)
    {
        lock (Sync)
        {
            string normalizedSourcePath = NormalizeSourcePath(sourcePath);
            string relativePath = EnsureTextExtension(MapDumpRelativePath(normalizedSourcePath));
            string contentHash = ComputeHash(content);

            SaveContent(relativePath, content, dumpType, normalizedSourcePath, contentHash, encodingName);
        }
    }

    private static void SaveContent(string relativePath, string content, string dumpType, string sourcePath, string contentHash, string encodingName)
    {
        if (!WrittenRelativePaths.Add(relativePath))
        {
            return;
        }

        string fullPath = Path.Combine(_latestRoot, relativePath);
        string? directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(fullPath, content, Utf8NoBom);

        Entries.Add(new DumpEntry
        {
            DumpType = dumpType,
            SourcePath = sourcePath,
            RelativePath = relativePath.Replace('\\', '/'),
            ContentHash = contentHash,
            EncodingName = encodingName,
            LineCount = CountLines(content),
            CharacterCount = content.Length
        });
    }

    private static void ResetLatestDump()
    {
        if (Directory.Exists(_latestRoot))
        {
            Directory.Delete(_latestRoot, recursive: true);
        }

        Directory.CreateDirectory(_latestRoot);
    }

    private static void WriteManifest(string endTrigger)
    {
        DumpManifest manifest = new()
        {
            ActiveTrigger = _activeTrigger,
            EndTrigger = endTrigger,
            StartedUtc = _captureStartedUtc,
            FinishedUtc = DateTime.UtcNow,
            EntryCount = Entries.Count,
            Entries = new List<DumpEntry>(Entries)
        };

        string manifestPath = Path.Combine(_latestRoot, "manifest.json");
        string manifestJson = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(manifestPath, manifestJson, Utf8NoBom);
        MelonLoader.MelonLogger.Msg($"Config dump complete. Wrote {Entries.Count} table files to {_latestRoot}");
    }

    private static string NormalizeSourcePath(string sourcePath)
    {
        string normalized = sourcePath.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i += 1)
        {
            segments[i] = SanitizePathSegment(segments[i]);
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string ResolveTextAssetSourcePath(global::UnityEngine.TextAsset textAsset)
    {
        try
        {
            int instanceId = textAsset.GetInstanceID();
            lock (Sync)
            {
                if (ResourcePathsByInstanceId.TryGetValue(instanceId, out string? resourcePath) && !string.IsNullOrWhiteSpace(resourcePath))
                {
                    return resourcePath;
                }
            }
        }
        catch
        {
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(textAsset.name))
            {
                return SanitizePathSegment(textAsset.name);
            }
        }
        catch
        {
        }

        _anonymousTableIndex += 1;
        return $"TextAsset_{_anonymousTableIndex:D4}";
    }

    private static byte[]? TryGetTextAssetBytes(global::UnityEngine.TextAsset textAsset)
    {
        try
        {
            PropertyInfo? bytesProperty = typeof(global::UnityEngine.TextAsset).GetProperty("bytes", BindingFlags.Instance | BindingFlags.Public);
            object? rawValue = bytesProperty?.GetValue(textAsset);
            return ConvertToManagedBytes(rawValue);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ConvertToManagedBytes(object? rawValue)
    {
        switch (rawValue)
        {
            case null:
                return null;
            case byte[] managedBytes:
                return managedBytes;
            case Array array:
            {
                byte[] result = new byte[array.Length];
                for (int i = 0; i < array.Length; i += 1)
                {
                    result[i] = Convert.ToByte(array.GetValue(i));
                }

                return result;
            }
            case IEnumerable enumerable:
            {
                List<byte> bytes = new();
                foreach (object? item in enumerable)
                {
                    bytes.Add(Convert.ToByte(item));
                }

                return bytes.ToArray();
            }
            default:
                return null;
        }
    }

    private static string DecodeBest(byte[]? rawBytes, string fallbackText, out string encodingName)
    {
        if (rawBytes is null || rawBytes.Length == 0)
        {
            encodingName = "fallback-string";
            return fallbackText;
        }

        if (HasUtf8Bom(rawBytes))
        {
            encodingName = "UTF-8-BOM";
            return Utf8NoBom.GetString(rawBytes, 3, rawBytes.Length - 3);
        }

        if (TryDecodeUtf8(rawBytes, out string? utf8Text))
        {
            encodingName = "UTF-8";
            return utf8Text ?? fallbackText;
        }

        try
        {
            string gb18030Text = Encoding.GetEncoding("GB18030").GetString(rawBytes);
            encodingName = "GB18030";
            return gb18030Text;
        }
        catch
        {
        }

        try
        {
            string gbkText = Encoding.GetEncoding("GBK").GetString(rawBytes);
            encodingName = "GBK";
            return gbkText;
        }
        catch
        {
        }

        encodingName = "fallback-string";
        return fallbackText;
    }

    private static bool HasUtf8Bom(byte[] rawBytes)
    {
        return rawBytes.Length >= 3
            && rawBytes[0] == 0xEF
            && rawBytes[1] == 0xBB
            && rawBytes[2] == 0xBF;
    }

    private static bool TryDecodeUtf8(byte[] rawBytes, out string? text)
    {
        try
        {
            UTF8Encoding strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strictUtf8.GetString(rawBytes);
            return true;
        }
        catch
        {
            text = null;
            return false;
        }
    }

    private static bool TryConsumePendingSource(string contentHash, out PendingSource? pendingSource)
    {
        if (PendingSourcesByHash.TryGetValue(contentHash, out Queue<PendingSource>? queue))
        {
            while (queue.Count > 0)
            {
                PendingSource candidate = queue.Dequeue();
                if (ConsumedPendingSourceIds.Contains(candidate.Id))
                {
                    continue;
                }

                ConsumedPendingSourceIds.Add(candidate.Id);
                if (queue.Count == 0)
                {
                    PendingSourcesByHash.Remove(contentHash);
                }

                pendingSource = candidate;
                return true;
            }

            PendingSourcesByHash.Remove(contentHash);
        }

        pendingSource = null;
        return false;
    }

    private static void EnqueuePendingSource(string contentHash, PendingSource pendingSource)
    {
        if (!PendingSourcesByHash.TryGetValue(contentHash, out Queue<PendingSource>? queue))
        {
            queue = new Queue<PendingSource>();
            PendingSourcesByHash[contentHash] = queue;
        }

        queue.Enqueue(pendingSource);
    }

    private static string InferAnonymousTableName(string content, int tableIndex)
    {
        string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            string[] cells = trimmed.Split(',');
            List<string> parts = new();
            for (int i = 0; i < cells.Length && parts.Count < 3; i += 1)
            {
                string candidate = cells[i].Trim().Trim('"');
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                parts.Add(SanitizePathSegment(candidate));
            }

            if (parts.Count > 0)
            {
                string joined = string.Join("_", parts);
                if (!string.IsNullOrWhiteSpace(joined))
                {
                    return joined.Length > 64 ? joined.Substring(0, 64) : joined;
                }
            }
        }

        return $"Table_{tableIndex:D4}";
    }

    private static string EnsureTextExtension(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        if (!string.IsNullOrEmpty(extension))
        {
            return relativePath;
        }

        if (SpecialExportExtensionsBySourcePath.TryGetValue(relativePath, out string? preferredExtension))
        {
            return relativePath + preferredExtension;
        }

        return relativePath + ".csv";
    }

    private static string MapDumpRelativePath(string normalizedSourcePath)
    {
        string gameDataPrefix = "GameData" + Path.DirectorySeparatorChar;
        if (normalizedSourcePath.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine("Data", normalizedSourcePath.Substring(gameDataPrefix.Length));
        }

        if (string.Equals(normalizedSourcePath, "GameData", StringComparison.OrdinalIgnoreCase))
        {
            return "Data";
        }

        return normalizedSourcePath;
    }

    private static string BuildSafeRelativePath(string fullPath)
    {
        string normalizedFullPath = Path.GetFullPath(fullPath);
        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(_gameRoot, normalizedFullPath);
        }
        catch
        {
            relativePath = Path.GetFileName(normalizedFullPath);
        }

        return NormalizeSourcePath(relativePath);
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

    private static string ComputeHash(string content)
    {
        byte[] bytes = Utf8NoBom.GetBytes(content);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0)
        {
            return 0;
        }

        int count = 1;
        foreach (char ch in content)
        {
            if (ch == '\n')
            {
                count += 1;
            }
        }

        return count;
    }

    private sealed class DumpManifest
    {
        public string ActiveTrigger { get; set; } = string.Empty;

        public string EndTrigger { get; set; } = string.Empty;

        public DateTime StartedUtc { get; set; }

        public DateTime FinishedUtc { get; set; }

        public int EntryCount { get; set; }

        public List<DumpEntry> Entries { get; set; } = new();
    }

    private sealed class DumpEntry
    {
        public string DumpType { get; set; } = string.Empty;

        public string SourcePath { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public string EncodingName { get; set; } = string.Empty;

        public int LineCount { get; set; }

        public int CharacterCount { get; set; }
    }

    private sealed class PendingSource
    {
        public int Id { get; set; }

        public string SourcePath { get; set; } = string.Empty;

        public string EncodingName { get; set; } = string.Empty;
    }
}
