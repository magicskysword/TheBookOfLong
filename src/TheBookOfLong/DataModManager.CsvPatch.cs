using System;
using System.Collections.Generic;

namespace TheBookOfLong;

internal static partial class DataModManager
{
    private static bool TryFinalizePatchedCsv(
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
            rows = ParseCsv(content);
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
        int keyColumnIndex = ResolveKeyColumnIndex(header);
        if (keyColumnIndex < 0)
        {
            return true;
        }

        string canonicalSourcePath = BuildCanonicalSourcePath(sourcePath);
        bool changed = false;

        if (SymbolicGroupsBySourcePath.TryGetValue(canonicalSourcePath, out SymbolicIdGroup? group)
            && SymbolicSourcesBySourcePath.TryGetValue(canonicalSourcePath, out SymbolicSourceInfo? sourceInfo))
        {
            if (!sourceInfo.HasBaseMaxId)
            {
                warnings.Add(
                    $"Source '{canonicalSourcePath}' uses shared string IDs, but its base max ID could not be determined. Blank placeholder rows were not generated for missing IDs.");
            }
            else
            {
                List<int> insertedPlaceholderIds = EnsureSequentialRowsForGroup(rows, keyColumnIndex, sourceInfo.BaseMaxId + 1, group.MaxAssignedId);
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
            finalizedContent = SerializeCsv(rows);
        }

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
            string key = GetCell(rows[i], keyColumnIndex);
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
            string key = GetCell(rows[i], keyColumnIndex);
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

    private static bool LooksLikeCsvText(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        return trimmed[0] != '{' && trimmed[0] != '[';
    }

    private static bool TryMergeCsvPatch(
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
            baseRows = ParseCsv(baseContent);
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

        List<string> baseHeader = baseRows[0];
        List<string> patchHeader = patchFile.Rows[0];
        if (!HeadersEqual(baseHeader, patchHeader, out string? headerMismatch))
        {
            warning = $"Skipped data patch '{patchFile.FullPath}' for '{sourcePath}' because the header does not match the base CSV ({headerMismatch}).";
            return false;
        }

        int keyColumnIndex = ResolveKeyColumnIndex(baseHeader);
        Dictionary<string, int> rowIndexByKey = new(StringComparer.Ordinal);
        if (keyColumnIndex >= 0)
        {
            for (int i = 1; i < baseRows.Count; i += 1)
            {
                string key = GetCell(baseRows[i], keyColumnIndex);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    rowIndexByKey[key] = i;
                }
            }
        }

        for (int i = 1; i < patchFile.Rows.Count; i += 1)
        {
            if (!TryNormalizeRow(patchFile.Rows[i], baseHeader.Count, out List<string>? normalizedRow))
            {
                warning = $"Skipped data patch '{patchFile.FullPath}' for '{sourcePath}' because row {i + 1} has more columns than the header.";
                return false;
            }

            if (normalizedRow is null || IsRowEmpty(normalizedRow))
            {
                continue;
            }

            if (keyColumnIndex >= 0)
            {
                string key = normalizedRow[keyColumnIndex];
                if (!string.IsNullOrWhiteSpace(key) && rowIndexByKey.TryGetValue(key, out int existingRowIndex))
                {
                    if (!RowsEqual(baseRows[existingRowIndex], normalizedRow))
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

        mergedContent = SerializeCsv(baseRows);
        return true;
    }

    private static bool HeadersEqual(List<string> left, List<string> right, out string? mismatch)
    {
        mismatch = null;

        if (left.Count != right.Count)
        {
            mismatch = $"base has {left.Count} columns, patch has {right.Count} columns";
            return false;
        }

        for (int i = 0; i < left.Count; i += 1)
        {
            string leftCell = NormalizeHeaderCell(left[i]);
            string rightCell = NormalizeHeaderCell(right[i]);
            if (!string.Equals(leftCell, rightCell, StringComparison.Ordinal))
            {
                mismatch = $"column {i + 1}: base='{EscapeLogValue(leftCell)}', patch='{EscapeLogValue(rightCell)}'";
                return false;
            }
        }

        return true;
    }

    private static string NormalizeHeaderCell(string value)
    {
        return !string.IsNullOrEmpty(value) && value[0] == '\uFEFF'
            ? value.Substring(1)
            : value;
    }

    private static string EscapeLogValue(string value)
    {
        return value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static int ResolveKeyColumnIndex(List<string> header)
    {
        string[] exactKeyNames =
        {
            "id",
            "ID",
            "Id",
            "编号",
            "序号",
            "剧情编号"
        };

        for (int i = 0; i < exactKeyNames.Length; i += 1)
        {
            int index = IndexOfHeader(header, exactKeyNames[i]);
            if (index >= 0)
            {
                return index;
            }
        }

        for (int i = 0; i < header.Count; i += 1)
        {
            string columnName = header[i].Trim();
            if (string.IsNullOrWhiteSpace(columnName))
            {
                continue;
            }

            if (columnName.EndsWith("id", StringComparison.OrdinalIgnoreCase)
                || columnName.Contains("编号", StringComparison.Ordinal)
                || columnName.Contains("序号", StringComparison.Ordinal))
            {
                return i;
            }
        }

        if (header.Count == 1)
        {
            return 0;
        }

        return !string.IsNullOrWhiteSpace(header[0]) ? 0 : -1;
    }

    private static int IndexOfHeader(List<string> header, string name)
    {
        for (int i = 0; i < header.Count; i += 1)
        {
            if (string.Equals(header[i].Trim(), name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetCell(List<string> row, int columnIndex)
    {
        return columnIndex >= 0 && columnIndex < row.Count ? row[columnIndex] : string.Empty;
    }

    private static bool TryNormalizeRow(List<string> row, int columnCount, out List<string>? normalizedRow)
    {
        normalizedRow = new List<string>(columnCount);

        if (row.Count > columnCount)
        {
            for (int i = columnCount; i < row.Count; i += 1)
            {
                if (!string.IsNullOrEmpty(row[i]))
                {
                    normalizedRow = null;
                    return false;
                }
            }
        }

        for (int i = 0; i < columnCount; i += 1)
        {
            normalizedRow.Add(i < row.Count ? row[i] : string.Empty);
        }

        return true;
    }

    private static bool IsRowEmpty(List<string> row)
    {
        for (int i = 0; i < row.Count; i += 1)
        {
            if (!string.IsNullOrEmpty(row[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RowsEqual(List<string> left, List<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i += 1)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
