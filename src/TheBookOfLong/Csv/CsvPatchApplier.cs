using System;
using System.Collections.Generic;

namespace TheBookOfLong;

/// <summary>
/// 处理常规 CSV 补丁和补丁收尾工作。
/// DataModManager 只负责拿补丁文件和日志，这里的规则才负责真正的文本合并。
/// </summary>
internal static class CsvPatchApplier
{
    internal static bool LooksLikeCsvText(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        return trimmed[0] != '{' && trimmed[0] != '[';
    }

    internal static bool TryFinalizePatchedCsv(
        string content,
        string sourcePath,
        out string finalizedContent,
        out List<string> warnings)
    {
        finalizedContent = content;
        warnings = new List<string>();

        List<List<string>> rows;
        try
        {
            rows = CsvUtility.Parse(content);
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to finalize patched CSV '{sourcePath}': {ex.Message}");
            return false;
        }

        if (rows.Count == 0)
        {
            return true;
        }

        List<string> header = rows[0];
        int keyColumnIndex = CsvUtility.ResolveKeyColumnIndex(header);
        if (keyColumnIndex < 0)
        {
            return true;
        }

        string canonicalSourcePath = SymbolicIdService.BuildCanonicalSourcePath(sourcePath);
        bool changed = false;

        if (SymbolicIdService.TryGetSourceResolution(canonicalSourcePath, out SymbolicSourceResolution resolution))
        {
            if (!resolution.HasBaseMaxId)
            {
                warnings.Add(
                    $"Source '{canonicalSourcePath}' uses shared string IDs, but its base max ID could not be determined. Blank placeholder rows were not generated for missing IDs.");
            }
            else
            {
                List<int> insertedPlaceholderIds = EnsureSequentialRowsForGroup(rows, keyColumnIndex, resolution.BaseMaxId + 1, resolution.MaxAssignedId);
                if (insertedPlaceholderIds.Count > 0)
                {
                    changed = true;
                    warnings.Add(
                        $"Source '{canonicalSourcePath}' is missing {insertedPlaceholderIds.Count} row(s) required to keep shared string IDs aligned. Added blank placeholder row(s) for ID {FormatIdRanges(insertedPlaceholderIds)}.");
                }
            }
        }

        if (TrySortRowsByNumericKey(rows, keyColumnIndex))
        {
            changed = true;
        }

        if (changed)
        {
            finalizedContent = CsvUtility.Serialize(rows);
        }

        return true;
    }

    internal static bool TryMergePatch(
        string baseContent,
        CsvPatchFile patchFile,
        string sourcePath,
        out string mergedContent,
        out int addedRowCount,
        out int modifiedRowCount,
        out string? warning)
    {
        mergedContent = baseContent;
        addedRowCount = 0;
        modifiedRowCount = 0;
        warning = null;

        List<List<string>> baseRows;
        try
        {
            baseRows = CsvUtility.Parse(baseContent);
        }
        catch (Exception ex)
        {
            warning = $"Failed to parse base CSV '{sourcePath}' before applying '{patchFile.FullPath}': {ex.Message}";
            return false;
        }

        if (baseRows.Count == 0 || patchFile.Rows.Count == 0)
        {
            return true;
        }

        CsvSpecialPatchKind specialPatchKind = CsvSpecialPatchRules.GetPatchKind(sourcePath);
        if (specialPatchKind == CsvSpecialPatchKind.TransposedTable)
        {
            return TransposedCsvPatchApplier.TryMergePatch(
                baseRows,
                patchFile,
                sourcePath,
                out mergedContent,
                out addedRowCount,
                out modifiedRowCount,
                out warning);
        }

        List<string> baseHeader = baseRows[0];
        List<string> patchHeader = patchFile.Rows[0];
        if (!CsvUtility.HeadersEqual(baseHeader, patchHeader, out string? headerMismatch))
        {
            warning = $"Skipped data patch '{patchFile.FullPath}' for '{sourcePath}' because the header does not match the base CSV ({headerMismatch}).";
            return false;
        }

        int keyColumnIndex = CsvUtility.ResolveKeyColumnIndex(baseHeader);
        if (specialPatchKind == CsvSpecialPatchKind.PlotDataBlock)
        {
            return PlotDataPatchApplier.TryMergePatch(
                baseRows,
                baseHeader,
                patchFile,
                keyColumnIndex,
                out mergedContent,
                out addedRowCount,
                out modifiedRowCount,
                out warning);
        }

        Dictionary<string, int> rowIndexByKey = new(StringComparer.Ordinal);
        if (keyColumnIndex >= 0)
        {
            for (int i = 1; i < baseRows.Count; i += 1)
            {
                string key = CsvUtility.GetCell(baseRows[i], keyColumnIndex);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    rowIndexByKey[key] = i;
                }
            }
        }

        for (int i = 1; i < patchFile.Rows.Count; i += 1)
        {
            if (!CsvUtility.TryNormalizeRow(patchFile.Rows[i], baseHeader.Count, out List<string>? normalizedRow))
            {
                warning = $"Skipped data patch '{patchFile.FullPath}' for '{sourcePath}' because row {i + 1} has more columns than the header.";
                return false;
            }

            if (normalizedRow is null || CsvUtility.IsRowEmpty(normalizedRow))
            {
                continue;
            }

            if (keyColumnIndex >= 0)
            {
                string key = normalizedRow[keyColumnIndex];
                if (!string.IsNullOrWhiteSpace(key) && rowIndexByKey.TryGetValue(key, out int existingRowIndex))
                {
                    if (!CsvUtility.RowsEqual(baseRows[existingRowIndex], normalizedRow))
                    {
                        baseRows[existingRowIndex] = normalizedRow;
                        modifiedRowCount += 1;
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(key))
                {
                    rowIndexByKey[key] = baseRows.Count;
                }
            }

            baseRows.Add(normalizedRow);
            addedRowCount += 1;
        }

        if (addedRowCount == 0 && modifiedRowCount == 0)
        {
            return true;
        }

        mergedContent = CsvUtility.Serialize(baseRows);
        return true;
    }

    private static List<int> EnsureSequentialRowsForGroup(List<List<string>> rows, int keyColumnIndex, int startId, int endId)
    {
        List<int> insertedIds = new();
        if (startId > endId)
        {
            return insertedIds;
        }

        HashSet<int> existingIds = new();
        for (int i = 1; i < rows.Count; i += 1)
        {
            string key = CsvUtility.GetCell(rows[i], keyColumnIndex);
            if (int.TryParse(key, out int numericId))
            {
                existingIds.Add(numericId);
            }
        }

        int columnCount = rows[0].Count;
        for (int numericId = startId; numericId <= endId; numericId += 1)
        {
            if (existingIds.Contains(numericId))
            {
                continue;
            }

            List<string> blankRow = CreateBlankRow(columnCount, keyColumnIndex, numericId);
            rows.Add(blankRow);
            existingIds.Add(numericId);
            insertedIds.Add(numericId);
        }

        return insertedIds;
    }

    private static List<string> CreateBlankRow(int columnCount, int keyColumnIndex, int numericId)
    {
        List<string> row = new(columnCount);
        for (int i = 0; i < columnCount; i += 1)
        {
            row.Add(string.Empty);
        }

        if (keyColumnIndex >= 0 && keyColumnIndex < row.Count)
        {
            row[keyColumnIndex] = numericId.ToString();
        }

        return row;
    }

    private static bool TrySortRowsByNumericKey(List<List<string>> rows, int keyColumnIndex)
    {
        if (rows.Count <= 2)
        {
            return false;
        }

        List<SortableRow> sortableRows = new(rows.Count - 1);
        for (int i = 1; i < rows.Count; i += 1)
        {
            string key = CsvUtility.GetCell(rows[i], keyColumnIndex);
            if (!int.TryParse(key, out int numericId))
            {
                return false;
            }

            sortableRows.Add(new SortableRow
            {
                OriginalIndex = i,
                NumericId = numericId,
                Row = rows[i]
            });
        }

        sortableRows.Sort(static (left, right) =>
        {
            int compare = left.NumericId.CompareTo(right.NumericId);
            return compare != 0 ? compare : left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        bool changed = false;
        for (int i = 0; i < sortableRows.Count; i += 1)
        {
            if (!ReferenceEquals(rows[i + 1], sortableRows[i].Row))
            {
                changed = true;
            }

            rows[i + 1] = sortableRows[i].Row;
        }

        return changed;
    }

    private static string FormatIdRanges(List<int> ids)
    {
        if (ids.Count == 0)
        {
            return string.Empty;
        }

        ids.Sort();

        List<string> parts = new();
        int rangeStart = ids[0];
        int rangeEnd = ids[0];

        for (int i = 1; i < ids.Count; i += 1)
        {
            int currentId = ids[i];
            if (currentId == rangeEnd + 1)
            {
                rangeEnd = currentId;
                continue;
            }

            parts.Add(rangeStart == rangeEnd ? rangeStart.ToString() : $"{rangeStart}-{rangeEnd}");
            rangeStart = currentId;
            rangeEnd = currentId;
        }

        parts.Add(rangeStart == rangeEnd ? rangeStart.ToString() : $"{rangeStart}-{rangeEnd}");
        return string.Join(", ", parts);
    }

    private sealed class SortableRow
    {
        public int OriginalIndex { get; set; }

        public int NumericId { get; set; }

        public List<string> Row { get; set; } = new();
    }
}
