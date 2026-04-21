using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TheBookOfLong;

internal static partial class GameComplexDataPatchManager
{
    private static bool EnsureInitialized()
    {
        return ModProjectRegistry.Initialize();
    }

    private static void LoadPatchFiles()
    {
        LoadedPatchFiles.Clear();

        IReadOnlyList<IModProject> modProjects = ModProjectRegistry.GetEnabledProjectsSnapshot();
        int loadOrder = 0;
        for (int modIndex = 0; modIndex < modProjects.Count; modIndex += 1)
        {
            IModProject modProject = modProjects[modIndex];
            for (int patchIndex = 0; patchIndex < modProject.ComplexDataPatchFiles.Count; patchIndex += 1)
            {
                string patchFilePath = modProject.ComplexDataPatchFiles[patchIndex];
                if (TryLoadPatchFile(modProject, patchFilePath, ++loadOrder, out ComplexJsonPatchFile? patchFile))
                {
                    LoadedPatchFiles.Add(patchFile!);
                }
            }
        }

        if (LoadedPatchFiles.Count > 0)
        {
            MelonLoader.MelonLogger.Msg(
                $"Game complex data patches ready: '{ModProjectRegistry.ModsOfLongRoot}'. Loaded {LoadedPatchFiles.Count} JSON patch file(s) from {modProjects.Count} enabled mod(s).");
        }
    }

    private static bool TryLoadPatchFile(
        IModProject modProject,
        string patchFilePath,
        int loadOrder,
        out ComplexJsonPatchFile? patchFile)
    {
        patchFile = null;

        string relativePath = NormalizeLookupKey(Path.GetRelativePath(modProject.ComplexDataDirectory, patchFilePath));
        string canonicalRelativePath = BuildCanonicalComplexDataPath(relativePath);
        string fileName = Path.GetFileName(canonicalRelativePath);

        if (!TargetDefinitionsByFileName.TryGetValue(fileName, out ComplexPatchTargetDefinition? targetDefinition))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(patchFilePath, Utf8NoBom);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement rootElement = document.RootElement.Clone();

            if (targetDefinition.PatchTargetKind == ComplexPatchTargetKind.ArrayByName && rootElement.ValueKind != JsonValueKind.Array)
            {
                MelonLoader.MelonLogger.Warning($"Skipped complex data patch '{patchFilePath}' because the root JSON value is not an array.");
                return false;
            }

            if (targetDefinition.PatchTargetKind == ComplexPatchTargetKind.ObjectReplace && rootElement.ValueKind != JsonValueKind.Object)
            {
                MelonLoader.MelonLogger.Warning($"Skipped complex data patch '{patchFilePath}' because the root JSON value is not an object.");
                return false;
            }

            patchFile = new ComplexJsonPatchFile
            {
                ModName = modProject.DisplayName,
                FullPath = patchFilePath,
                RelativePath = canonicalRelativePath,
                LoadOrder = loadOrder,
                RootElement = rootElement,
                Target = targetDefinition
            };

            RegisterSymbolicFieldReferences(patchFile);
            MelonLoader.MelonLogger.Msg(
                $"Loaded complex data patch '{modProject.DisplayName}' (v{modProject.Version}, order {modProject.LoadOrder}): '{patchFile.RelativePath}'");
            return true;
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"Failed to load complex data patch file '{patchFilePath}': {ex.Message}");
            return false;
        }
    }

    private static void RegisterSymbolicFieldReferences(ComplexJsonPatchFile patchFile)
    {
        RegisterSymbolicFieldReferencesRecursive(patchFile.RootElement, patchFile, "$");
    }

    private static void RegisterSymbolicFieldReferencesRecursive(JsonElement element, ComplexJsonPatchFile patchFile, string jsonPath)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string childPath = $"{jsonPath}.{property.Name}";
                    ComplexSymbolicFieldRules.RegisterReferencesForJsonProperty(property, patchFile, childPath);
                    RegisterSymbolicFieldReferencesRecursive(property.Value, patchFile, childPath);
                }

                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement childElement in element.EnumerateArray())
                {
                    RegisterSymbolicFieldReferencesRecursive(childElement, patchFile, $"{jsonPath}[{index}]");
                    index += 1;
                }

                break;
        }
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
