using MelonLoader;
using MelonLoader.Preferences;

namespace TheBookOfLong;

internal static class ModSettings
{
    private static MelonPreferences_Category? _settingsCategory;
    private static MelonPreferences_Entry<global::UnityEngine.KeyCode>? _toggleKeyEntry;
    private static MelonPreferences_Entry<bool>? _autoOpenOnStartupEntry;
    private static MelonPreferences_Entry<float>? _autoOpenDelaySecondsEntry;

    internal static void Initialize()
    {
        _settingsCategory ??= MelonPreferences.CreateCategory("TheBookOfLong.UI", "界面设置");
        _toggleKeyEntry ??= _settingsCategory.CreateEntry("配置界面开关键", global::UnityEngine.KeyCode.F4);
        _autoOpenOnStartupEntry ??= _settingsCategory.CreateEntry("启动后自动打开配置界面", true);
        _autoOpenDelaySecondsEntry ??= _settingsCategory.CreateEntry(
            "自动打开延迟秒数",
            2.5f,
            validator: new ValueRange<float>(0f, 15f));
    }

    internal static global::UnityEngine.KeyCode GetToggleKey()
    {
        if (_toggleKeyEntry?.BoxedValue is global::UnityEngine.KeyCode keyCode)
        {
            return keyCode;
        }

        return global::UnityEngine.KeyCode.F4;
    }

    internal static string GetToggleKeyLabel()
    {
        return GetToggleKey().ToString();
    }

    internal static bool ShouldAutoOpenOnStartup()
    {
        if (_autoOpenOnStartupEntry?.BoxedValue is bool autoOpen)
        {
            return autoOpen;
        }

        return true;
    }

    internal static float GetAutoOpenDelaySeconds()
    {
        if (_autoOpenDelaySecondsEntry?.BoxedValue is float delaySeconds)
        {
            return global::UnityEngine.Mathf.Clamp(delaySeconds, 0f, 15f);
        }

        return 2.5f;
    }
}
