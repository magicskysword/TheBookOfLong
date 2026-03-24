using System;
using System.Collections.Generic;
using MelonLoader;

namespace TheBookOfLong;

internal sealed partial class MelonPreferencesEditor
{
    private const float WindowWidth = 1120f;
    private const float WindowHeight = 720f;
    private const float MinWindowWidth = 860f;
    private const float MinWindowHeight = 520f;
    private const float WindowPadding = 10f;
    private const float TitleBarHeight = 30f;
    private const float MottoHeight = 18f;
    private const float ToolbarHeight = 36f;
    private const float LeftPaneWidth = 280f;
    private const float PaneSpacing = 8f;
    private const float CategoryItemHeight = 28f;
    private const float EntrySpacing = 8f;
    private const float ButtonHeight = 24f;
    private const float TextFieldHeight = 24f;
    private const float MultiLineTextHeight = 56f;

    private readonly Dictionary<string, EntryTextState> _textStates = new(StringComparer.Ordinal);
    private readonly List<MelonPreferences_Category> _categories = new();
    private readonly List<MelonPreferences_Entry> _entries = new();

    private global::UnityEngine.Rect _windowRect = new(48f, 48f, WindowWidth, WindowHeight);
    private global::UnityEngine.Vector2 _categoryScroll;
    private global::UnityEngine.Vector2 _entryScroll;

    private string _selectedCategoryIdentifier = string.Empty;
    private string _searchText = string.Empty;
    private string _statusMessage = string.Empty;
    private string _capturingKeyEntryKey = string.Empty;
    private float _statusExpiresAt;
    private bool _isVisible;
    private bool _showHidden;
    private bool _isDraggingWindow;
    private global::UnityEngine.Vector2 _dragCursorOffset;
    private float _nextEventSystemRefreshTime;

    private global::UnityEngine.GUIStyle? _mutedLabelStyle;
    private global::UnityEngine.GUIStyle? _wrappedLabelStyle;
    private global::UnityEngine.GUIStyle? _errorLabelStyle;
    private global::UnityEngine.GUIStyle? _titleLabelStyle;
    private global::UnityEngine.GUIStyle? _sectionHeaderStyle;
    private global::UnityEngine.GUIStyle? _centeredLabelStyle;

    internal void OnUpdate()
    {
        RefreshEventSystemBlocking(force: false);

        if (!string.IsNullOrEmpty(_capturingKeyEntryKey))
        {
            return;
        }

        if (!global::UnityEngine.Input.GetKeyDown(ModSettings.GetToggleKey()))
        {
            return;
        }

        SetVisible(!_isVisible);
    }

    internal void Open()
    {
        SetVisible(true);
    }

    internal void OnGUI()
    {
        if (!_isVisible)
        {
            return;
        }

        EnsureStyles();
        EnsureSelectedCategory();
        ClampWindowRectToScreen();

        global::UnityEngine.GUI.depth = -1000;
        global::UnityEngine.Event? current = global::UnityEngine.Event.current;
        if (current is null)
        {
            return;
        }

        HandleWindowDrag(current);
        DrawBackdrop(current);
        DrawWindow(current);
    }

    private void SavePreferences()
    {
        try
        {
            MelonPreferences.Save();
            _textStates.Clear();
            SetStatus("Preferences saved.", 3.0f);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Failed to save Melon preferences: {ex.Message}");
            SetStatus($"Save failed: {ex.Message}", 5.0f);
        }
    }

    private void ReloadPreferences()
    {
        try
        {
            MelonPreferences.Load();
            _capturingKeyEntryKey = string.Empty;
            _textStates.Clear();
            SetStatus("Preferences reloaded from disk.", 3.0f);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"Failed to reload Melon preferences: {ex.Message}");
            SetStatus($"Reload failed: {ex.Message}", 5.0f);
        }
    }

    private void RefreshCategories()
    {
        _categories.Clear();
        if (MelonPreferences.Categories is null)
        {
            return;
        }

        for (int i = 0; i < MelonPreferences.Categories.Count; i += 1)
        {
            MelonPreferences_Category category = MelonPreferences.Categories[i];
            if (!_showHidden && category.IsHidden)
            {
                continue;
            }

            if (CategoryMatchesSearch(category))
            {
                _categories.Add(category);
            }
        }

        _categories.Sort(static (left, right) => CompareDisplayName(left.DisplayName, right.DisplayName, left.Identifier, right.Identifier));
    }

    private void CollectEntries(MelonPreferences_Category category, List<MelonPreferences_Entry> entries)
    {
        entries.Clear();
        if (category.Entries is null)
        {
            return;
        }

        for (int i = 0; i < category.Entries.Count; i += 1)
        {
            MelonPreferences_Entry entry = category.Entries[i];
            if (!_showHidden && entry.IsHidden)
            {
                continue;
            }

            if (EntryMatchesSearch(entry))
            {
                entries.Add(entry);
            }
        }

        entries.Sort(static (left, right) => CompareDisplayName(left.DisplayName, right.DisplayName, left.Identifier, right.Identifier));
    }

    private void EnsureSelectedCategory()
    {
        RefreshCategories();
        if (_categories.Count == 0)
        {
            _selectedCategoryIdentifier = string.Empty;
            return;
        }

        for (int i = 0; i < _categories.Count; i += 1)
        {
            if (string.Equals(_categories[i].Identifier, _selectedCategoryIdentifier, StringComparison.Ordinal))
            {
                return;
            }
        }

        _selectedCategoryIdentifier = _categories[0].Identifier;
        _entryScroll = global::UnityEngine.Vector2.zero;
    }

    private MelonPreferences_Category? GetSelectedCategory()
    {
        for (int i = 0; i < _categories.Count; i += 1)
        {
            if (string.Equals(_categories[i].Identifier, _selectedCategoryIdentifier, StringComparison.Ordinal))
            {
                return _categories[i];
            }
        }

        return null;
    }

    private bool CategoryMatchesSearch(MelonPreferences_Category category)
    {
        if (!HasSearchText())
        {
            return true;
        }

        string search = _searchText.Trim();
        if (ContainsIgnoreCase(category.DisplayName, search) || ContainsIgnoreCase(category.Identifier, search))
        {
            return true;
        }

        if (category.Entries is null)
        {
            return false;
        }

        for (int i = 0; i < category.Entries.Count; i += 1)
        {
            MelonPreferences_Entry entry = category.Entries[i];
            if ((_showHidden || !entry.IsHidden) && EntryMatchesSearch(entry))
            {
                return true;
            }
        }

        return false;
    }

    private bool EntryMatchesSearch(MelonPreferences_Entry entry)
    {
        if (!HasSearchText())
        {
            return true;
        }

        string search = _searchText.Trim();
        return ContainsIgnoreCase(entry.DisplayName, search)
            || ContainsIgnoreCase(entry.Identifier, search)
            || ContainsIgnoreCase(entry.Description, search)
            || ContainsIgnoreCase(entry.Comment, search)
            || ContainsIgnoreCase(entry.GetValueAsString(), search)
            || ContainsIgnoreCase(entry.GetEditedValueAsString(), search);
    }

    private bool HasSearchText()
    {
        return !string.IsNullOrWhiteSpace(_searchText);
    }

    private static bool ContainsIgnoreCase(string? value, string search)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetEntryKey(MelonPreferences_Entry entry)
    {
        return entry.Category.Identifier + "/" + entry.Identifier;
    }

    private void SetVisible(bool visible)
    {
        if (_isVisible == visible)
        {
            return;
        }

        _isVisible = visible;
        _isDraggingWindow = false;
        _capturingKeyEntryKey = string.Empty;
        global::UnityEngine.GUI.FocusControl(string.Empty);

        if (_isVisible)
        {
            EnsureSelectedCategory();
        }

        RefreshEventSystemBlocking(force: true);
    }

    private void SetStatus(string message, float durationSeconds)
    {
        _statusMessage = message;
        _statusExpiresAt = durationSeconds > 0f
            ? global::UnityEngine.Time.realtimeSinceStartup + durationSeconds
            : 0f;
    }

    private void ClampWindowRectToScreen()
    {
        float maxWidth = Math.Max(640f, global::UnityEngine.Screen.width - 24f);
        float maxHeight = Math.Max(420f, global::UnityEngine.Screen.height - 24f);
        float minWidth = Math.Min(MinWindowWidth, maxWidth);
        float minHeight = Math.Min(MinWindowHeight, maxHeight);

        _windowRect.width = global::UnityEngine.Mathf.Clamp(_windowRect.width, minWidth, maxWidth);
        _windowRect.height = global::UnityEngine.Mathf.Clamp(_windowRect.height, minHeight, maxHeight);
        _windowRect.x = global::UnityEngine.Mathf.Clamp(_windowRect.x, 0f, Math.Max(0f, global::UnityEngine.Screen.width - _windowRect.width));
        _windowRect.y = global::UnityEngine.Mathf.Clamp(_windowRect.y, 0f, Math.Max(0f, global::UnityEngine.Screen.height - _windowRect.height));
    }

    private void EnsureStyles()
    {
        if (_mutedLabelStyle is null)
        {
            _mutedLabelStyle = new global::UnityEngine.GUIStyle(global::UnityEngine.GUI.skin.label) { fontSize = 11 };
            _mutedLabelStyle.normal.textColor = new global::UnityEngine.Color(0.78f, 0.78f, 0.78f, 1f);
        }

        if (_wrappedLabelStyle is null)
        {
            _wrappedLabelStyle = new global::UnityEngine.GUIStyle(global::UnityEngine.GUI.skin.label) { wordWrap = true };
        }

        if (_errorLabelStyle is null)
        {
            _errorLabelStyle = new global::UnityEngine.GUIStyle(global::UnityEngine.GUI.skin.label) { wordWrap = true };
            _errorLabelStyle.normal.textColor = new global::UnityEngine.Color(1f, 0.55f, 0.55f, 1f);
        }

        if (_titleLabelStyle is null)
        {
            _titleLabelStyle = new global::UnityEngine.GUIStyle(global::UnityEngine.GUI.skin.label);
        }

        if (_sectionHeaderStyle is null)
        {
            _sectionHeaderStyle = new global::UnityEngine.GUIStyle(global::UnityEngine.GUI.skin.label) { fontSize = 12 };
        }

        if (_centeredLabelStyle is null)
        {
            _centeredLabelStyle = new global::UnityEngine.GUIStyle(global::UnityEngine.GUI.skin.label);
        }

    }

    private static int CompareDisplayName(string? leftDisplayName, string? rightDisplayName, string leftIdentifier, string rightIdentifier)
    {
        string left = string.IsNullOrWhiteSpace(leftDisplayName) ? leftIdentifier : leftDisplayName!;
        string right = string.IsNullOrWhiteSpace(rightDisplayName) ? rightIdentifier : rightDisplayName!;

        int displayCompare = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return displayCompare != 0
            ? displayCompare
            : string.Compare(leftIdentifier, rightIdentifier, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCategoryDisplayName(MelonPreferences_Category category)
    {
        return string.IsNullOrWhiteSpace(category.DisplayName) ? category.Identifier : category.DisplayName;
    }

    private static string GetEntryDisplayName(MelonPreferences_Entry entry)
    {
        return string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Identifier : entry.DisplayName;
    }

    private static global::UnityEngine.Rect ShrinkRect(global::UnityEngine.Rect rect, float padding)
    {
        return new global::UnityEngine.Rect(
            rect.x + padding,
            rect.y + padding,
            Math.Max(0f, rect.width - (padding * 2f)),
            Math.Max(0f, rect.height - (padding * 2f)));
    }

    private sealed class EntryTextState
    {
        public string Text { get; set; } = string.Empty;

        public string LastSyncedText { get; set; } = string.Empty;

        public string ErrorMessage { get; set; } = string.Empty;

        public bool WasFocused { get; set; }
    }
}
