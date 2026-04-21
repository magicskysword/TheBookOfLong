using System;
using System.Collections.Generic;

namespace TheBookOfLong;

internal sealed class CsvPatchFile
{
    public string ModName { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public int KeyColumnIndex { get; set; } = -1;

    public int LoadOrder { get; set; }

    // CSV 主键列里真正定义出来的 modXXX 符号 ID。
    // 其他系统只能引用这里定义过的 ID；分配顺序也以这里的主键排序为准。
    public List<string> OrderedPrimarySymbolicIds { get; set; } = new();

    // 当前 patch 文件里出现过的全部 modXXX。
    // 除主键外，也包含 PlotData 这类特殊列里的引用型字符串 ID。
    public HashSet<string> SymbolicIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<List<string>> Rows { get; set; } = new();
}
