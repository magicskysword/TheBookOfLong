using System;
using System.Collections.Generic;
using System.Text;

namespace TheBookOfLong;

/// <summary>
/// CSV 补丁系统的公共格式工具。
/// 这里只做纯文本层处理，不关心具体业务表的合并规则。
/// </summary>
internal static class CsvUtility
{
    internal static List<List<string>> Parse(string content)
    {
        List<List<string>> rows = new();
        List<string> currentRow = new();
        StringBuilder currentCell = new();
        bool inQuotes = false;

        void EndCell()
        {
            currentRow.Add(currentCell.ToString());
            currentCell.Clear();
        }

        void EndRow()
        {
            EndCell();

            bool hasContent = false;
            for (int i = 0; i < currentRow.Count; i += 1)
            {
                if (!string.IsNullOrEmpty(currentRow[i]))
                {
                    hasContent = true;
                    break;
                }
            }

            if (hasContent || currentRow.Count > 1)
            {
                rows.Add(currentRow);
            }

            currentRow = new List<string>();
        }

        for (int i = 0; i < content.Length; i += 1)
        {
            char ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    bool escapedQuote = i + 1 < content.Length && content[i + 1] == '"';
                    if (escapedQuote)
                    {
                        currentCell.Append('"');
                        i += 1;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentCell.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    EndCell();
                    break;
                case '\r':
                    if (i + 1 < content.Length && content[i + 1] == '\n')
                    {
                        i += 1;
                    }

                    EndRow();
                    break;
                case '\n':
                    EndRow();
                    break;
                default:
                    currentCell.Append(ch);
                    break;
            }
        }

        bool hasPendingContent = currentCell.Length > 0 || currentRow.Count > 0;
        if (hasPendingContent)
        {
            EndRow();
        }

        return rows;
    }

    internal static string Serialize(List<List<string>> rows)
    {
        StringBuilder builder = new();
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex += 1)
        {
            if (rowIndex > 0)
            {
                builder.Append("\r\n");
            }

            List<string> row = rows[rowIndex];
            for (int columnIndex = 0; columnIndex < row.Count; columnIndex += 1)
            {
                if (columnIndex > 0)
                {
                    builder.Append(',');
                }

                builder.Append(EscapeCsvCell(row[columnIndex]));
            }
        }

        return builder.ToString();
    }

    internal static bool HeadersEqual(List<string> left, List<string> right, out string? mismatch)
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

    internal static int ResolveKeyColumnIndex(List<string> header)
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

    internal static string GetCell(List<string> row, int columnIndex)
    {
        return columnIndex >= 0 && columnIndex < row.Count ? row[columnIndex] : string.Empty;
    }

    internal static bool TryNormalizeRow(List<string> row, int columnCount, out List<string>? normalizedRow)
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

    internal static bool IsRowEmpty(List<string> row)
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

    internal static bool RowsEqual(List<string> left, List<string> right)
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

    private static string EscapeCsvCell(string value)
    {
        bool requiresQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!requiresQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
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
}
