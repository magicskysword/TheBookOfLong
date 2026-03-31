using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace TheBookOfLong;

internal static partial class DataModManager
{
    private static int LoadDataMod(IModProject modProject, List<CsvPatchFile> csvPatchFiles)
    {
        int patchFileCount = 0;

        for (int i = 0; i < modProject.CsvPatchFiles.Count; i += 1)
        {
            string patchFilePath = modProject.CsvPatchFiles[i];
            if (TryLoadCsvPatchFile(modProject, patchFilePath, out CsvPatchFile? csvPatchFile))
            {
                csvPatchFiles.Add(csvPatchFile!);
                patchFileCount += 1;
            }
        }

        return patchFileCount;
    }

    private static bool TryLoadCsvPatchFile(IModProject modProject, string patchFilePath, out CsvPatchFile? csvPatchFile)
    {
        csvPatchFile = null;

        try
        {
            string content = File.ReadAllText(patchFilePath, Utf8NoBom);
            List<List<string>> rows = CsvUtility.Parse(content);
            if (rows.Count == 0)
            {
                MelonLogger.Warning($"Skipped empty data patch file '{patchFilePath}'.");
                return false;
            }

            string relativePath = NormalizeLookupKey(Path.GetRelativePath(modProject.DataDirectory, patchFilePath));
            int keyColumnIndex = CsvUtility.ResolveKeyColumnIndex(rows[0]);
            csvPatchFile = new CsvPatchFile
            {
                ModName = modProject.DisplayName,
                FullPath = patchFilePath,
                RelativePath = relativePath,
                SourcePath = SymbolicIdService.BuildCanonicalSourcePath(relativePath),
                KeyColumnIndex = keyColumnIndex,
                LoadOrder = ++_patchFileOrder,
                SymbolicIds = SymbolicIdService.CollectSymbolicIds(rows, keyColumnIndex),
                Rows = rows
            };

            SymbolicIdService.RegisterCsvReferences(csvPatchFile);
            MelonLogger.Msg(
                $"Loaded data patch '{modProject.DisplayName}' (v{modProject.Version}, order {modProject.LoadOrder}): '{csvPatchFile.RelativePath}' -> '{csvPatchFile.SourcePath}'");

            return true;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Failed to load data patch file '{patchFilePath}': {ex.Message}");
            return false;
        }
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
        HashSet<string> seenPatchFiles = new(StringComparer.OrdinalIgnoreCase);

        foreach (string lookupKey in BuildSourceLookupKeys(sourcePath))
        {
            if (!CsvPatchesByLookupKey.TryGetValue(lookupKey, out List<CsvPatchFile>? patchFiles))
            {
                continue;
            }

            foreach (CsvPatchFile patchFile in patchFiles)
            {
                if (seenPatchFiles.Add(patchFile.FullPath))
                {
                    matches.Add(patchFile);
                }
            }
        }

        matches.Sort(static (left, right) =>
        {
            int compare = left.LoadOrder.CompareTo(right.LoadOrder);
            return compare != 0
                ? compare
                : string.Compare(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);
        });

        return matches;
    }

    private static IEnumerable<string> BuildPatchLookupKeys(string relativePath)
    {
        string normalizedPath = NormalizeLookupKey(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            yield break;
        }

        string withExtension = string.IsNullOrEmpty(Path.GetExtension(normalizedPath))
            ? normalizedPath + ".csv"
            : normalizedPath;
        string withoutExtension = Path.Combine(
            Path.GetDirectoryName(withExtension) ?? string.Empty,
            Path.GetFileNameWithoutExtension(withExtension));

        yield return withExtension;
        if (!string.Equals(withExtension, withoutExtension, StringComparison.OrdinalIgnoreCase))
        {
            yield return NormalizeLookupKey(withoutExtension);
        }

        string fileName = Path.GetFileName(withExtension);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            yield return fileName;
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(withExtension);
        if (!string.IsNullOrWhiteSpace(fileNameWithoutExtension))
        {
            yield return fileNameWithoutExtension;
        }

        string gameDataPrefix = "GameData" + Path.DirectorySeparatorChar;
        if (!withExtension.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine("GameData", withExtension);
            yield return Path.Combine("GameData", NormalizeLookupKey(withoutExtension));
        }
    }

    private static IEnumerable<string> BuildSourceLookupKeys(string sourcePath)
    {
        string normalizedPath = NormalizeLookupKey(sourcePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            yield break;
        }

        yield return normalizedPath;

        string fileName = Path.GetFileName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            yield return fileName;
        }

        string gameDataPrefix = "GameData" + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine("GameData", normalizedPath);
        }

        if (string.IsNullOrEmpty(Path.GetExtension(normalizedPath)))
        {
            string withExtension = normalizedPath + ".csv";
            yield return withExtension;

            string fileNameWithExtension = Path.GetFileName(withExtension);
            if (!string.IsNullOrWhiteSpace(fileNameWithExtension))
            {
                yield return fileNameWithExtension;
            }

            if (!withExtension.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine("GameData", withExtension);
            }
        }
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
                return NormalizeLookupKey(textAsset.name);
            }
        }
        catch
        {
        }

        return string.Empty;
    }
}
