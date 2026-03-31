using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MelonLoader;

namespace TheBookOfLong;

/// <summary>
/// 由龙之书框架维护 Mod 加载配置。
/// 玩家可以编辑 UserData 下的配置文件，但修改后必须完全重启游戏才会生效。
/// </summary>
internal static class ModLoadConfigManager
{
    private const string ConfigFileName = "TheBookOfLong.ModLoadConfig.json";
    private const string ConfigDescription =
        "修改本文件后，需要完全重启游戏才能应用新的 Mod 加载配置。Mods 数组中的顺序就是加载顺序，越靠后覆盖能力越强。";

    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static string _configPath = string.Empty;

    internal static string ConfigPath => _configPath;

    internal static bool Initialize(string gameRoot)
    {
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(_configPath))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                MelonLogger.Warning("Mod load config path is unavailable. Could not resolve game root directory.");
                return false;
            }

            string userDataRoot = Path.Combine(Path.GetFullPath(gameRoot), "UserData");
            Directory.CreateDirectory(userDataRoot);
            _configPath = Path.Combine(userDataRoot, ConfigFileName);
            return true;
        }
    }

    internal static List<ModProject> ApplyLoadConfig(string gameRoot, List<ModProject> discoveredProjects)
    {
        lock (Sync)
        {
            if (!Initialize(gameRoot))
            {
                return discoveredProjects;
            }

            ModLoadConfigFile configFile = LoadConfigFile();
            List<ModProject> orderedProjects = BuildOrderedProjects(discoveredProjects, configFile);
            SaveConfigFile(orderedProjects);
            return orderedProjects;
        }
    }

    private static ModLoadConfigFile LoadConfigFile()
    {
        if (!File.Exists(_configPath))
        {
            return CreateDefaultConfigFile();
        }

        try
        {
            string json = File.ReadAllText(_configPath, Utf8NoBom);
            ModLoadConfigFile? configFile = JsonSerializer.Deserialize<ModLoadConfigFile>(json, ReadJsonOptions);
            return configFile ?? CreateDefaultConfigFile();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Failed to read mod load config '{_configPath}': {ex.Message}");
            return CreateDefaultConfigFile();
        }
    }

    private static ModLoadConfigFile CreateDefaultConfigFile()
    {
        return new ModLoadConfigFile
        {
            FormatVersion = 1,
            Description = ConfigDescription
        };
    }

    private static List<ModProject> BuildOrderedProjects(List<ModProject> discoveredProjects, ModLoadConfigFile configFile)
    {
        Dictionary<string, ModProject> projectsByFolderName = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < discoveredProjects.Count; i += 1)
        {
            ModProject project = discoveredProjects[i];
            projectsByFolderName[project.FolderName] = project;
        }

        List<ModProject> orderedProjects = new();
        HashSet<string> addedFolders = new(StringComparer.OrdinalIgnoreCase);
        List<ModLoadConfigEntry> entries = configFile.Mods ?? new List<ModLoadConfigEntry>();

        for (int i = 0; i < entries.Count; i += 1)
        {
            string folderName = entries[i].FolderName.Trim();
            if (string.IsNullOrWhiteSpace(folderName))
            {
                continue;
            }

            if (!projectsByFolderName.TryGetValue(folderName, out ModProject? project) || !addedFolders.Add(folderName))
            {
                continue;
            }

            project.IsEnabled = entries[i].Enabled;
            project.LoadOrder = orderedProjects.Count;
            orderedProjects.Add(project);
        }

        List<ModProject> remainingProjects = new();
        for (int i = 0; i < discoveredProjects.Count; i += 1)
        {
            ModProject project = discoveredProjects[i];
            if (!addedFolders.Contains(project.FolderName))
            {
                remainingProjects.Add(project);
            }
        }

        remainingProjects.Sort(static (left, right) =>
            string.Compare(left.FolderName, right.FolderName, StringComparison.OrdinalIgnoreCase));

        for (int i = 0; i < remainingProjects.Count; i += 1)
        {
            ModProject project = remainingProjects[i];
            project.IsEnabled = true;
            project.LoadOrder = orderedProjects.Count;
            orderedProjects.Add(project);
        }

        return orderedProjects;
    }

    private static void SaveConfigFile(IReadOnlyList<ModProject> orderedProjects)
    {
        ModLoadConfigFile configFile = CreateDefaultConfigFile();
        for (int i = 0; i < orderedProjects.Count; i += 1)
        {
            ModProject project = orderedProjects[i];
            configFile.Mods.Add(new ModLoadConfigEntry
            {
                FolderName = project.FolderName,
                DisplayName = project.DisplayName,
                Version = project.Version,
                Enabled = project.IsEnabled
            });
        }

        string json = JsonSerializer.Serialize(configFile, WriteJsonOptions);
        File.WriteAllText(_configPath, json, Utf8NoBom);
    }
}
