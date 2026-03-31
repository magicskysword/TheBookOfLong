using System;
using System.Collections.Generic;

namespace TheBookOfLong;

/// <summary>
/// 处理 PlotData 这种“一个剧情块占多行”的特殊 CSV。
/// 这里单独拆出来，避免常规 CSV 合并逻辑里混入太多特例分支。
/// </summary>
internal static class PlotDataPatchApplier
{
    internal static bool TryMergePatch(
        List<List<string>> baseRows,
        List<string> baseHeader,
        CsvPatchFile patchFile,
        int keyColumnIndex,
        out string mergedContent,
        out int addedBlockCount,
        out int modifiedBlockCount,
        out string? warning)
    {
        mergedContent = CsvUtility.Serialize(baseRows);
        addedBlockCount = 0;
        modifiedBlockCount = 0;
        warning = null;

        if (keyColumnIndex < 0)
        {
            warning = $"Skipped PlotData patch '{patchFile.FullPath}' because the key column could not be resolved.";
            return false;
        }

        if (!TryNormalizeBodyRows(baseRows, baseHeader.Count, "base PlotData", out List<List<string>> normalizedBaseRows, out warning)
            || !TryNormalizeBodyRows(patchFile.Rows, baseHeader.Count, $"patch '{patchFile.FullPath}'", out List<List<string>> normalizedPatchRows, out warning))
        {
            return false;
        }

        if (!TryBuildPlotDataBlocks(normalizedBaseRows, keyColumnIndex, "base PlotData", out List<PlotDataBlock> baseBlocks, out warning)
            || !TryBuildPlotDataBlocks(normalizedPatchRows, keyColumnIndex, $"patch '{patchFile.FullPath}'", out List<PlotDataBlock> patchBlocks, out warning))
        {
            return false;
        }

        Dictionary<string, int> baseIndexByKey = new(StringComparer.Ordinal);
        for (int i = 0; i < baseBlocks.Count; i += 1)
        {
            baseIndexByKey[baseBlocks[i].Key] = i;
        }

        for (int i = 0; i < patchBlocks.Count; i += 1)
        {
            PlotDataBlock patchBlock = patchBlocks[i];
            if (baseIndexByKey.TryGetValue(patchBlock.Key, out int existingIndex))
            {
                if (!PlotDataBlocksEqual(baseBlocks[existingIndex], patchBlock))
                {
                    baseBlocks[existingIndex] = patchBlock;
                    modifiedBlockCount += 1;
                }

                continue;
            }

            baseIndexByKey[patchBlock.Key] = baseBlocks.Count;
            baseBlocks.Add(patchBlock);
            addedBlockCount += 1;
        }

        if (addedBlockCount == 0 && modifiedBlockCount == 0)
        {
            return true;
        }

        List<List<string>> mergedRows = new(baseRows.Count);
        mergedRows.Add(new List<string>(baseHeader));
        for (int blockIndex = 0; blockIndex < baseBlocks.Count; blockIndex += 1)
        {
            PlotDataBlock block = baseBlocks[blockIndex];
            for (int rowIndex = 0; rowIndex < block.Rows.Count; rowIndex += 1)
            {
                mergedRows.Add(block.Rows[rowIndex]);
            }
        }

        mergedContent = CsvUtility.Serialize(mergedRows);
        return true;
    }

    private static bool TryNormalizeBodyRows(
        List<List<string>> rows,
        int columnCount,
        string sourceLabel,
        out List<List<string>> normalizedRows,
        out string? warning)
    {
        normalizedRows = new List<List<string>>();
        warning = null;

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

    private static bool TryBuildPlotDataBlocks(
        List<List<string>> rows,
        int keyColumnIndex,
        string sourceLabel,
        out List<PlotDataBlock> blocks,
        out string? warning)
    {
        blocks = new List<PlotDataBlock>();
        warning = null;

        PlotDataBlock? currentBlock = null;
        for (int i = 0; i < rows.Count; i += 1)
        {
            List<string> row = rows[i];
            string key = CsvUtility.GetCell(row, keyColumnIndex);
            if (!string.IsNullOrWhiteSpace(key))
            {
                currentBlock = new PlotDataBlock
                {
                    Key = key
                };

                currentBlock.Rows.Add(row);
                blocks.Add(currentBlock);
                continue;
            }

            if (currentBlock is null)
            {
                warning = $"Skipped {sourceLabel} because row {i + 2} is a PlotData continuation row without a leading ID.";
                blocks = new List<PlotDataBlock>();
                return false;
            }

            currentBlock.Rows.Add(row);
        }

        return true;
    }

    private static bool PlotDataBlocksEqual(PlotDataBlock left, PlotDataBlock right)
    {
        if (!string.Equals(left.Key, right.Key, StringComparison.Ordinal)
            || left.Rows.Count != right.Rows.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Rows.Count; i += 1)
        {
            if (!CsvUtility.RowsEqual(left.Rows[i], right.Rows[i]))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class PlotDataBlock
    {
        public string Key { get; set; } = string.Empty;

        public List<List<string>> Rows { get; } = new();
    }
}
