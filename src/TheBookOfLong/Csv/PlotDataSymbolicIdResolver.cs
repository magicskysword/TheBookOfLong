using System;

namespace TheBookOfLong;

/// <summary>
/// 处理 PlotData.csv 中的“调用函数”和“选项”列。
/// 这里不再按函数名解释业务语义，只按统一分隔符扫描整段文本，
/// 只要片段本身是 modXXX，就交给外部回调做引用登记或最终替换。
/// </summary>
internal static class PlotDataSymbolicIdResolver
{
    internal static string RewriteFunctionCell(string value, Func<string, string?> rewriteSymbolicId)
    {
        return DelimitedSymbolicIdRewriter.Rewrite(value, rewriteSymbolicId);
    }

    internal static string RewriteOptionCell(string value, Func<string, string?> rewriteSymbolicId)
    {
        return DelimitedSymbolicIdRewriter.Rewrite(value, rewriteSymbolicId);
    }
}
