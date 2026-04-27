using System.Collections.Generic;

namespace TheBookOfLong;

/// <summary>
/// 统一维护符号 ID 的分隔符列表。
/// CSV 与 ComplexData 中所有“按分隔符识别 modXXX”的逻辑都应走这里，
/// 后续若要兼容新的分隔符，只需在此处追加即可。
/// </summary>
public static class SymbolicIdTokenDelimiters
{
    public static List<char> TokenDelimiters { get; } = new()
    {
        ';',
        '|',
        '-',
        '/',
        '~',
        ':'
    };

    internal static bool IsDelimiter(char ch)
    {
        for (int i = 0; i < TokenDelimiters.Count; i += 1)
        {
            if (TokenDelimiters[i] == ch)
            {
                return true;
            }
        }

        return false;
    }
}
