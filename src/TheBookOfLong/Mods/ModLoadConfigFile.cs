using System.Collections.Generic;

namespace TheBookOfLong;

internal sealed class ModLoadConfigFile
{
    public int FormatVersion { get; set; } = 1;

    public string Description { get; set; } = string.Empty;

    public List<ModLoadConfigEntry> Mods { get; set; } = new();
}
