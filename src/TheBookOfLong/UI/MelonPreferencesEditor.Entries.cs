using System;
using System.Globalization;
using MelonLoader;

namespace TheBookOfLong;

internal sealed partial class MelonPreferencesEditor
{
    private float MeasureEntryHeight(MelonPreferences_Entry entry, float width)
    {
        Type valueType = ResolveEntryType(entry);
        EnsureEntryValueInitialized(entry, valueType);
        EntryTextState state = SyncTextState(entry, valueType, force: false);

        float innerWidth = Math.Max(100f, width - 16f);
        float height = 8f + 24f;

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            height += _wrappedLabelStyle!.CalcHeight(new global::UnityEngine.GUIContent(entry.Description), innerWidth) + 4f;
        }

        height += MeasureEditorHeight(entry, valueType, state) + 6f;

        if (!string.IsNullOrWhiteSpace(state.ErrorMessage))
        {
            height += _errorLabelStyle!.CalcHeight(new global::UnityEngine.GUIContent(state.ErrorMessage), innerWidth) + 4f;
        }

        return height + 8f;
    }

    private float MeasureEditorHeight(MelonPreferences_Entry entry, Type valueType, EntryTextState state)
    {
        if (valueType == typeof(bool) || valueType == typeof(global::UnityEngine.KeyCode) || valueType.IsEnum)
        {
            return ButtonHeight;
        }

        if (!IsTextEditableType(valueType))
        {
            return TextFieldHeight;
        }

        bool useTextArea = state.Text.IndexOf('\n') >= 0 || state.Text.Length > 120 || valueType == typeof(string);
        float height = useTextArea ? MultiLineTextHeight : TextFieldHeight;

        if (TryGetNumericSliderRange(entry, valueType, out _, out _))
        {
            height += 24f;
        }

        return height;
    }

    private void DrawEntry(MelonPreferences_Entry entry, global::UnityEngine.Rect rect, global::UnityEngine.Event current)
    {
        Type valueType = ResolveEntryType(entry);
        EnsureEntryValueInitialized(entry, valueType);
        EntryTextState state = SyncTextState(entry, valueType, force: false);

        global::UnityEngine.GUI.Box(rect, global::UnityEngine.GUIContent.none);
        global::UnityEngine.Rect inner = ShrinkRect(rect, 8f);
        float y = inner.y;

        global::UnityEngine.Rect nameRect = new(inner.x, y, Math.Max(100f, inner.width - 70f), 20f);
        global::UnityEngine.Rect resetRect = new(inner.xMax - 58f, y - 2f, 58f, 24f);

        global::UnityEngine.GUI.Label(nameRect, GetEntryDisplayName(entry), _titleLabelStyle!);

        if (global::UnityEngine.GUI.Button(resetRect, "重置"))
        {
            if (TryResetEntry(entry, valueType, out string? errorMessage))
            {
                SyncTextState(entry, valueType, force: true);
            }
            else
            {
                state.ErrorMessage = errorMessage ?? "重置失败。";
                SetStatus(state.ErrorMessage, 4.0f);
            }
        }

        y += 24f;

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            float descriptionHeight = _wrappedLabelStyle!.CalcHeight(new global::UnityEngine.GUIContent(entry.Description), inner.width);
            global::UnityEngine.GUI.Label(new global::UnityEngine.Rect(inner.x, y, inner.width, descriptionHeight), entry.Description, _wrappedLabelStyle!);
            y += descriptionHeight + 4f;
        }

        global::UnityEngine.Rect editorRect = new(inner.x, y, inner.width, MeasureEditorHeight(entry, valueType, state));
        DrawEntryEditor(entry, valueType, state, editorRect, current);
        y += editorRect.height + 6f;

        if (!string.IsNullOrWhiteSpace(state.ErrorMessage))
        {
            float errorHeight = _errorLabelStyle!.CalcHeight(new global::UnityEngine.GUIContent(state.ErrorMessage), inner.width);
            global::UnityEngine.GUI.Label(new global::UnityEngine.Rect(inner.x, y, inner.width, errorHeight), state.ErrorMessage, _errorLabelStyle!);
            y += errorHeight + 4f;
        }
    }

    private void DrawEntryEditor(
        MelonPreferences_Entry entry,
        Type valueType,
        EntryTextState state,
        global::UnityEngine.Rect rect,
        global::UnityEngine.Event current)
    {
        if (valueType == typeof(bool))
        {
            DrawBooleanEntry(entry, rect);
            return;
        }

        if (valueType == typeof(global::UnityEngine.KeyCode))
        {
            DrawKeyCodeEntry(entry, rect, current);
            return;
        }

        if (valueType.IsEnum)
        {
            DrawEnumEntry(entry, valueType, rect);
            return;
        }

        if (IsTextEditableType(valueType))
        {
            DrawTextEntry(entry, valueType, state, rect, current);
            return;
        }

        DrawReadOnlyEntry(FormatValue(entry.BoxedEditedValue, valueType), rect);
    }

    private void DrawBooleanEntry(MelonPreferences_Entry entry, global::UnityEngine.Rect rect)
    {
        bool currentValue = entry.BoxedEditedValue is bool boolValue && boolValue;
        bool newValue = global::UnityEngine.GUI.Toggle(rect, currentValue, currentValue ? "开启" : "关闭");
        if (newValue != currentValue)
        {
            if (!TryApplyEntryValue(entry, newValue, typeof(bool), out string? errorMessage))
            {
                SetStatus(errorMessage ?? "保存失败。", 4.0f);
            }
        }
    }

    private void DrawKeyCodeEntry(MelonPreferences_Entry entry, global::UnityEngine.Rect rect, global::UnityEngine.Event current)
    {
        string entryKey = GetEntryKey(entry);
        bool isCapturing = string.Equals(_capturingKeyEntryKey, entryKey, StringComparison.Ordinal);

        if (isCapturing && current.type == global::UnityEngine.EventType.KeyDown)
        {
            if (current.keyCode == global::UnityEngine.KeyCode.Escape)
            {
                _capturingKeyEntryKey = string.Empty;
            }
            else if (current.keyCode != global::UnityEngine.KeyCode.None)
            {
                if (TryApplyEntryValue(entry, current.keyCode, typeof(global::UnityEngine.KeyCode), out string? errorMessage))
                {
                    _capturingKeyEntryKey = string.Empty;
                }
                else
                {
                    SetStatus(errorMessage ?? "保存失败。", 4.0f);
                }
            }

            current.Use();
        }

        string buttonLabel = isCapturing
            ? "请按下按键，Esc 取消"
            : FormatValue(entry.BoxedEditedValue, typeof(global::UnityEngine.KeyCode));

        if (global::UnityEngine.GUI.Button(new global::UnityEngine.Rect(rect.x, rect.y, rect.width, ButtonHeight), buttonLabel))
        {
            _capturingKeyEntryKey = isCapturing ? string.Empty : entryKey;
            global::UnityEngine.GUI.FocusControl(string.Empty);
        }
    }

    private void DrawEnumEntry(MelonPreferences_Entry entry, Type valueType, global::UnityEngine.Rect rect)
    {
        Array values = Enum.GetValues(valueType);
        if (values.Length == 0)
        {
            DrawReadOnlyEntry(FormatValue(entry.BoxedEditedValue, valueType), rect);
            return;
        }

        object? currentValue = entry.BoxedEditedValue;
        int currentIndex = 0;
        for (int i = 0; i < values.Length; i += 1)
        {
            if (Equals(values.GetValue(i), currentValue))
            {
                currentIndex = i;
                break;
            }
        }

        int newIndex = currentIndex;
        global::UnityEngine.Rect prevRect = new(rect.x, rect.y, 28f, ButtonHeight);
        global::UnityEngine.Rect valueRect = new(prevRect.xMax + 6f, rect.y, rect.width - 68f, ButtonHeight);
        global::UnityEngine.Rect nextRect = new(rect.xMax - 28f, rect.y, 28f, ButtonHeight);

        if (global::UnityEngine.GUI.Button(prevRect, "<"))
        {
            newIndex = (currentIndex - 1 + values.Length) % values.Length;
        }

        global::UnityEngine.GUI.Box(valueRect, values.GetValue(currentIndex)?.ToString() ?? string.Empty);

        if (global::UnityEngine.GUI.Button(nextRect, ">"))
        {
            newIndex = (currentIndex + 1) % values.Length;
        }

        if (newIndex != currentIndex)
        {
            if (!TryApplyEntryValue(entry, values.GetValue(newIndex), valueType, out string? errorMessage))
            {
                SetStatus(errorMessage ?? "保存失败。", 4.0f);
            }
        }
    }

    private void DrawTextEntry(
        MelonPreferences_Entry entry,
        Type valueType,
        EntryTextState state,
        global::UnityEngine.Rect rect,
        global::UnityEngine.Event current)
    {
        bool useTextArea = state.Text.IndexOf('\n') >= 0 || state.Text.Length > 120 || valueType == typeof(string);
        float editorHeight = useTextArea ? MultiLineTextHeight : TextFieldHeight;
        string controlName = "EntryInput." + GetEntryKey(entry);
        global::UnityEngine.Rect inputRect = new(rect.x, rect.y, rect.width, editorHeight);

        global::UnityEngine.GUI.SetNextControlName(controlName);
        state.Text = useTextArea
            ? global::UnityEngine.GUI.TextArea(inputRect, state.Text)
            : global::UnityEngine.GUI.TextField(inputRect, state.Text);

        bool isFocused = string.Equals(global::UnityEngine.GUI.GetNameOfFocusedControl(), controlName, StringComparison.Ordinal);
        bool hasPendingChanges = !string.Equals(state.Text, state.LastSyncedText, StringComparison.Ordinal);
        bool submitOnEnter = isFocused
            && !useTextArea
            && current.type == global::UnityEngine.EventType.KeyDown
            && (current.keyCode == global::UnityEngine.KeyCode.Return || current.keyCode == global::UnityEngine.KeyCode.KeypadEnter);
        bool submitOnBlur = state.WasFocused && !isFocused && hasPendingChanges;

        if (submitOnEnter)
        {
            ApplyTextEntry(entry, valueType, state);
            current.Use();
            global::UnityEngine.GUI.FocusControl(string.Empty);
            isFocused = false;
        }
        else if (submitOnBlur)
        {
            ApplyTextEntry(entry, valueType, state);
        }

        state.WasFocused = isFocused;

        if (TryGetNumericSliderRange(entry, valueType, out double minValue, out double maxValue))
        {
            double currentValue = Convert.ToDouble(entry.BoxedEditedValue, CultureInfo.InvariantCulture);
            float sliderValue = global::UnityEngine.GUI.HorizontalSlider(
                new global::UnityEngine.Rect(rect.x, rect.y + editorHeight + 4f, rect.width, 20f),
                (float)currentValue,
                (float)minValue,
                (float)maxValue);

            if (Math.Abs(sliderValue - currentValue) > 0.0001d
                && TryConvertNumericSliderValue(sliderValue, valueType, out object? convertedValue))
            {
                if (TryApplyEntryValue(entry, convertedValue, valueType, out string? errorMessage))
                {
                    SyncTextState(entry, valueType, force: true);
                    state.ErrorMessage = string.Empty;
                }
                else
                {
                    state.ErrorMessage = errorMessage ?? "保存失败。";
                }
            }
        }
    }

    private void DrawReadOnlyEntry(string value, global::UnityEngine.Rect rect)
    {
        bool previousEnabled = global::UnityEngine.GUI.enabled;
        global::UnityEngine.GUI.enabled = false;
        global::UnityEngine.GUI.TextField(new global::UnityEngine.Rect(rect.x, rect.y, rect.width, TextFieldHeight), value ?? string.Empty);
        global::UnityEngine.GUI.enabled = previousEnabled;
    }

    private void ApplyTextEntry(MelonPreferences_Entry entry, Type valueType, EntryTextState state)
    {
        if (!TryParseValue(state.Text, valueType, out object? parsedValue, out string? parseError))
        {
            state.ErrorMessage = parseError ?? "输入内容无法识别。";
            return;
        }

        if (!TryApplyEntryValue(entry, parsedValue, valueType, out string? applyError))
        {
            state.ErrorMessage = applyError ?? "保存失败。";
            return;
        }

        SyncTextState(entry, valueType, force: true);
        state.ErrorMessage = string.Empty;
    }
}
