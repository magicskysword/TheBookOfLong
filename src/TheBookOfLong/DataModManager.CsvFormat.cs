using System.Collections.Generic;
using System.Text;

namespace TheBookOfLong;

internal static partial class DataModManager
{
    private static List<List<string>> ParseCsv(string content)
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

    private static string SerializeCsv(List<List<string>> rows)
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

    private static string EscapeCsvCell(string value)
    {
        bool requiresQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!requiresQuotes)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
