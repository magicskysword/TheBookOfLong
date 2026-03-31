using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MelonLoader;

namespace TheBookOfLong;

internal static partial class DataModManager
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Dictionary<int, string> ResourcePathsByInstanceId = new();
    private static readonly Dictionary<string, List<CsvPatchFile>> CsvPatchesByLookupKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> PatchedContentCache = new(StringComparer.Ordinal);

    private static string _gameRoot = string.Empty;
    private static int _patchFileOrder;

    internal static string ModsOfLongRoot => ModProjectRegistry.ModsOfLongRoot;

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
            if (matchingPatches.Count == 0 || !CsvPatchApplier.LooksLikeCsvText(text))
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
                if (!CsvPatchApplier.TryMergePatch(
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

            if (!CsvPatchApplier.TryFinalizePatchedCsv(currentContent, sourcePath, out string finalizedContent, out List<string> finalizationWarnings))
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
        if (!string.IsNullOrWhiteSpace(_gameRoot))
        {
            return true;
        }

        if (!ModProjectRegistry.Initialize())
        {
            return false;
        }

        _gameRoot = ModProjectRegistry.GameRoot;
        return true;
    }

    private static void ReloadDataMods()
    {
        CsvPatchesByLookupKey.Clear();
        PatchedContentCache.Clear();
        _patchFileOrder = 0;
        SymbolicIdService.Reset(_gameRoot);

        IReadOnlyList<IModProject> modProjects = ModProjectRegistry.GetEnabledProjectsSnapshot();

        List<CsvPatchFile> csvPatchFiles = new();
        int totalPatchFiles = 0;
        for (int i = 0; i < modProjects.Count; i += 1)
        {
            totalPatchFiles += LoadDataMod(modProjects[i], csvPatchFiles);
        }

        SymbolicIdService.PrepareCsvPatches(csvPatchFiles);
        for (int i = 0; i < csvPatchFiles.Count; i += 1)
        {
            RegisterCsvPatch(csvPatchFiles[i]);
        }

        SymbolicFieldManager.WriteReport();

        MelonLogger.Msg($"ModsOfLong ready: '{ModsOfLongRoot}'. Loaded {modProjects.Count} enabled data mod(s), {totalPatchFiles} CSV patch file(s).");
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

    private sealed class FilePatchResult
    {
        public string ModName { get; set; } = string.Empty;

        public string RelativePath { get; set; } = string.Empty;

        public int AddedRowCount { get; set; }

        public int ModifiedRowCount { get; set; }
    }
}
