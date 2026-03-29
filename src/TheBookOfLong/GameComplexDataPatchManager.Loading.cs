using System;
using System.IO;
using System.Text.Json;

namespace TheBookOfLong;

internal static partial class GameComplexDataPatchManager
{
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
            string complexDataDirectory = Path.Combine(modDirectory, "ComplexData");
            if (!Directory.Exists(complexDataDirectory))
            {
                continue;
            }

            string[] patchFiles = Directory.GetFiles(complexDataDirectory, "*.json", SearchOption.AllDirectories);
            Array.Sort(patchFiles, StringComparer.OrdinalIgnoreCase);

            for (int patchIndex = 0; patchIndex < patchFiles.Length; patchIndex += 1)
            {
                string patchFilePath = patchFiles[patchIndex];
                if (TryLoadPatchFile(modName, complexDataDirectory, patchFilePath, ++loadOrder, out ComplexJsonPatchFile? patchFile))
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
        string complexDataDirectory,
        string patchFilePath,
        int loadOrder,
        out ComplexJsonPatchFile? patchFile)
    {
        patchFile = null;

        string relativePath = NormalizeLookupKey(Path.GetRelativePath(complexDataDirectory, patchFilePath));
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

        string prefix = "ComplexData" + Path.DirectorySeparatorChar;
        if (!withExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            withExtension = Path.Combine("ComplexData", withExtension);
        }

        return NormalizeLookupKey(withExtension);
    }

    private static string NormalizeLookupKey(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Path.DirectorySeparatorChar, segments);
    }
}
