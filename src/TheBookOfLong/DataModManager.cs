using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MelonLoader;

namespace TheBookOfLong;

internal static class DataModManager
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Dictionary<int, string> ResourcePathsByInstanceId = new();
    private static readonly Dictionary<string, List<CsvPatchFile>> CsvPatchesByLookupKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PatchedContentCache = new(StringComparer.Ordinal);
    private static readonly List<DataModDefinition> LoadedMods = new();
    private static readonly Dictionary<string, SymbolicSourceInfo> SymbolicSourcesBySourcePath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SymbolicIdGroup> SymbolicGroupsBySourcePath = new(StringComparer.OrdinalIgnoreCase);

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

        MelonLogger.Msg($"ModsOfLong ready: '{_modsOfLongRoot}'. Loaded {LoadedMods.Count} data mod(s), {totalPatchFiles} CSV patch file(s).");
    }

    private static DataModDefinition LoadDataMod(string modDirectory, List<CsvPatchFile> csvPatchFiles)
    {
        string folderName = Path.GetFileName(modDirectory);
        string displayName = ResolveDataModDisplayName(modDirectory, folderName);
        string dataDirectory = Path.Combine(modDirectory, "Data");
        int patchFileCount = 0;

        if (Directory.Exists(dataDirectory))
        {
            string[] patchFiles = Directory.GetFiles(dataDirectory, "*.csv", SearchOption.AllDirectories);
            Array.Sort(patchFiles, StringComparer.OrdinalIgnoreCase);

            foreach (string patchFilePath in patchFiles)
            {
                if (TryLoadCsvPatchFile(displayName, dataDirectory, patchFilePath, out CsvPatchFile? csvPatchFile))
                {
                    csvPatchFiles.Add(csvPatchFile!);
                    patchFileCount += 1;
                }
            }
        }

        return new DataModDefinition
        {
            FolderName = folderName,
            DisplayName = displayName,
            ModDirectory = modDirectory,
            DataDirectory = dataDirectory,
            PatchFileCount = patchFileCount
        };
    }

    private static string ResolveDataModDisplayName(string modDirectory, string folderName)
    {
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
                MelonLogger.Warning($"Failed to read data mod info '{infoFilePath}': {ex.Message}");
            }
        }

        string fallbackName = folderName.StartsWith("mod", StringComparison.OrdinalIgnoreCase)
            ? folderName.Substring(3).TrimStart(' ', '_', '-')
            : folderName;

        return string.IsNullOrWhiteSpace(fallbackName) ? folderName : fallbackName;
    }

    private static bool TryLoadCsvPatchFile(string modName, string dataDirectory, string patchFilePath, out CsvPatchFile? csvPatchFile)
    {
        csvPatchFile = null;

        try
        {
            string content = File.ReadAllText(patchFilePath, Utf8NoBom);
            List<List<string>> rows = ParseCsv(content);
            if (rows.Count == 0)
            {
                MelonLogger.Warning($"Skipped empty data patch file '{patchFilePath}'.");
                return false;
            }

            string relativePath = NormalizeLookupKey(Path.GetRelativePath(dataDirectory, patchFilePath));
            int keyColumnIndex = ResolveKeyColumnIndex(rows[0]);
            csvPatchFile = new CsvPatchFile
            {
                ModName = modName,
                FullPath = patchFilePath,
                RelativePath = relativePath,
                SourcePath = BuildCanonicalSourcePath(relativePath),
                KeyColumnIndex = keyColumnIndex,
                LoadOrder = ++_patchFileOrder,
                SymbolicIds = CollectSymbolicIds(rows, keyColumnIndex),
                Rows = rows
            };

            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Failed to load data patch file '{patchFilePath}': {ex.Message}");
            return false;
        }
    }

    private static void PrepareCsvPatches(List<CsvPatchFile> csvPatchFiles)
    {
        if (csvPatchFiles.Count == 0)
        {
            return;
        }

        Dictionary<string, SymbolicSourceInfo> symbolicSources = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < csvPatchFiles.Count; i += 1)
        {
            CsvPatchFile patchFile = csvPatchFiles[i];
            if (patchFile.SymbolicIds.Count == 0)
            {
                continue;
            }

            if (!symbolicSources.TryGetValue(patchFile.SourcePath, out SymbolicSourceInfo? sourceInfo))
            {
                sourceInfo = new SymbolicSourceInfo
                {
                    SourcePath = patchFile.SourcePath,
                    KeyColumnIndex = patchFile.KeyColumnIndex
                };

                symbolicSources[sourceInfo.SourcePath] = sourceInfo;
            }
            else if (sourceInfo.KeyColumnIndex != patchFile.KeyColumnIndex)
            {
                MelonLogger.Warning(
                    $"Data patch '{patchFile.FullPath}' uses key column {patchFile.KeyColumnIndex}, but other patches for '{patchFile.SourcePath}' use key column {sourceInfo.KeyColumnIndex}. The first detected key column will be used for string ID resolution.");
            }

            sourceInfo.SymbolicIds.UnionWith(patchFile.SymbolicIds);
        }

        if (symbolicSources.Count == 0)
        {
            return;
        }

        foreach (SymbolicSourceInfo sourceInfo in symbolicSources.Values)
        {
            if (TryGetBaseMaxId(sourceInfo.SourcePath, sourceInfo.KeyColumnIndex, out int baseMaxId, out string? warning))
            {
                sourceInfo.HasBaseMaxId = true;
                sourceInfo.BaseMaxId = baseMaxId;
            }
            else if (!string.IsNullOrWhiteSpace(warning))
            {
                MelonLogger.Warning(warning);
            }
        }

        List<SymbolicIdGroup> groups = BuildSymbolicIdGroups(symbolicSources);
        for (int i = 0; i < groups.Count; i += 1)
        {
            ResolveSymbolicIdGroup(groups[i], symbolicSources);
        }

        foreach ((string sourcePath, SymbolicSourceInfo sourceInfo) in symbolicSources)
        {
            SymbolicSourcesBySourcePath[sourcePath] = sourceInfo;
        }

        for (int i = 0; i < csvPatchFiles.Count; i += 1)
        {
            ApplyResolvedSymbolicIds(csvPatchFiles[i]);
        }
    }

    private static List<SymbolicIdGroup> BuildSymbolicIdGroups(Dictionary<string, SymbolicSourceInfo> symbolicSources)
    {
        Dictionary<string, HashSet<string>> sourcePathsBySymbolicId = new(StringComparer.OrdinalIgnoreCase);
        foreach (SymbolicSourceInfo sourceInfo in symbolicSources.Values)
        {
            foreach (string symbolicId in sourceInfo.SymbolicIds)
            {
                if (!sourcePathsBySymbolicId.TryGetValue(symbolicId, out HashSet<string>? sourcePaths))
                {
                    sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    sourcePathsBySymbolicId[symbolicId] = sourcePaths;
                }

                sourcePaths.Add(sourceInfo.SourcePath);
            }
        }

        Dictionary<string, HashSet<string>> neighborsBySourcePath = new(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in symbolicSources.Keys)
        {
            neighborsBySourcePath[sourcePath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (HashSet<string> sourcePaths in sourcePathsBySymbolicId.Values)
        {
            string[] items = new string[sourcePaths.Count];
            sourcePaths.CopyTo(items);
            for (int i = 0; i < items.Length; i += 1)
            {
                for (int j = i + 1; j < items.Length; j += 1)
                {
                    neighborsBySourcePath[items[i]].Add(items[j]);
                    neighborsBySourcePath[items[j]].Add(items[i]);
                }
            }
        }

        List<SymbolicIdGroup> groups = new();
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        foreach (string sourcePath in symbolicSources.Keys)
        {
            if (!visited.Add(sourcePath))
            {
                continue;
            }

            Queue<string> pending = new();
            pending.Enqueue(sourcePath);

            SymbolicIdGroup group = new();
            while (pending.Count > 0)
            {
                string currentSourcePath = pending.Dequeue();
                group.SourcePaths.Add(currentSourcePath);
                group.SymbolicIds.UnionWith(symbolicSources[currentSourcePath].SymbolicIds);

                foreach (string neighbor in neighborsBySourcePath[currentSourcePath])
                {
                    if (visited.Add(neighbor))
                    {
                        pending.Enqueue(neighbor);
                    }
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static void ResolveSymbolicIdGroup(SymbolicIdGroup group, Dictionary<string, SymbolicSourceInfo> symbolicSources)
    {
        List<string> orderedSymbolicIds = new(group.SymbolicIds);
        orderedSymbolicIds.Sort(StringComparer.OrdinalIgnoreCase);
        group.OrderedSymbolicIds = orderedSymbolicIds;

        int maxExistingId = int.MinValue;
        bool hasBaseMaxId = false;

        foreach (string sourcePath in group.SourcePaths)
        {
            SymbolicSourceInfo sourceInfo = symbolicSources[sourcePath];
            if (!sourceInfo.HasBaseMaxId)
            {
                continue;
            }

            if (!hasBaseMaxId || sourceInfo.BaseMaxId > maxExistingId)
            {
                maxExistingId = sourceInfo.BaseMaxId;
                hasBaseMaxId = true;
            }
        }

        if (!hasBaseMaxId)
        {
            maxExistingId = 0;
            MelonLogger.Warning(
                $"Could not resolve the base max ID for string IDs used by {FormatSourcePathList(group.SourcePaths)}. New IDs will start at 1, and conflicts may still occur if the base tables already contain higher IDs.");
        }

        int nextId = maxExistingId + 1;
        for (int i = 0; i < group.OrderedSymbolicIds.Count; i += 1)
        {
            string symbolicId = group.OrderedSymbolicIds[i];
            group.AssignedIds[symbolicId] = nextId;
            nextId += 1;
        }

        group.MaxAssignedId = nextId - 1;

        foreach (string sourcePath in group.SourcePaths)
        {
            SymbolicGroupsBySourcePath[sourcePath] = group;
        }
    }

    private static void ApplyResolvedSymbolicIds(CsvPatchFile patchFile)
    {
        if (patchFile.SymbolicIds.Count == 0
            || patchFile.KeyColumnIndex < 0
            || !SymbolicGroupsBySourcePath.TryGetValue(patchFile.SourcePath, out SymbolicIdGroup? group))
        {
            return;
        }

        List<List<string>> resolvedRows = new(patchFile.Rows.Count);
        for (int rowIndex = 0; rowIndex < patchFile.Rows.Count; rowIndex += 1)
        {
            List<string> row = new(patchFile.Rows[rowIndex]);

            if (rowIndex > 0 && patchFile.KeyColumnIndex < row.Count)
            {
                string key = row[patchFile.KeyColumnIndex];
                if (TryGetSymbolicId(key, out string symbolicId)
                    && group.AssignedIds.TryGetValue(symbolicId, out int assignedId))
                {
                    row[patchFile.KeyColumnIndex] = assignedId.ToString();
                }
            }

            resolvedRows.Add(row);
        }

        patchFile.Rows = resolvedRows;
    }

    private static HashSet<string> CollectSymbolicIds(List<List<string>> rows, int keyColumnIndex)
    {
        HashSet<string> symbolicIds = new(StringComparer.OrdinalIgnoreCase);
        if (keyColumnIndex < 0)
        {
            return symbolicIds;
        }

        for (int i = 1; i < rows.Count; i += 1)
        {
            string key = GetCell(rows[i], keyColumnIndex);
            if (TryGetSymbolicId(key, out string symbolicId))
            {
                symbolicIds.Add(symbolicId);
            }
        }

        return symbolicIds;
    }

    private static bool TryGetSymbolicId(string? value, out string symbolicId)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length > 3 && trimmed.StartsWith("mod", StringComparison.OrdinalIgnoreCase))
        {
            symbolicId = trimmed;
            return true;
        }

        symbolicId = string.Empty;
        return false;
    }

    private static string BuildCanonicalSourcePath(string path)
    {
        string normalizedPath = NormalizeLookupKey(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return string.Empty;
        }

        string withExtension = string.IsNullOrEmpty(Path.GetExtension(normalizedPath))
            ? normalizedPath + ".csv"
            : normalizedPath;

        string gameDataPrefix = "GameData" + Path.DirectorySeparatorChar;
        if (!withExtension.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            withExtension = Path.Combine("GameData", withExtension);
        }

        return NormalizeLookupKey(withExtension);
    }

    private static bool TryGetBaseMaxId(string sourcePath, int fallbackKeyColumnIndex, out int baseMaxId, out string? warning)
    {
        baseMaxId = 0;
        warning = null;

        string latestDumpPath = GetLatestDumpPath(sourcePath);
        if (!File.Exists(latestDumpPath))
        {
            warning = $"Could not inspect base CSV '{sourcePath}' while resolving string IDs because '{latestDumpPath}' does not exist.";
            return false;
        }

        try
        {
            string content = File.ReadAllText(latestDumpPath, Utf8NoBom);
            List<List<string>> rows = ParseCsv(content);
            if (rows.Count == 0)
            {
                baseMaxId = 0;
                return true;
            }

            int keyColumnIndex = ResolveKeyColumnIndex(rows[0]);
            if (keyColumnIndex < 0)
            {
                keyColumnIndex = fallbackKeyColumnIndex;
            }

            bool foundNumericId = false;
            int maxId = int.MinValue;

            for (int i = 1; i < rows.Count; i += 1)
            {
                string key = GetCell(rows[i], keyColumnIndex);
                if (!int.TryParse(key, out int numericId))
                {
                    continue;
                }

                if (!foundNumericId || numericId > maxId)
                {
                    maxId = numericId;
                    foundNumericId = true;
                }
            }

            baseMaxId = foundNumericId ? maxId : 0;
            return true;
        }
        catch (Exception ex)
        {
            warning = $"Failed to inspect base CSV '{sourcePath}' while resolving string IDs: {ex.Message}";
            return false;
        }
    }

    private static string GetLatestDumpPath(string sourcePath)
    {
        return Path.Combine(_gameRoot, "DataDump", "Latest", sourcePath);
    }

    private static string FormatSourcePathList(IEnumerable<string> sourcePaths)
    {
        return string.Join(", ", sourcePaths);
    }

    private static void RegisterCsvPatch(CsvPatchFile csvPatchFile)
    {
        foreach (string lookupKey in BuildPatchLookupKeys(csvPatchFile.RelativePath))
        {
            if (!CsvPatchesByLookupKey.TryGetValue(lookupKey, out List<CsvPatchFile>? patchFiles))
            {
                patchFiles = new List<CsvPatchFile>();
                CsvPatchesByLookupKey[lookupKey] = patchFiles;
            }

            patchFiles.Add(csvPatchFile);
        }
    }

    private static List<CsvPatchFile> GetMatchingCsvPatches(string sourcePath)
    {
        List<CsvPatchFile> matches = new();
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string lookupKey in BuildSourceLookupKeys(sourcePath))
        {
            if (!CsvPatchesByLookupKey.TryGetValue(lookupKey, out List<CsvPatchFile>? patchFiles))
            {
                continue;
            }

            for (int i = 0; i < patchFiles.Count; i += 1)
            {
                CsvPatchFile patchFile = patchFiles[i];
                if (seenPaths.Add(patchFile.FullPath))
                {
                    matches.Add(patchFile);
                }
            }
        }

        matches.Sort(static (left, right) => left.LoadOrder.CompareTo(right.LoadOrder));
        return matches;
    }

    private static IEnumerable<string> BuildPatchLookupKeys(string relativePath)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        string normalizedPath = NormalizeLookupKey(relativePath);
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            keys.Add(normalizedPath);

            string fileName = Path.GetFileName(normalizedPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                keys.Add(fileName);
            }

            string gameDataPrefix = "GameData" + Path.DirectorySeparatorChar;
            if (normalizedPath.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(normalizedPath.Substring(gameDataPrefix.Length));
            }
            else
            {
                keys.Add(Path.Combine("GameData", normalizedPath));
            }
        }

        return keys;
    }

    private static IEnumerable<string> BuildSourceLookupKeys(string sourcePath)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        string normalizedSourcePath = NormalizeLookupKey(sourcePath);
        string withExtension = string.IsNullOrEmpty(Path.GetExtension(normalizedSourcePath))
            ? normalizedSourcePath + ".csv"
            : normalizedSourcePath;

        if (!string.IsNullOrWhiteSpace(withExtension))
        {
            keys.Add(withExtension);

            string fileName = Path.GetFileName(withExtension);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                keys.Add(fileName);
            }

            string gameDataPrefix = "GameData" + Path.DirectorySeparatorChar;
            if (withExtension.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                keys.Add(withExtension.Substring(gameDataPrefix.Length));
            }
        }

        return keys;
    }

    private static string ResolveTextAssetSourcePath(global::UnityEngine.TextAsset textAsset)
    {
        try
        {
            int instanceId = textAsset.GetInstanceID();
            if (ResourcePathsByInstanceId.TryGetValue(instanceId, out string? resourcePath) && !string.IsNullOrWhiteSpace(resourcePath))
            {
                return resourcePath;
            }
        }
        catch
        {
        }

        try
        {
            string name = textAsset.name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return NormalizeLookupKey(name);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool TryFinalizePatchedCsv(
        string content,
        string sourcePath,
        out string finalizedContent,
        out List<string> warnings)
    {
        finalizedContent = content;
        warnings = new List<string>();

        List<List<string>> rows;
        try
        {
            rows = ParseCsv(content);
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to finalize patched CSV '{sourcePath}': {ex.Message}");
            return false;
        }

        if (rows.Count == 0)
        {
            return true;
        }

        List<string> header = rows[0];
        int keyColumnIndex = ResolveKeyColumnIndex(header);
        if (keyColumnIndex < 0)
        {
            return true;
        }

        string canonicalSourcePath = BuildCanonicalSourcePath(sourcePath);
        bool changed = false;

        if (SymbolicGroupsBySourcePath.TryGetValue(canonicalSourcePath, out SymbolicIdGroup? group)
            && SymbolicSourcesBySourcePath.TryGetValue(canonicalSourcePath, out SymbolicSourceInfo? sourceInfo))
        {
            if (!sourceInfo.HasBaseMaxId)
            {
                warnings.Add(
                    $"Source '{canonicalSourcePath}' uses shared string IDs, but its base max ID could not be determined. Blank placeholder rows were not generated for missing IDs.");
            }
            else
            {
                List<int> insertedPlaceholderIds = EnsureSequentialRowsForGroup(rows, keyColumnIndex, sourceInfo.BaseMaxId + 1, group.MaxAssignedId);
                if (insertedPlaceholderIds.Count > 0)
                {
                    changed = true;
                    warnings.Add(
                        $"Source '{canonicalSourcePath}' is missing {insertedPlaceholderIds.Count} row(s) required to keep shared string IDs aligned. Added blank placeholder row(s) for ID {FormatIdRanges(insertedPlaceholderIds)}.");
                }
            }
        }

        if (TrySortRowsByNumericKey(rows, keyColumnIndex))
        {
            changed = true;
        }

        if (changed)
        {
            finalizedContent = SerializeCsv(rows);
        }

        return true;
    }

    private static List<int> EnsureSequentialRowsForGroup(List<List<string>> rows, int keyColumnIndex, int startId, int endId)
    {
        List<int> insertedIds = new();
        if (startId > endId)
        {
            return insertedIds;
        }

        HashSet<int> existingIds = new();
        for (int i = 1; i < rows.Count; i += 1)
        {
            string key = GetCell(rows[i], keyColumnIndex);
            if (int.TryParse(key, out int numericId))
            {
                existingIds.Add(numericId);
            }
        }

        int columnCount = rows[0].Count;
        for (int numericId = startId; numericId <= endId; numericId += 1)
        {
            if (existingIds.Contains(numericId))
            {
                continue;
            }

            List<string> blankRow = CreateBlankRow(columnCount, keyColumnIndex, numericId);
            rows.Add(blankRow);
            existingIds.Add(numericId);
            insertedIds.Add(numericId);
        }

        return insertedIds;
    }

    private static List<string> CreateBlankRow(int columnCount, int keyColumnIndex, int numericId)
    {
        List<string> row = new(columnCount);
        for (int i = 0; i < columnCount; i += 1)
        {
            row.Add(string.Empty);
        }

        if (keyColumnIndex >= 0 && keyColumnIndex < row.Count)
        {
            row[keyColumnIndex] = numericId.ToString();
        }

        return row;
    }

    private static bool TrySortRowsByNumericKey(List<List<string>> rows, int keyColumnIndex)
    {
        if (rows.Count <= 2)
        {
            return false;
        }

        List<SortableRow> sortableRows = new(rows.Count - 1);
        for (int i = 1; i < rows.Count; i += 1)
        {
            string key = GetCell(rows[i], keyColumnIndex);
            if (!int.TryParse(key, out int numericId))
            {
                return false;
            }

            sortableRows.Add(new SortableRow
            {
                OriginalIndex = i,
                NumericId = numericId,
                Row = rows[i]
            });
        }

        sortableRows.Sort(static (left, right) =>
        {
            int compare = left.NumericId.CompareTo(right.NumericId);
            return compare != 0 ? compare : left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        bool changed = false;
        for (int i = 0; i < sortableRows.Count; i += 1)
        {
            if (!ReferenceEquals(rows[i + 1], sortableRows[i].Row))
            {
                changed = true;
            }

            rows[i + 1] = sortableRows[i].Row;
        }

        return changed;
    }

    private static string FormatIdRanges(List<int> ids)
    {
        if (ids.Count == 0)
        {
            return string.Empty;
        }

        ids.Sort();

        List<string> parts = new();
        int rangeStart = ids[0];
        int rangeEnd = ids[0];

        for (int i = 1; i < ids.Count; i += 1)
        {
            int currentId = ids[i];
            if (currentId == rangeEnd + 1)
            {
                rangeEnd = currentId;
                continue;
            }

            parts.Add(rangeStart == rangeEnd ? rangeStart.ToString() : $"{rangeStart}-{rangeEnd}");
            rangeStart = currentId;
            rangeEnd = currentId;
        }

        parts.Add(rangeStart == rangeEnd ? rangeStart.ToString() : $"{rangeStart}-{rangeEnd}");
        return string.Join(", ", parts);
    }

    private static bool LooksLikeCsvText(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        return trimmed[0] != '{' && trimmed[0] != '[';
    }

    private static bool TryMergeCsvPatch(
        string baseContent,
        CsvPatchFile patchFile,
        string sourcePath,
        out string mergedContent,
        out int addedRowCount,
        out int modifiedRowCount,
        out string? warning)
    {
        mergedContent = baseContent;
        addedRowCount = 0;
        modifiedRowCount = 0;
        warning = null;

        List<List<string>> baseRows;
        try
        {
            baseRows = ParseCsv(baseContent);
        }
        catch (Exception ex)
        {
            warning = $"Failed to parse base CSV '{sourcePath}' before applying '{patchFile.FullPath}': {ex.Message}";
            return false;
        }

        if (baseRows.Count == 0 || patchFile.Rows.Count == 0)
        {
            return true;
        }

        List<string> baseHeader = baseRows[0];
        List<string> patchHeader = patchFile.Rows[0];
        if (!HeadersEqual(baseHeader, patchHeader, out string? headerMismatch))
        {
            warning = $"Skipped data patch '{patchFile.FullPath}' for '{sourcePath}' because the header does not match the base CSV ({headerMismatch}).";
            return false;
        }

        int keyColumnIndex = ResolveKeyColumnIndex(baseHeader);
        Dictionary<string, int> rowIndexByKey = new(StringComparer.Ordinal);
        if (keyColumnIndex >= 0)
        {
            for (int i = 1; i < baseRows.Count; i += 1)
            {
                string key = GetCell(baseRows[i], keyColumnIndex);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    rowIndexByKey[key] = i;
                }
            }
        }

        for (int i = 1; i < patchFile.Rows.Count; i += 1)
        {
            if (!TryNormalizeRow(patchFile.Rows[i], baseHeader.Count, out List<string>? normalizedRow))
            {
                warning = $"Skipped data patch '{patchFile.FullPath}' for '{sourcePath}' because row {i + 1} has more columns than the header.";
                return false;
            }

            if (normalizedRow is null || IsRowEmpty(normalizedRow))
            {
                continue;
            }

            if (keyColumnIndex >= 0)
            {
                string key = normalizedRow[keyColumnIndex];
                if (!string.IsNullOrWhiteSpace(key) && rowIndexByKey.TryGetValue(key, out int existingRowIndex))
                {
                    if (!RowsEqual(baseRows[existingRowIndex], normalizedRow))
                    {
                        baseRows[existingRowIndex] = normalizedRow;
                        modifiedRowCount += 1;
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(key))
                {
                    rowIndexByKey[key] = baseRows.Count;
                }
            }

            baseRows.Add(normalizedRow);
            addedRowCount += 1;
        }

        if (addedRowCount == 0 && modifiedRowCount == 0)
        {
            return true;
        }

        mergedContent = SerializeCsv(baseRows);
        return true;
    }

    private static bool HeadersEqual(List<string> left, List<string> right, out string? mismatch)
    {
        mismatch = null;

        if (left.Count != right.Count)
        {
            mismatch = $"base has {left.Count} columns, patch has {right.Count} columns";
            return false;
        }

        for (int i = 0; i < left.Count; i += 1)
        {
            string leftCell = NormalizeHeaderCell(left[i]);
            string rightCell = NormalizeHeaderCell(right[i]);
            if (!string.Equals(leftCell, rightCell, StringComparison.Ordinal))
            {
                mismatch = $"column {i + 1}: base='{EscapeLogValue(leftCell)}', patch='{EscapeLogValue(rightCell)}'";
                return false;
            }
        }

        return true;
    }

    private static string NormalizeHeaderCell(string value)
    {
        return !string.IsNullOrEmpty(value) && value[0] == '\uFEFF'
            ? value.Substring(1)
            : value;
    }

    private static string EscapeLogValue(string value)
    {
        return value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static int ResolveKeyColumnIndex(List<string> header)
    {
        string[] exactKeyNames =
        {
            "id",
            "ID",
            "Id",
            "编号",
            "序号",
            "剧情编号"
        };

        for (int i = 0; i < exactKeyNames.Length; i += 1)
        {
            int index = IndexOfHeader(header, exactKeyNames[i]);
            if (index >= 0)
            {
                return index;
            }
        }

        for (int i = 0; i < header.Count; i += 1)
        {
            string columnName = header[i].Trim();
            if (string.IsNullOrWhiteSpace(columnName))
            {
                continue;
            }

            if (columnName.EndsWith("id", StringComparison.OrdinalIgnoreCase)
                || columnName.Contains("编号", StringComparison.Ordinal)
                || columnName.Contains("序号", StringComparison.Ordinal))
            {
                return i;
            }
        }

        if (header.Count == 1)
        {
            return 0;
        }

        return !string.IsNullOrWhiteSpace(header[0]) ? 0 : -1;
    }

    private static int IndexOfHeader(List<string> header, string name)
    {
        for (int i = 0; i < header.Count; i += 1)
        {
            if (string.Equals(header[i].Trim(), name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetCell(List<string> row, int columnIndex)
    {
        return columnIndex >= 0 && columnIndex < row.Count ? row[columnIndex] : string.Empty;
    }

    private static bool TryNormalizeRow(List<string> row, int columnCount, out List<string>? normalizedRow)
    {
        normalizedRow = new List<string>(columnCount);

        if (row.Count > columnCount)
        {
            for (int i = columnCount; i < row.Count; i += 1)
            {
                if (!string.IsNullOrEmpty(row[i]))
                {
                    normalizedRow = null;
                    return false;
                }
            }
        }

        for (int i = 0; i < columnCount; i += 1)
        {
            normalizedRow.Add(i < row.Count ? row[i] : string.Empty);
        }

        return true;
    }

    private static bool IsRowEmpty(List<string> row)
    {
        for (int i = 0; i < row.Count; i += 1)
        {
            if (!string.IsNullOrEmpty(row[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RowsEqual(List<string> left, List<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i += 1)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static List<List<string>> ParseCsv(string content)
    {
        List<List<string>> rows = new();
        List<string> currentRow = new();
        StringBuilder currentCell = new();
        bool inQuotes = false;

        void EndCell()
        {
            currentRow.Add(currentCell.ToString());
            currentCell.Clear();
        }

        void EndRow()
        {
            EndCell();

            bool hasContent = false;
            for (int i = 0; i < currentRow.Count; i += 1)
            {
                if (!string.IsNullOrEmpty(currentRow[i]))
                {
                    hasContent = true;
                    break;
                }
            }

            if (hasContent || currentRow.Count > 1)
            {
                rows.Add(currentRow);
            }

            currentRow = new List<string>();
        }

        for (int i = 0; i < content.Length; i += 1)
        {
            char ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    bool escapedQuote = i + 1 < content.Length && content[i + 1] == '"';
                    if (escapedQuote)
                    {
                        currentCell.Append('"');
                        i += 1;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentCell.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    EndCell();
                    break;
                case '\r':
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                    {
                        i += 1;
                    }

                    EndRow();
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    currentCell.Append(ch);
                    break;
            }
        }

        bool hasPendingContent = currentCell.Length > 0 || currentRow.Count > 0;
        if (hasPendingContent)
        {
            EndRow();
        }

        return rows;
    }

    private static string SerializeCsv(List<List<string>> rows)
    {
        StringBuilder builder = new();
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex += 1)
        {
            if (rowIndex > 0)
            {
                builder.Append("\r\n");
            }

            List<string> row = rows[rowIndex];
            for (int columnIndex = 0; columnIndex < row.Count; columnIndex += 1)
            {
                if (columnIndex > 0)
                {
                    builder.Append(',');
                }

                builder.Append(EscapeCsvCell(row[columnIndex]));
            }
        }

        return builder.ToString();
    }

    private static string EscapeCsvCell(string value)
    {
        bool requiresQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!requiresQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
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
