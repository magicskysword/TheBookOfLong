using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using MelonLoader;

namespace TheBookOfLong;

/// <summary>
/// 统一扫描 ModsOfLong，并把每个 mod 文件夹整理成 IModProject。
/// 后续 CSV、ComplexData 等补丁系统都只从这里取项目数据。
/// </summary>
internal static class ModProjectRegistry
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly List<ModProject> AllProjects = new();
    private static readonly List<IModProject> EnabledProjects = new();

    private static string _gameRoot = string.Empty;
    private static string _modsRoot = string.Empty;
    private static string _modsOfLongRoot = string.Empty;
    private static bool _isInitialized;

    internal static string GameRoot => _gameRoot;

    internal static string ModsRoot => _modsRoot;

    internal static string ModsOfLongRoot => _modsOfLongRoot;

    internal static bool Initialize()
    {
        lock (Sync)
        {
            if (_isInitialized)
            {
                return true;
            }

            if (!EnsurePaths())
            {
                return false;
            }

            ReloadProjects();
            _isInitialized = true;
            return true;
        }
    }

    internal static IReadOnlyList<IModProject> GetEnabledProjectsSnapshot()
    {
        lock (Sync)
        {
            return EnabledProjects.ToArray();
        }
    }

    private static bool EnsurePaths()
    {
        if (!string.IsNullOrWhiteSpace(_modsOfLongRoot))
        {
            return true;
        }

        string? modsRoot = ResolveModsRoot();
        if (string.IsNullOrWhiteSpace(modsRoot))
        {
            MelonLogger.Warning("ModsOfLong path is unavailable. Could not resolve Mods directory.");
            return false;
        }

        _modsRoot = Path.GetFullPath(modsRoot);
        _gameRoot = ResolveGameRoot() ?? Directory.GetParent(_modsRoot)?.FullName ?? string.Empty;
        _modsOfLongRoot = Path.Combine(_modsRoot, "ModsOfLong");
        Directory.CreateDirectory(_modsOfLongRoot);
        return true;
    }

    private static void ReloadProjects()
    {
        AllProjects.Clear();
        EnabledProjects.Clear();

        string[] modDirectories = Directory.GetDirectories(_modsOfLongRoot, "mod*", SearchOption.TopDirectoryOnly);
        Array.Sort(modDirectories, StringComparer.OrdinalIgnoreCase);

        List<ModProject> discoveredProjects = new();
        for (int i = 0; i < modDirectories.Length; i += 1)
        {
            discoveredProjects.Add(LoadProject(modDirectories[i]));
        }

        AllProjects.AddRange(ModLoadConfigManager.ApplyLoadConfig(_gameRoot, discoveredProjects));

        for (int i = 0; i < AllProjects.Count; i += 1)
        {
            ModProject project = AllProjects[i];
            if (project.IsEnabled)
            {
                EnabledProjects.Add(project);
            }
        }

        int disabledCount = AllProjects.Count - EnabledProjects.Count;
        MelonLogger.Msg(
            $"ModsOfLong registry ready: '{_modsOfLongRoot}'. Found {AllProjects.Count} mod project(s), enabled {EnabledProjects.Count}, disabled {disabledCount}.");
        MelonLogger.Msg($"Mod load config: '{ModLoadConfigManager.ConfigPath}'. Edit it and restart the game to apply changes.");
    }

    private static ModProject LoadProject(string modDirectory)
    {
        string folderName = Path.GetFileName(modDirectory);
        ModProjectInfoFile? info = ReadInfoFile(modDirectory);

        string displayName = ResolveDisplayName(folderName, info?.Name);
        string version = string.IsNullOrWhiteSpace(info?.Version) ? "unspecified" : info!.Version!.Trim();

        string dataDirectory = Path.Combine(modDirectory, "Data");
        string complexDataDirectory = Path.Combine(modDirectory, "ComplexData");

        return new ModProject(
            folderName,
            displayName,
            version,
            modDirectory,
            dataDirectory,
            complexDataDirectory,
            EnumeratePatchFiles(dataDirectory, "*.csv"),
            EnumeratePatchFiles(complexDataDirectory, "*.json"));
    }

    private static ModProjectInfoFile? ReadInfoFile(string modDirectory)
    {
        string infoFilePath = Path.Combine(modDirectory, "Info.json");
        if (!File.Exists(infoFilePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(infoFilePath, Utf8NoBom);
            return JsonSerializer.Deserialize<ModProjectInfoFile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Failed to read mod info '{infoFilePath}': {ex.Message}");
            return null;
        }
    }

    private static string ResolveDisplayName(string folderName, string? configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            return configuredName.Trim();
        }

        string fallbackName = folderName.StartsWith("mod", StringComparison.OrdinalIgnoreCase)
            ? folderName.Substring(3).TrimStart(' ', '_', '-')
            : folderName;

        return string.IsNullOrWhiteSpace(fallbackName) ? folderName : fallbackName;
    }

    private static string[] EnumeratePatchFiles(string directoryPath, string searchPattern)
    {
        if (!Directory.Exists(directoryPath))
        {
            return Array.Empty<string>();
        }

        string[] files = Directory.GetFiles(directoryPath, searchPattern, SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
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
}
