using System;
using System.ComponentModel;
using System.Globalization;
using MelonLoader;

namespace TheBookOfLong;

internal sealed partial class MelonPreferencesEditor
{
    private static void EnsureEntryValueInitialized(MelonPreferences_Entry entry, Type valueType)
    {
        if (entry.BoxedValue is null)
        {
            entry.BoxedValue = CreateSafeDefaultValue(valueType);
        }

        if (entry.BoxedEditedValue is null)
        {
            entry.BoxedEditedValue = entry.BoxedValue;
        }
    }

    private static object? CreateSafeDefaultValue(Type valueType)
    {
        Type targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (targetType == typeof(string))
        {
            return string.Empty;
        }

        if (targetType.IsArray)
        {
            return Array.CreateInstance(targetType.GetElementType() ?? typeof(object), 0);
        }

        return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
    }

    private EntryTextState SyncTextState(MelonPreferences_Entry entry, Type valueType, bool force)
    {
        EnsureEntryValueInitialized(entry, valueType);

        string key = GetEntryKey(entry);
        string currentValue = FormatValue(entry.BoxedEditedValue, valueType);

        if (!_textStates.TryGetValue(key, out EntryTextState? state))
        {
            state = new EntryTextState
            {
                Text = currentValue,
                LastSyncedText = currentValue
            };

            _textStates[key] = state;
            return state;
        }

        if (force || string.Equals(state.Text, state.LastSyncedText, StringComparison.Ordinal))
        {
            state.Text = currentValue;
            state.LastSyncedText = currentValue;
            state.ErrorMessage = string.Empty;
        }

        return state;
    }

    private static bool TryApplyEntryValue(MelonPreferences_Entry entry, object? value, Type valueType, out string? errorMessage)
    {
        errorMessage = null;

        object? oldValue = entry.BoxedValue;
        object? oldEditedValue = entry.BoxedEditedValue;

        try
        {
            EnsureEntryValueInitialized(entry, valueType);

            object? validatedValue = value;
            if (entry.Validator is not null && validatedValue is not null)
            {
                validatedValue = entry.Validator.EnsureValid(validatedValue);
            }

            entry.BoxedEditedValue = validatedValue;
            entry.BoxedValue = validatedValue;
            MelonPreferences.Save();
            return true;
        }
        catch (Exception ex)
        {
            TryRestoreEntryState(entry, oldValue, oldEditedValue);
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool TryResetEntry(MelonPreferences_Entry entry, Type valueType, out string? errorMessage)
    {
        errorMessage = null;

        object? oldValue = entry.BoxedValue;
        object? oldEditedValue = entry.BoxedEditedValue;

        try
        {
            EnsureEntryValueInitialized(entry, valueType);
            entry.ResetToDefault();
            entry.BoxedEditedValue = entry.BoxedValue;
            MelonPreferences.Save();
            return true;
        }
        catch (Exception ex)
        {
            TryRestoreEntryState(entry, oldValue, oldEditedValue);
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void TryRestoreEntryState(MelonPreferences_Entry entry, object? oldValue, object? oldEditedValue)
    {
        try
        {
            entry.BoxedValue = oldValue;
            entry.BoxedEditedValue = oldEditedValue;
        }
        catch
        {
        }
    }

    private static Type ResolveEntryType(MelonPreferences_Entry entry)
    {
        return entry.GetReflectedType() ?? entry.BoxedEditedValue?.GetType() ?? entry.BoxedValue?.GetType() ?? typeof(string);
    }

    private static bool IsTextEditableType(Type valueType)
    {
        Type targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (targetType == typeof(string) || targetType == typeof(char))
        {
            return true;
        }

        if (targetType.IsPrimitive || targetType == typeof(decimal))
        {
            return true;
        }

        return TypeDescriptor.GetConverter(targetType).CanConvertFrom(typeof(string));
    }

    private static bool TryParseValue(string text, Type valueType, out object? value, out string? errorMessage)
    {
        errorMessage = null;
        Type? nullableType = Nullable.GetUnderlyingType(valueType);
        Type targetType = nullableType ?? valueType;

        if (nullableType is not null && string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        if (targetType == typeof(string))
        {
            value = text;
            return true;
        }

        if (targetType == typeof(bool))
        {
            string normalized = text.Trim().ToLowerInvariant();
            if (normalized is "true" or "on" or "yes" or "1")
            {
                value = true;
                return true;
            }

            if (normalized is "false" or "off" or "no" or "0")
            {
                value = false;
                return true;
            }

            value = null;
            errorMessage = "请输入 true/false、on/off、yes/no 或 1/0。";
            return false;
        }

        if (targetType == typeof(char))
        {
            if (text.Length == 1)
            {
                value = text[0];
                return true;
            }

            value = null;
            errorMessage = "这里只能输入单个字符。";
            return false;
        }

        if (targetType.IsEnum)
        {
            try
            {
                value = Enum.Parse(targetType, text.Trim(), ignoreCase: true);
                return true;
            }
            catch
            {
                value = null;
                errorMessage = $"可选值：{string.Join("、", Enum.GetNames(targetType))}";
                return false;
            }
        }

        try
        {
            switch (Type.GetTypeCode(targetType))
            {
                case TypeCode.SByte:
                    value = sbyte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Byte:
                    value = byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Int16:
                    value = short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.UInt16:
                    value = ushort.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Int32:
                    value = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.UInt32:
                    value = uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Int64:
                    value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.UInt64:
                    value = ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Single:
                    value = float.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Double:
                    value = double.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Decimal:
                    value = decimal.Parse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                    return true;
            }

            TypeConverter converter = TypeDescriptor.GetConverter(targetType);
            value = converter.ConvertFromInvariantString(text);
            return true;
        }
        catch (Exception ex)
        {
            value = null;
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string FormatValue(object? value, Type valueType)
    {
        if (value is null)
        {
            return string.Empty;
        }

        Type targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (targetType == typeof(bool))
        {
            return (bool)value ? "true" : "false";
        }

        if (targetType == typeof(float))
        {
            return ((float)value).ToString("G9", CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(double))
        {
            return ((double)value).ToString("G17", CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(decimal))
        {
            return ((decimal)value).ToString(CultureInfo.InvariantCulture);
        }

        return value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString() ?? string.Empty;
    }

    private static bool TryGetNumericSliderRange(MelonPreferences_Entry entry, Type valueType, out double minValue, out double maxValue)
    {
        minValue = 0d;
        maxValue = 0d;

        if (entry.Validator is not MelonLoader.Preferences.IValueRange range)
        {
            return false;
        }

        if (!IsNumericType(valueType))
        {
            return false;
        }

        try
        {
            minValue = Convert.ToDouble(range.MinValue, CultureInfo.InvariantCulture);
            maxValue = Convert.ToDouble(range.MaxValue, CultureInfo.InvariantCulture);
            return maxValue > minValue;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertNumericSliderValue(float sliderValue, Type valueType, out object? convertedValue)
    {
        convertedValue = null;
        Type targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;

        try
        {
            switch (Type.GetTypeCode(targetType))
            {
                case TypeCode.SByte:
                    convertedValue = (sbyte)Math.Round(sliderValue);
                    return true;
                case TypeCode.Byte:
                    convertedValue = (byte)Math.Round(sliderValue);
                    return true;
                case TypeCode.Int16:
                    convertedValue = (short)Math.Round(sliderValue);
                    return true;
                case TypeCode.UInt16:
                    convertedValue = (ushort)Math.Round(sliderValue);
                    return true;
                case TypeCode.Int32:
                    convertedValue = (int)Math.Round(sliderValue);
                    return true;
                case TypeCode.UInt32:
                    convertedValue = (uint)Math.Round(sliderValue);
                    return true;
                case TypeCode.Int64:
                    convertedValue = (long)Math.Round(sliderValue);
                    return true;
                case TypeCode.UInt64:
                    convertedValue = (ulong)Math.Round(sliderValue);
                    return true;
                case TypeCode.Single:
                    convertedValue = sliderValue;
                    return true;
                case TypeCode.Double:
                    convertedValue = (double)sliderValue;
                    return true;
                case TypeCode.Decimal:
                    convertedValue = (decimal)sliderValue;
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNumericType(Type valueType)
    {
        Type targetType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        return targetType == typeof(byte)
            || targetType == typeof(sbyte)
            || targetType == typeof(short)
            || targetType == typeof(ushort)
            || targetType == typeof(int)
            || targetType == typeof(uint)
            || targetType == typeof(long)
            || targetType == typeof(ulong)
            || targetType == typeof(float)
            || targetType == typeof(double)
            || targetType == typeof(decimal);
    }
}
