using System;
using System.Text;

namespace TheBookOfLong;

internal static class PlotDataSymbolicIdResolver
{
    internal static string RewriteFunctionCell(string value, Func<string, string?> rewriteSymbolicId)
    {
        return RewritePipeSeparatedEntries(value, rewriteSymbolicId, RewriteFunctionEntry);
    }

    internal static string RewriteOptionCell(string value, Func<string, string?> rewriteSymbolicId)
    {
        return RewritePipeSeparatedEntries(value, rewriteSymbolicId, RewriteOptionEntry);
    }

    private static string RewritePipeSeparatedEntries(
        string value,
        Func<string, string?> rewriteSymbolicId,
        Func<string, Func<string, string?>, string> rewriteEntry)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string[] entries = value.Split('|');
        bool changed = false;
        for (int i = 0; i < entries.Length; i += 1)
        {
            string rewrittenEntry = rewriteEntry(entries[i], rewriteSymbolicId);
            if (!string.Equals(rewrittenEntry, entries[i], StringComparison.Ordinal))
            {
                entries[i] = rewrittenEntry;
                changed = true;
            }
        }

        return changed ? string.Join("|", entries) : value;
    }

    private static string RewriteFunctionEntry(string entry, Func<string, string?> rewriteSymbolicId)
    {
        int separatorIndex = entry.IndexOf(';');
        if (separatorIndex < 0)
        {
            return entry;
        }

        string functionName = entry.Substring(0, separatorIndex);
        string parameter = entry.Substring(separatorIndex + 1);
        string rewrittenParameter = RewriteFunctionParameter(functionName, parameter, rewriteSymbolicId);
        return string.Equals(rewrittenParameter, parameter, StringComparison.Ordinal)
            ? entry
            : functionName + ";" + rewrittenParameter;
    }

    private static string RewriteOptionEntry(string entry, Func<string, string?> rewriteSymbolicId)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return entry;
        }

        string[] parts = entry.Split(';');
        if (parts.Length < 3)
        {
            return entry;
        }

        string rewrittenParameter = RewriteFunctionParameter(parts[1], parts[2], rewriteSymbolicId);
        if (string.Equals(rewrittenParameter, parts[2], StringComparison.Ordinal))
        {
            return entry;
        }

        parts[2] = rewrittenParameter;
        return string.Join(";", parts);
    }

    private static string RewriteFunctionParameter(string functionName, string parameter, Func<string, string?> rewriteSymbolicId)
    {
        string rewritten = functionName switch
        {
            "ChangePlotDataBase" => RewriteSinglePlotId(parameter, rewriteSymbolicId),
            "AddPlotDataBase" => RewriteSinglePlotId(parameter, rewriteSymbolicId),
            "CheckPlotHappenedChangePlotDataBase" => RewriteDelimitedPlotIds(parameter, '-', rewriteSymbolicId),
            "FightAddPlotDataBase" => RewriteFightResultParameter(parameter, rewriteSymbolicId),
            "FightChangePlotDataBase" => RewriteFightResultParameter(parameter, rewriteSymbolicId),
            "DebateChangePlotDataBase" => RewriteDebateResultParameter(parameter, rewriteSymbolicId),
            _ => parameter
        };

        rewritten = RewriteNestedFightCallback(rewritten, "FightAddPlotDataBase~", rewriteSymbolicId);
        rewritten = RewriteNestedFightCallback(rewritten, "FightChangePlotDataBase~", rewriteSymbolicId);
        return rewritten;
    }

    private static string RewriteSinglePlotId(string value, Func<string, string?> rewriteSymbolicId)
    {
        return RewriteToken(value, rewriteSymbolicId);
    }

    private static string RewriteDelimitedPlotIds(string value, char separator, Func<string, string?> rewriteSymbolicId)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string[] parts = value.Split(separator);
        bool changed = false;
        for (int i = 0; i < parts.Length; i += 1)
        {
            string rewrittenPart = RewriteToken(parts[i], rewriteSymbolicId);
            if (!string.Equals(rewrittenPart, parts[i], StringComparison.Ordinal))
            {
                parts[i] = rewrittenPart;
                changed = true;
            }
        }

        return changed ? string.Join(separator.ToString(), parts) : value;
    }

    private static string RewriteFightResultParameter(string value, Func<string, string?> rewriteSymbolicId)
    {
        int tildeIndex = value.IndexOf('~');
        if (tildeIndex < 0 || tildeIndex >= value.Length - 1)
        {
            return value;
        }

        string prefix = value.Substring(0, tildeIndex + 1);
        string suffix = value.Substring(tildeIndex + 1);
        int tailIndex = suffix.IndexOf('-');
        string idsPart = tailIndex >= 0 ? suffix.Substring(0, tailIndex) : suffix;
        string tail = tailIndex >= 0 ? suffix.Substring(tailIndex) : string.Empty;
        string rewrittenIdsPart = RewriteDelimitedPlotIds(idsPart, '/', rewriteSymbolicId);
        return string.Equals(rewrittenIdsPart, idsPart, StringComparison.Ordinal)
            ? value
            : prefix + rewrittenIdsPart + tail;
    }

    private static string RewriteDebateResultParameter(string value, Func<string, string?> rewriteSymbolicId)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('-') < 0)
        {
            return value;
        }

        string[] parts = value.Split('-');
        bool changed = false;
        for (int i = 1; i < parts.Length; i += 1)
        {
            string rewrittenPart = RewriteDelimitedPlotIds(parts[i], '/', rewriteSymbolicId);
            if (!string.Equals(rewrittenPart, parts[i], StringComparison.Ordinal))
            {
                parts[i] = rewrittenPart;
                changed = true;
            }
        }

        return changed ? string.Join("-", parts) : value;
    }

    private static string RewriteNestedFightCallback(string value, string marker, Func<string, string?> rewriteSymbolicId)
    {
        int searchIndex = 0;
        int lastCopiedIndex = 0;
        StringBuilder? builder = null;

        while (searchIndex < value.Length)
        {
            int markerIndex = value.IndexOf(marker, searchIndex, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                break;
            }

            int idsStart = markerIndex + marker.Length;
            int idsEnd = idsStart;
            while (idsEnd < value.Length && value[idsEnd] != '-' && value[idsEnd] != ';' && value[idsEnd] != '|')
            {
                idsEnd += 1;
            }

            string idsPart = value.Substring(idsStart, idsEnd - idsStart);
            string rewrittenIdsPart = RewriteDelimitedPlotIds(idsPart, '/', rewriteSymbolicId);
            if (!string.Equals(rewrittenIdsPart, idsPart, StringComparison.Ordinal))
            {
                builder ??= new StringBuilder(value.Length + 16);
                builder.Append(value, lastCopiedIndex, idsStart - lastCopiedIndex);
                builder.Append(rewrittenIdsPart);
                lastCopiedIndex = idsEnd;
            }

            searchIndex = idsEnd;
        }

        if (builder is null)
        {
            return value;
        }

        builder.Append(value, lastCopiedIndex, value.Length - lastCopiedIndex);
        return builder.ToString();
    }

    private static string RewriteToken(string value, Func<string, string?> rewriteSymbolicId)
    {
        int start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start]))
        {
            start += 1;
        }

        int end = value.Length - 1;
        while (end >= start && char.IsWhiteSpace(value[end]))
        {
            end -= 1;
        }

        if (end < start)
        {
            return value;
        }

        string coreValue = value.Substring(start, end - start + 1);
        if (!SymbolicIdService.TryGetSymbolicId(coreValue, out string symbolicId))
        {
            return value;
        }

        string? replacement = rewriteSymbolicId(symbolicId);
        if (string.IsNullOrWhiteSpace(replacement))
        {
            return value;
        }

        if (start == 0 && end == value.Length - 1)
        {
            return replacement;
        }

        return value.Substring(0, start) + replacement + value.Substring(end + 1);
    }
}
