using System;
using System.Text;

namespace TheBookOfLong;

/// <summary>
/// 只按统一分隔符列表扫描文本，并替换其中完整匹配 modXXX 的片段。
/// 这里不理解函数名、参数语义或业务结构，只负责“分段后识别符号 ID”。
/// </summary>
internal static class DelimitedSymbolicIdRewriter
{
    internal static void Visit(string value, Action<string> visitor)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        int tokenStart = 0;
        for (int i = 0; i <= value.Length; i += 1)
        {
            bool atEnd = i == value.Length;
            if (!atEnd && !SymbolicIdTokenDelimiters.IsDelimiter(value[i]))
            {
                continue;
            }

            string token = value.Substring(tokenStart, i - tokenStart);
            if (TryExtractSymbolicId(token, out string symbolicId, out _, out _))
            {
                visitor(symbolicId);
            }

            tokenStart = i + 1;
        }
    }

    internal static string Rewrite(string value, Func<string, string?> rewriteSymbolicId)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        int tokenStart = 0;
        int lastCopiedIndex = 0;
        StringBuilder? builder = null;

        for (int i = 0; i <= value.Length; i += 1)
        {
            bool atEnd = i == value.Length;
            if (!atEnd && !SymbolicIdTokenDelimiters.IsDelimiter(value[i]))
            {
                continue;
            }

            string token = value.Substring(tokenStart, i - tokenStart);
            string rewrittenToken = RewriteToken(token, rewriteSymbolicId);
            if (!string.Equals(rewrittenToken, token, StringComparison.Ordinal))
            {
                builder ??= new StringBuilder(value.Length + 16);
                builder.Append(value, lastCopiedIndex, tokenStart - lastCopiedIndex);
                builder.Append(rewrittenToken);
                lastCopiedIndex = i;
            }

            tokenStart = i + 1;
        }

        if (builder is null)
        {
            return value;
        }

        builder.Append(value, lastCopiedIndex, value.Length - lastCopiedIndex);
        return builder.ToString();
    }

    private static string RewriteToken(string token, Func<string, string?> rewriteSymbolicId)
    {
        if (!TryExtractSymbolicId(token, out string symbolicId, out int coreStart, out int coreLength))
        {
            return token;
        }

        string? replacement = rewriteSymbolicId(symbolicId);
        if (string.IsNullOrWhiteSpace(replacement))
        {
            return token;
        }

        return coreStart == 0 && coreLength == token.Length
            ? replacement
            : token.Substring(0, coreStart) + replacement + token.Substring(coreStart + coreLength);
    }

    private static bool TryExtractSymbolicId(string token, out string symbolicId, out int coreStart, out int coreLength)
    {
        coreStart = 0;
        while (coreStart < token.Length && char.IsWhiteSpace(token[coreStart]))
        {
            coreStart += 1;
        }

        int coreEnd = token.Length - 1;
        while (coreEnd >= coreStart && char.IsWhiteSpace(token[coreEnd]))
        {
            coreEnd -= 1;
        }

        if (coreEnd < coreStart)
        {
            symbolicId = string.Empty;
            coreLength = 0;
            return false;
        }

        coreLength = coreEnd - coreStart + 1;
        string coreValue = token.Substring(coreStart, coreLength);
        return SymbolicIdService.TryGetSymbolicId(coreValue, out symbolicId);
    }
}
