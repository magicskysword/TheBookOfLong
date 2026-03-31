using System;
using MelonLoader;

namespace TheBookOfLong;

internal sealed partial class MelonPreferencesEditor
{
    private void DrawBackdrop(global::UnityEngine.Event current)
    {
        global::UnityEngine.Rect backdropRect = new(0f, 0f, global::UnityEngine.Screen.width, global::UnityEngine.Screen.height);
        global::UnityEngine.Color previousColor = global::UnityEngine.GUI.color;
        global::UnityEngine.GUI.color = new global::UnityEngine.Color(0f, 0f, 0f, 0.55f);
        global::UnityEngine.GUI.DrawTexture(backdropRect, global::UnityEngine.Texture2D.whiteTexture);
        global::UnityEngine.GUI.color = previousColor;

        if ((current.type == global::UnityEngine.EventType.MouseDown
                || current.type == global::UnityEngine.EventType.MouseUp
                || current.type == global::UnityEngine.EventType.ScrollWheel)
            && !_windowRect.Contains(current.mousePosition))
        {
            if (current.type == global::UnityEngine.EventType.MouseDown)
            {
                global::UnityEngine.GUI.FocusControl(string.Empty);
            }

            current.Use();
        }
    }

    private void HandleWindowDrag(global::UnityEngine.Event current)
    {
        global::UnityEngine.Rect titleRect = new(_windowRect.x, _windowRect.y, _windowRect.width, TitleBarHeight);
        global::UnityEngine.Rect closeRect = new(titleRect.xMax - 30f, titleRect.y + 4f, 24f, 22f);

        switch (current.type)
        {
            case global::UnityEngine.EventType.MouseDown:
                if (titleRect.Contains(current.mousePosition) && !closeRect.Contains(current.mousePosition))
                {
                    _isDraggingWindow = true;
                    _dragCursorOffset = current.mousePosition - new global::UnityEngine.Vector2(_windowRect.x, _windowRect.y);
                    current.Use();
                }

                break;
            case global::UnityEngine.EventType.MouseDrag:
                if (_isDraggingWindow)
                {
                    _windowRect.x = current.mousePosition.x - _dragCursorOffset.x;
                    _windowRect.y = current.mousePosition.y - _dragCursorOffset.y;
                    ClampWindowRectToScreen();
                    current.Use();
                }

                break;
            case global::UnityEngine.EventType.MouseUp:
                if (_isDraggingWindow)
                {
                    _isDraggingWindow = false;
                    current.Use();
                }

                break;
        }
    }

    private void DrawWindow(global::UnityEngine.Event current)
    {
        global::UnityEngine.GUI.Box(_windowRect, global::UnityEngine.GUIContent.none);

        global::UnityEngine.Rect titleRect = new(_windowRect.x, _windowRect.y, _windowRect.width, TitleBarHeight);
        global::UnityEngine.Rect toolbarRect = new(
            _windowRect.x + WindowPadding,
            titleRect.yMax + MottoHeight + WindowPadding,
            _windowRect.width - (WindowPadding * 2f),
            ToolbarHeight);
        global::UnityEngine.Rect mottoRect = new(
            _windowRect.x + WindowPadding,
            titleRect.yMax + 2f,
            _windowRect.width - (WindowPadding * 2f),
            MottoHeight);
        global::UnityEngine.Rect bodyRect = new(
            _windowRect.x + WindowPadding,
            toolbarRect.yMax + WindowPadding,
            _windowRect.width - (WindowPadding * 2f),
            _windowRect.height - TitleBarHeight - MottoHeight - ToolbarHeight - (WindowPadding * 3f));
        global::UnityEngine.Rect categoryRect = new(bodyRect.x, bodyRect.y, LeftPaneWidth, bodyRect.height);
        global::UnityEngine.Rect entriesRect = new(
            categoryRect.xMax + PaneSpacing,
            bodyRect.y,
            bodyRect.width - LeftPaneWidth - PaneSpacing,
            bodyRect.height);

        DrawTitleBar(titleRect);
        DrawMotto(mottoRect);
        DrawToolbar(toolbarRect);
        DrawCategoryPane(categoryRect);
        DrawEntriesPane(entriesRect, current);
    }

    private void DrawTitleBar(global::UnityEngine.Rect rect)
    {
        global::UnityEngine.GUI.Box(rect, "龙之书 - Mod配置编辑器");

        global::UnityEngine.Rect closeRect = new(rect.xMax - 30f, rect.y + 4f, 24f, 22f);
        if (global::UnityEngine.GUI.Button(closeRect, "X"))
        {
            SetVisible(false);
        }
    }

    private void DrawMotto(global::UnityEngine.Rect rect)
    {
        DrawCenteredLabel(rect, "龙，可是帝王之征啊！", _mutedLabelStyle!);
    }

    private void DrawToolbar(global::UnityEngine.Rect rect)
    {
        global::UnityEngine.GUI.Box(rect, global::UnityEngine.GUIContent.none);

        global::UnityEngine.Rect inner = ShrinkRect(rect, 8f);
        float rowHeight = 24f;

        global::UnityEngine.Rect searchLabelRect = new(inner.x, inner.y, 36f, rowHeight);
        global::UnityEngine.Rect searchRect = new(searchLabelRect.xMax + 4f, inner.y, 260f, rowHeight);
        global::UnityEngine.Rect clearRect = new(searchRect.xMax + 6f, inner.y, 56f, rowHeight);
        global::UnityEngine.Rect hiddenRect = new(clearRect.xMax + 8f, inner.y, 120f, rowHeight);
        global::UnityEngine.Rect hintRect = new(rect.xMax - 180f, inner.y, 160f, rowHeight);

        global::UnityEngine.GUI.Label(searchLabelRect, "搜索");
        _searchText = global::UnityEngine.GUI.TextField(searchRect, _searchText ?? string.Empty);

        if (global::UnityEngine.GUI.Button(clearRect, "清空"))
        {
            _searchText = string.Empty;
            EnsureSelectedCategory();
        }

        bool newShowHidden = global::UnityEngine.GUI.Toggle(hiddenRect, _showHidden, "显示隐藏项");
        if (newShowHidden != _showHidden)
        {
            _showHidden = newShowHidden;
            EnsureSelectedCategory();
        }

        if (!string.IsNullOrWhiteSpace(_statusMessage)
            && (_statusExpiresAt <= 0f || global::UnityEngine.Time.realtimeSinceStartup <= _statusExpiresAt))
        {
            global::UnityEngine.GUI.Label(hintRect, _statusMessage, _errorLabelStyle!);
            return;
        }

        DrawCenteredLabel(hintRect, $"{ModSettings.GetToggleKeyLabel()} 开关", _centeredLabelStyle!);
    }

    private void DrawCategoryPane(global::UnityEngine.Rect rect)
    {
        RefreshCategories();
        global::UnityEngine.GUI.Box(rect, global::UnityEngine.GUIContent.none);

        global::UnityEngine.Rect headerRect = new(rect.x + 8f, rect.y + 8f, rect.width - 16f, 18f);
        global::UnityEngine.GUI.Label(headerRect, "分类", _sectionHeaderStyle!);

        global::UnityEngine.Rect scrollRect = new(rect.x + 8f, headerRect.yMax + 6f, rect.width - 16f, rect.height - 40f);
        float contentHeight = Math.Max(scrollRect.height - 1f, (_categories.Count * CategoryItemHeight) + 4f);
        global::UnityEngine.Rect contentRect = new(0f, 0f, scrollRect.width - 18f, contentHeight);

        _categoryScroll = global::UnityEngine.GUI.BeginScrollView(scrollRect, _categoryScroll, contentRect);
        for (int i = 0; i < _categories.Count; i += 1)
        {
            MelonPreferences_Category category = _categories[i];
            string label = GetCategoryDisplayName(category);
            if (category.IsHidden)
            {
                label += " [隐藏]";
            }

            bool isSelected = string.Equals(_selectedCategoryIdentifier, category.Identifier, StringComparison.Ordinal);
            global::UnityEngine.Rect itemRect = new(2f, 2f + (i * CategoryItemHeight), contentRect.width - 4f, CategoryItemHeight - 4f);

            if (isSelected)
            {
                global::UnityEngine.Color previousColor = global::UnityEngine.GUI.color;
                global::UnityEngine.GUI.color = new global::UnityEngine.Color(0.16f, 0.32f, 0.18f, 1f);
                global::UnityEngine.GUI.DrawTexture(itemRect, global::UnityEngine.Texture2D.whiteTexture);
                global::UnityEngine.GUI.color = previousColor;
            }

            if (global::UnityEngine.GUI.Button(itemRect, label))
            {
                _selectedCategoryIdentifier = category.Identifier;
                _entryScroll = global::UnityEngine.Vector2.zero;
                global::UnityEngine.GUI.FocusControl(string.Empty);
            }
        }

        global::UnityEngine.GUI.EndScrollView();
    }

    private void DrawEntriesPane(global::UnityEngine.Rect rect, global::UnityEngine.Event current)
    {
        global::UnityEngine.GUI.Box(rect, global::UnityEngine.GUIContent.none);

        MelonPreferences_Category? category = GetSelectedCategory();
        if (category is null)
        {
            global::UnityEngine.GUI.Label(
                new global::UnityEngine.Rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, 24f),
                "没有可显示的分类。");
            return;
        }

        CollectEntries(category, _entries);

        global::UnityEngine.Rect titleRect = new(rect.x + 8f, rect.y + 8f, rect.width - 16f, 20f);
        global::UnityEngine.Rect scrollRect = new(rect.x + 8f, titleRect.yMax + 8f, rect.width - 16f, rect.height - 36f);

        global::UnityEngine.GUI.Label(titleRect, GetCategoryDisplayName(category), _sectionHeaderStyle!);

        float contentWidth = Math.Max(100f, scrollRect.width - 20f);
        float totalHeight = 4f;
        for (int i = 0; i < _entries.Count; i += 1)
        {
            totalHeight += MeasureEntryHeight(_entries[i], contentWidth) + EntrySpacing;
        }

        if (_entries.Count == 0)
        {
            totalHeight = Math.Max(totalHeight, 36f);
        }

        global::UnityEngine.Rect contentRect = new(0f, 0f, contentWidth, Math.Max(scrollRect.height - 1f, totalHeight));
        _entryScroll = global::UnityEngine.GUI.BeginScrollView(scrollRect, _entryScroll, contentRect);

        float y = 4f;
        for (int i = 0; i < _entries.Count; i += 1)
        {
            MelonPreferences_Entry entry = _entries[i];
            float entryHeight = MeasureEntryHeight(entry, contentWidth);
            DrawEntry(entry, new global::UnityEngine.Rect(0f, y, contentWidth, entryHeight), current);
            y += entryHeight + EntrySpacing;
        }

        if (_entries.Count == 0)
        {
            global::UnityEngine.GUI.Label(new global::UnityEngine.Rect(4f, 6f, contentWidth - 8f, 24f), "当前分类没有可显示的设置。", _mutedLabelStyle!);
        }

        global::UnityEngine.GUI.EndScrollView();
    }

    private static void DrawCenteredLabel(global::UnityEngine.Rect rect, string text, global::UnityEngine.GUIStyle style)
    {
        global::UnityEngine.GUIContent content = new(text);
        global::UnityEngine.Vector2 size = style.CalcSize(content);
        float labelWidth = Math.Min(rect.width, size.x);
        float labelHeight = Math.Min(rect.height, Math.Max(size.y, 16f));
        global::UnityEngine.Rect labelRect = new(
            rect.x + ((rect.width - labelWidth) * 0.5f),
            rect.y + ((rect.height - labelHeight) * 0.5f),
            labelWidth,
            labelHeight);

        global::UnityEngine.GUI.Label(labelRect, content, style);
    }
}
