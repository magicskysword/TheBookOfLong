using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace TheBookOfLong;

internal static partial class DataModManager
{
    private static void PrepareCsvPatches(List<CsvPatchFile> csvPatchFiles)
    {
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

        MergeExternalSymbolicSources(symbolicSources);

        if (symbolicSources.Count == 0)
        {
            PublishExternalSymbolicSources();
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

        PublishResolvedSymbolicSources(symbolicSources, groups);
    }

    private static void RegisterCsvSymbolicReferences(CsvPatchFile patchFile)
    {
        if (patchFile.KeyColumnIndex < 0)
        {
            return;
        }

        for (int rowIndex = 1; rowIndex < patchFile.Rows.Count; rowIndex += 1)
        {
            string key = GetCell(patchFile.Rows[rowIndex], patchFile.KeyColumnIndex);
            if (!TryGetSymbolicId(key, out string symbolicId))
            {
                continue;
            }

            SymbolicFieldManager.RegisterReference(
                patchFile.SourcePath,
                symbolicId,
                patchFile.ModName,
                patchFile.RelativePath,
                $"row {rowIndex + 1}",
                "csv-key");
        }
    }

    private static void MergeExternalSymbolicSources(Dictionary<string, SymbolicSourceInfo> symbolicSources)
    {
        foreach ((string sourcePath, HashSet<string> symbolicIds) in ExternalSymbolicIdsBySourcePath)
        {
            if (!symbolicSources.TryGetValue(sourcePath, out SymbolicSourceInfo? sourceInfo))
            {
                sourceInfo = new SymbolicSourceInfo
                {
                    SourcePath = sourcePath,
                    KeyColumnIndex = -1
                };

                symbolicSources[sourcePath] = sourceInfo;
            }

            sourceInfo.SymbolicIds.UnionWith(symbolicIds);
        }
    }

    private static void PublishResolvedSymbolicSources(
        Dictionary<string, SymbolicSourceInfo> symbolicSources,
        List<SymbolicIdGroup> groups)
    {
        foreach (SymbolicSourceInfo sourceInfo in symbolicSources.Values)
        {
            SymbolicFieldManager.UpdateSourceBaseInfo(sourceInfo.SourcePath, sourceInfo.HasBaseMaxId, sourceInfo.BaseMaxId);
        }

        for (int i = 0; i < groups.Count; i += 1)
        {
            SymbolicIdGroup group = groups[i];
            foreach (string sourcePath in group.SourcePaths)
            {
                SymbolicFieldManager.UpdateSourceAssignments(sourcePath, group.AssignedIds);
            }
        }
    }

    private static void PublishExternalSymbolicSources()
    {
        foreach ((string sourcePath, HashSet<string> _) in ExternalSymbolicIdsBySourcePath)
        {
            SymbolicFieldManager.UpdateSourceBaseInfo(sourcePath, hasBaseMaxId: false, baseMaxId: 0);
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
        string normalizedSourcePath = NormalizeLookupKey(sourcePath);
        string gameDataPrefix = "GameData" + Path.DirectorySeparatorChar;
        if (normalizedSourcePath.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalizedSourcePath = Path.Combine("Data", normalizedSourcePath.Substring(gameDataPrefix.Length));
        }
        else if (string.Equals(normalizedSourcePath, "GameData", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSourcePath = "Data";
        }

        return Path.Combine(_gameRoot, "DataDump", "Latest", normalizedSourcePath);
    }

    private static string FormatSourcePathList(IEnumerable<string> sourcePaths)
    {
        return string.Join(", ", sourcePaths);
    }
}
