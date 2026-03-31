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

    public HashSet<string> SymbolicIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<List<string>> Rows { get; set; } = new();
}
