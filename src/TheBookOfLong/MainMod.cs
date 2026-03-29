using System;
using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(TheBookOfLong.MainMod), "TheBookOfLong", "0.1.0", "skysw")]
[assembly: MelonGame("TppStudio", "LongYinLiZhiZhuan")]

namespace TheBookOfLong;

public sealed class MainMod : MelonMod
{
    private HarmonyLib.Harmony? _harmony;
    private readonly MelonPreferencesEditor _preferencesEditor = new();
    private bool _pendingAutoOpen;
    private DateTime _autoOpenAtUtc;

    public override void OnInitializeMelon()
    {
        ModSettings.Initialize();
        ConfigDumpManager.Initialize();
        GameComplexDataDumpManager.Initialize();
        DataModManager.Initialize();

        _harmony = new HarmonyLib.Harmony("TheBookOfLong.ConfigDump");
        _harmony.PatchAll(typeof(MainMod).Assembly);

        if (ModSettings.ShouldAutoOpenOnStartup())
        {
            _pendingAutoOpen = true;
            _autoOpenAtUtc = DateTime.UtcNow.AddSeconds(ModSettings.GetAutoOpenDelaySeconds());
        }

        MelonLogger.Msg($"TheBookOfLong loaded. Config dump root: {ConfigDumpManager.DumpRoot}. Data mods root: {DataModManager.ModsOfLongRoot}");
    }

    public override void OnUpdate()
    {
        if (_pendingAutoOpen && DateTime.UtcNow >= _autoOpenAtUtc)
        {
            _pendingAutoOpen = false;
            _preferencesEditor.Open();
        }

        _preferencesEditor.OnUpdate();
    }

    public override void OnGUI()
    {
        _preferencesEditor.OnGUI();
    }
}
