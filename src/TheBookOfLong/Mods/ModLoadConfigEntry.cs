namespace TheBookOfLong;

internal sealed class ModLoadConfigEntry
{
    public string FolderName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
