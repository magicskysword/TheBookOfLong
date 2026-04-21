using System;
using System.Collections.Generic;

namespace TheBookOfLong;

/// <summary>
/// 处理“第一列是字段、第一行是对象”的转置表。
/// 这里按首行对象名做主键：已存在则更新对应列，不存在则整列追加。
/// patch 允许只提供部分字段行；未提供的字段保持原值。
/// </summary>
internal static class TransposedCsvPatchApplier
{
    internal static bool TryMergePatch(
        List<List<string>> baseRows,
        CsvPatchFile patchFile,
        string sourcePath,
        out string mergedContent,
        out int addedColumnCount,
        out int modifiedColumnCount,
        out string? warning)
    {
        mergedContent = CsvUtility.Serialize(baseRows);
        addedColumnCount = 0;
        modifiedColumnCount = 0;
        warning = null;

        if (baseRows.Count == 0 || patchFile.Rows.Count == 0)
        {
            return true;
        }

        if (!TryNormalizeRows(baseRows, baseRows[0].Count, $"base transposed CSV '{sourcePath}'", out List<List<string>> normalizedBaseRows, out warning)
            || !TryNormalizeRows(patchFile.Rows, patchFile.Rows[0].Count, $"patch '{patchFile.FullPath}'", out List<List<string>> normalizedPatchRows, out warning))
        {
            return false;
        }

        List<string> baseHeader = normalizedBaseRows[0];
        List<string> patchHeader = normalizedPatchRows[0];
        if (patchHeader.Count <= 1)
        {
            return true;
        }

        if (!TryBuildRowIndexByLabel(normalizedBaseRows, $"base transposed CSV '{sourcePath}'", out Dictionary<string, int> baseRowIndexByLabel, out warning)
            || !TryBuildColumnIndexByKey(baseHeader, $"base transposed CSV '{sourcePath}'", out Dictionary<string, int> baseColumnIndexByKey, out warning)
            || !TryBuildPatchRowEntries(normalizedPatchRows, patchFile.FullPath, out List<PatchRowEntry> patchRows, out warning))
        {
            return false;
        }

        HashSet<int> modifiedExistingColumns = new();
        HashSet<string> seenPatchColumnKeys = new(StringComparer.Ordinal);

        for (int patchColumnIndex = 1; patchColumnIndex < patchHeader.Count; patchColumnIndex += 1)
        {
            string patchColumnKey = NormalizeAxisKey(patchHeader[patchColumnIndex]);
            if (string.IsNullOrWhiteSpace(patchColumnKey))
            {
                if (IsPatchColumnEmpty(normalizedPatchRows, patchColumnIndex))
                {
                    continue;
                }

                warning = $"Skipped data patch '{patchFile.FullPath}' for '{sourcePath}' because patch column {patchColumnIndex + 1} has a blank header but contains values.";
                return false;
            }

            if (!seenPatchColumnKeys.Add(patchColumnKey))
            {
                warning = $"Skipped data patch '{patchFile.FullPath}' for '{sourcePath}' because patch column '{patchColumnKey}' is duplicated.";
                return false;
            }

            if (baseColumnIndexByKey.TryGetValue(patchColumnKey, out int baseColumnIndex))
            {
                bool columnChanged = ApplyPatchColumn(
                    normalizedBaseRows,
                    baseRowIndexByLabel,
                    patchRows,
                    patchColumnIndex,
                    baseColumnIndex);

                if (columnChanged)
                {
                    modifiedExistingColumns.Add(baseColumnIndex);
                }

                continue;
            }

            int addedColumnIndex = AppendColumn(normalizedBaseRows, patchColumnKey);
            baseColumnIndexByKey[patchColumnKey] = addedColumnIndex;
            ApplyPatchColumn(normalizedBaseRows, baseRowIndexByLabel, patchRows, patchColumnIndex, addedColumnIndex);
            addedColumnCount += 1;
        }

        modifiedColumnCount = modifiedExistingColumns.Count;
        if (addedColumnCount == 0 && modifiedColumnCount == 0)
        {
            return true;
        }

        mergedContent = CsvUtility.Serialize(normalizedBaseRows);
        return true;
    }

    private static bool TryNormalizeRows(
        List<List<string>> rows,
        int columnCount,
        string sourceLabel,
        out List<List<string>> normalizedRows,
        out string? warning)
    {
        normalizedRows = new List<List<string>>();
        warning = null;

        if (rows.Count == 0)
        {
            return true;
        }

        if (!CsvUtility.TryNormalizeRow(rows[0], columnCount, out List<string>? normalizedHeader) || normalizedHeader is null)
        {
            warning = $"Skipped {sourceLabel} because its header row has more columns than expected.";
            normalizedRows = new List<List<string>>();
            return false;
        }

        normalizedRows.Add(normalizedHeader);

        for (int i = 1; i < rows.Count; i += 1)
        {
            if (!CsvUtility.TryNormalizeRow(rows[i], columnCount, out List<string>? normalizedRow))
            {
                warning = $"Skipped {sourceLabel} because row {i + 1} has more columns than the header.";
                normalizedRows = new List<List<string>>();
                return false;
            }

            if (normalizedRow is null || CsvUtility.IsRowEmpty(normalizedRow))
            {
                continue;
            }

            normalizedRows.Add(normalizedRow);
        }

        return true;
    }

    private static bool TryBuildRowIndexByLabel(
        List<List<string>> rows,
        string sourceLabel,
        out Dictionary<string, int> rowIndexByLabel,
        out string? warning)
    {
        rowIndexByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
        warning = null;

        for (int rowIndex = 1; rowIndex < rows.Count; rowIndex += 1)
        {
            string rowLabel = NormalizeAxisKey(CsvUtility.GetCell(rows[rowIndex], 0));
            if (string.IsNullOrWhiteSpace(rowLabel))
            {
                warning = $"Skipped {sourceLabel} because row {rowIndex + 1} has a blank first-column label.";
                rowIndexByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
                return false;
            }

            if (!rowIndexByLabel.TryAdd(rowLabel, rowIndex))
            {
                warning = $"Skipped {sourceLabel} because first-column label '{rowLabel}' is duplicated.";
                rowIndexByLabel = new Dictionary<string, int>(StringComparer.Ordinal);
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildColumnIndexByKey(
        List<string> header,
        string sourceLabel,
        out Dictionary<string, int> columnIndexByKey,
        out string? warning)
    {
        columnIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        warning = null;

        for (int columnIndex = 1; columnIndex < header.Count; columnIndex += 1)
        {
            string columnKey = NormalizeAxisKey(header[columnIndex]);
            if (string.IsNullOrWhiteSpace(columnKey))
            {
                continue;
            }

            if (!columnIndexByKey.TryAdd(columnKey, columnIndex))
            {
                warning = $"Skipped {sourceLabel} because column header '{columnKey}' is duplicated.";
                columnIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildPatchRowEntries(
        List<List<string>> patchRows,
        string patchFilePath,
        out List<PatchRowEntry> rowEntries,
        out string? warning)
    {
        rowEntries = new List<PatchRowEntry>();
        warning = null;

        HashSet<string> seenRowLabels = new(StringComparer.Ordinal);
        for (int rowIndex = 1; rowIndex < patchRows.Count; rowIndex += 1)
        {
            string rowLabel = NormalizeAxisKey(CsvUtility.GetCell(patchRows[rowIndex], 0));
            if (string.IsNullOrWhiteSpace(rowLabel))
            {
                warning = $"Skipped data patch '{patchFilePath}' because row {rowIndex + 1} has a blank first-column label.";
                rowEntries = new List<PatchRowEntry>();
                return false;
            }

            if (!seenRowLabels.Add(rowLabel))
            {
                warning = $"Skipped data patch '{patchFilePath}' because first-column label '{rowLabel}' is duplicated.";
                rowEntries = new List<PatchRowEntry>();
                return false;
            }

            rowEntries.Add(new PatchRowEntry
            {
                RowLabel = rowLabel,
                Row = patchRows[rowIndex]
            });
        }

        return true;
    }

    private static bool ApplyPatchColumn(
        List<List<string>> baseRows,
        Dictionary<string, int> baseRowIndexByLabel,
        List<PatchRowEntry> patchRows,
        int patchColumnIndex,
        int baseColumnIndex)
    {
        bool changed = false;
        for (int i = 0; i < patchRows.Count; i += 1)
        {
            PatchRowEntry patchRow = patchRows[i];
            int baseRowIndex = EnsureRow(baseRows, baseRowIndexByLabel, patchRow.RowLabel, baseRows[0].Count);
            string patchValue = CsvUtility.GetCell(patchRow.Row, patchColumnIndex);
            if (!string.Equals(baseRows[baseRowIndex][baseColumnIndex], patchValue, StringComparison.Ordinal))
            {
                baseRows[baseRowIndex][baseColumnIndex] = patchValue;
                changed = true;
            }
        }

        return changed;
    }

    private static int AppendColumn(List<List<string>> baseRows, string columnKey)
    {
        for (int rowIndex = 0; rowIndex < baseRows.Count; rowIndex += 1)
        {
            baseRows[rowIndex].Add(string.Empty);
        }

        int columnIndex = baseRows[0].Count - 1;
        baseRows[0][columnIndex] = columnKey;
        return columnIndex;
    }

    private static int EnsureRow(
        List<List<string>> baseRows,
        Dictionary<string, int> baseRowIndexByLabel,
        string rowLabel,
        int columnCount)
    {
        if (baseRowIndexByLabel.TryGetValue(rowLabel, out int existingRowIndex))
        {
            return existingRowIndex;
        }

        List<string> newRow = new(columnCount);
        for (int i = 0; i < columnCount; i += 1)
        {
            newRow.Add(string.Empty);
        }

        newRow[0] = rowLabel;
        int newRowIndex = baseRows.Count;
        baseRows.Add(newRow);
        baseRowIndexByLabel[rowLabel] = newRowIndex;
        return newRowIndex;
    }

    private static bool IsPatchColumnEmpty(List<List<string>> patchRows, int patchColumnIndex)
    {
        for (int rowIndex = 1; rowIndex < patchRows.Count; rowIndex += 1)
        {
            if (!string.IsNullOrEmpty(CsvUtility.GetCell(patchRows[rowIndex], patchColumnIndex)))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeAxisKey(string value)
    {
        return value.Trim();
    }

    private sealed class PatchRowEntry
    {
        public string RowLabel { get; set; } = string.Empty;

        public List<string> Row { get; set; } = new();
    }
}
