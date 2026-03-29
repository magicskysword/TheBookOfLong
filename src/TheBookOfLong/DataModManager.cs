using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MelonLoader;

namespace TheBookOfLong;

internal static partial class DataModManager
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Dictionary<int, string> ResourcePathsByInstanceId = new();
    private static readonly Dictionary<string, List<CsvPatchFile>> CsvPatchesByLookupKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PatchedContentCache = new(StringComparer.Ordinal);
    private static readonly List<DataModDefinition> LoadedMods = new();
    private static readonly Dictionary<string, SymbolicSourceInfo> SymbolicSourcesBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SymbolicIdGroup> SymbolicGroupsBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> ExternalSymbolicIdsBySourcePath = new(StringComparer.OrdinalIgnoreCase);

    private static string _gameRoot = string.Empty;
    private static string _modsRoot = string.Empty;
    private static string _modsOfLongRoot = string.Empty;
    private static int _patchFileOrder;

    internal static string ModsOfLongRoot => _modsOfLongRoot;

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (!EnsureInitialized())
            {
                return;
            }

            ReloadDataMods();
        }
    }

    internal static void RegisterExternalSymbolicReference(
        string sourcePath,
        string symbolicValue,
        string modName,
        string filePath,
        string location)
    {
        if (!TryGetSymbolicId(symbolicValue, out string symbolicId))
        {
            return;
        }

        string canonicalSourcePath = BuildCanonicalSourcePath(sourcePath);
        lock (Sync)
        {
            if (!ExternalSymbolicIdsBySourcePath.TryGetValue(canonicalSourcePath, out HashSet<string>? symbolicIds))
            {
                symbolicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ExternalSymbolicIdsBySourcePath[canonicalSourcePath] = symbolicIds;
            }

            symbolicIds.Add(symbolicId);
        }

        SymbolicFieldManager.RegisterReference(
            canonicalSourcePath,
            symbolicId,
            modName,
            filePath,
            location,
            "json-plotID");
    }

    internal static bool TryResolveSymbolicIdForSource(string sourcePath, string symbolicValue, out int assignedId)
    {
        assignedId = 0;
        if (!TryGetSymbolicId(symbolicValue, out string symbolicId))
        {
            return false;
        }

        string canonicalSourcePath = BuildCanonicalSourcePath(sourcePath);
        lock (Sync)
        {
            return SymbolicGroupsBySourcePath.TryGetValue(canonicalSourcePath, out SymbolicIdGroup? group)
                   && group.AssignedIds.TryGetValue(symbolicId, out assignedId);
        }
    }

    internal static void CaptureLoadedResource(string? resourcePath, object? asset)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) || asset is not global::UnityEngine.Object unityObject)
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
            ResourcePathsByInstanceId[instanceId] = NormalizeLookupKey(resourcePath);
        }
    }

    internal static void TryApplyTextPatch(global::UnityEngine.TextAsset textAsset, ref string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (Sync)
        {
            if (!EnsureInitialized())
            {
                return;
            }

            string sourcePath = ResolveTextAssetSourcePath(textAsset);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            List<CsvPatchFile> matchingPatches = GetMatchingCsvPatches(sourcePath);
            if (matchingPatches.Count == 0 || !LooksLikeCsvText(text))
            {
                return;
            }

            string cacheKey = sourcePath + "\n" + ComputeHash(text);
            if (PatchedContentCache.TryGetValue(cacheKey, out string? cachedContent))
            {
                text = cachedContent;
                return;
            }

            string currentContent = text;
            List<FilePatchResult> filePatchResults = new();

            foreach (CsvPatchFile patchFile in matchingPatches)
            {
                if (!TryMergeCsvPatch(
                    currentContent,
                    patchFile,
                    sourcePath,
                    out string mergedContent,
                    out int addedRowCount,
                    out int modifiedRowCount,
                    out string? warning))
                {
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        MelonLogger.Warning(warning);
                    }

                    continue;
                }

                if (string.Equals(currentContent, mergedContent, StringComparison.Ordinal))
                {
                    continue;
                }

                currentContent = mergedContent;
                filePatchResults.Add(new FilePatchResult
                {
                    ModName = patchFile.ModName,
                    RelativePath = patchFile.RelativePath,
                    AddedRowCount = addedRowCount,
                    ModifiedRowCount = modifiedRowCount
                });
            }

            if (!TryFinalizePatchedCsv(currentContent, sourcePath, out string finalizedContent, out List<string> finalizationWarnings))
            {
                return;
            }

            for (int i = 0; i < finalizationWarnings.Count; i += 1)
            {
                MelonLogger.Warning(finalizationWarnings[i]);
            }

            bool contentChanged = !string.Equals(text, finalizedContent, StringComparison.Ordinal);
            if (filePatchResults.Count == 0 && !contentChanged)
            {
                return;
            }

            PatchedContentCache[cacheKey] = finalizedContent;
            text = finalizedContent;

            Dictionary<string, List<FilePatchResult>> resultsByMod = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < filePatchResults.Count; i += 1)
            {
                FilePatchResult filePatchResult = filePatchResults[i];
                if (!resultsByMod.TryGetValue(filePatchResult.ModName, out List<FilePatchResult>? modResults))
                {
                    modResults = new List<FilePatchResult>();
                    resultsByMod[filePatchResult.ModName] = modResults;
                }

                modResults.Add(filePatchResult);
            }

            foreach ((string modName, List<FilePatchResult> modResults) in resultsByMod)
            {
                for (int i = 0; i < modResults.Count; i += 1)
                {
                    FilePatchResult modResult = modResults[i];
                    MelonLogger.Msg(
                        $"Data mod '{modName}' patched '{modResult.RelativePath}': added {modResult.AddedRowCount}, modified {modResult.ModifiedRowCount}");
                }
            }
        }
    }

    private static bool EnsureInitialized()
    {
        if (!string.IsNullOrWhiteSpace(_modsOfLongRoot))
        {
            return true;
        }

        string? modsRoot = ResolveModsRoot();
        if (string.IsNullOrWhiteSpace(modsRoot))
        {
            MelonLogger.Warning("Data mods path is unavailable. Could not resolve Mods directory.");
            return false;
        }

        _modsRoot = Path.GetFullPath(modsRoot);
        _gameRoot = ResolveGameRoot() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_gameRoot))
        {
            DirectoryInfo? modsParent = Directory.GetParent(_modsRoot);
            _gameRoot = modsParent?.FullName ?? string.Empty;
        }

        _modsOfLongRoot = Path.Combine(_modsRoot, "ModsOfLong");
        Directory.CreateDirectory(_modsOfLongRoot);
        return true;
    }

    private static string? ResolveModsRoot()
    {
        try
        {
            string modsDirectory = MelonLoader.Utils.MelonEnvironment.ModsDirectory;
            if (!string.IsNullOrWhiteSpace(modsDirectory))
            {
                return modsDirectory;
            }
        }
        catch
        {
        }

        try
        {
            string gameRoot = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
            if (!string.IsNullOrWhiteSpace(gameRoot))
            {
                return Path.Combine(gameRoot, "Mods");
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? ResolveGameRoot()
    {
        try
        {
            string gameRoot = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
            if (!string.IsNullOrWhiteSpace(gameRoot))
            {
                return Path.GetFullPath(gameRoot);
            }
        }
        catch
        {
        }

        return null;
    }

    private static void ReloadDataMods()
    {
        LoadedMods.Clear();
        CsvPatchesByLookupKey.Clear();
        PatchedContentCache.Clear();
        SymbolicSourcesBySourcePath.Clear();
        SymbolicGroupsBySourcePath.Clear();
        _patchFileOrder = 0;

        string[] modDirectories = Directory.GetDirectories(_modsOfLongRoot, "mod*", SearchOption.TopDirectoryOnly);
        Array.Sort(modDirectories, StringComparer.OrdinalIgnoreCase);

        List<CsvPatchFile> csvPatchFiles = new();
        int totalPatchFiles = 0;
        foreach (string modDirectory in modDirectories)
        {
            DataModDefinition dataMod = LoadDataMod(modDirectory, csvPatchFiles);
            LoadedMods.Add(dataMod);
            totalPatchFiles += dataMod.PatchFileCount;
        }

        PrepareCsvPatches(csvPatchFiles);
        for (int i = 0; i < csvPatchFiles.Count; i += 1)
        {
            RegisterCsvPatch(csvPatchFiles[i]);
        }

        SymbolicFieldManager.WriteReport();

        MelonLogger.Msg($"ModsOfLong ready: '{_modsOfLongRoot}'. Loaded {LoadedMods.Count} data mod(s), {totalPatchFiles} CSV patch file(s).");
    }

    private static string NormalizeLookupKey(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string ComputeHash(string content)
    {
        byte[] bytes = Utf8NoBom.GetBytes(content);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed class DataModInfoFile
    {
        public string? Name { get; set; }
    }

    private sealed class DataModDefinition
    {
        public string FolderName { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string ModDirectory { get; set; } = string.Empty;

        public string DataDirectory { get; set; } = string.Empty;

        public int PatchFileCount { get; set; }
    }

    private sealed class CsvPatchFile
    {
        public string ModName { get; set; } = string.Empty;

        public string FullPath { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public string SourcePath { get; set; } = string.Empty;

        public int KeyColumnIndex { get; set; } = -1;

        public int LoadOrder { get; set; }

        public HashSet<string> SymbolicIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public List<List<string>> Rows { get; set; } = new();
    }

    private sealed class FilePatchResult
    {
        public string ModName { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public int AddedRowCount { get; set; }

        public int ModifiedRowCount { get; set; }
    }

    private sealed class SymbolicSourceInfo
    {
        public string SourcePath { get; set; } = string.Empty;

        public int KeyColumnIndex { get; set; } = -1;

        public bool HasBaseMaxId { get; set; }

        public int BaseMaxId { get; set; }

        public HashSet<string> SymbolicIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SymbolicIdGroup
    {
        public HashSet<string> SourcePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SymbolicIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> OrderedSymbolicIds { get; set; } = new();

        public Dictionary<string, int> AssignedIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int MaxAssignedId { get; set; }
    }

    private sealed class SortableRow
    {
        public int OriginalIndex { get; set; }

        public int NumericId { get; set; }

        public List<string> Row { get; set; } = new();
    }
}
