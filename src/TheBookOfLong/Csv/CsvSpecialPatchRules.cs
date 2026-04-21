using System;
using System.Collections.Generic;

namespace TheBookOfLong;

/// <summary>
/// 集中登记 CSV 的特殊补丁规则，避免特例文件判断散落在多个实现里。
/// 当前有两类特殊表：
/// 1. PlotData 这种按剧情块合并的多行表。
/// 2. HeroSpeTalkText / HeroNatureTalkText 这种“首列为字段、首行为对象”的转置表。
/// </summary>
internal static class CsvSpecialPatchRules
{
    private static readonly Dictionary<string, CsvSpecialPatchKind> PatchKindsBySourcePath = new(StringComparer.OrdinalIgnoreCase)
    {
        [SymbolicIdService.BuildCanonicalSourcePath("GameData/PlotData.csv")] = CsvSpecialPatchKind.PlotDataBlock,
        [SymbolicIdService.BuildCanonicalSourcePath("GameData/HeroSpeTalkText.csv")] = CsvSpecialPatchKind.TransposedTable,
        [SymbolicIdService.BuildCanonicalSourcePath("GameData/HeroNatureTalkText.csv")] = CsvSpecialPatchKind.TransposedTable
    };

    internal static CsvSpecialPatchKind GetPatchKind(string sourcePath)
    {
        string canonicalSourcePath = SymbolicIdService.BuildCanonicalSourcePath(sourcePath);
        return PatchKindsBySourcePath.TryGetValue(canonicalSourcePath, out CsvSpecialPatchKind patchKind)
            ? patchKind
            : CsvSpecialPatchKind.None;
    }
}

internal enum CsvSpecialPatchKind
{
    None,
    PlotDataBlock,
    TransposedTable
}
