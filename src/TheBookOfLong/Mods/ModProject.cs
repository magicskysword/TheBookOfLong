using System.Collections.Generic;

namespace TheBookOfLong;

internal sealed class ModProject : IModProject
{
    internal ModProject(
        string folderName,
        string displayName,
        string version,
        string modDirectory,
        string dataDirectory,
        string complexDataDirectory,
        IReadOnlyList<string> csvPatchFiles,
        IReadOnlyList<string> complexDataPatchFiles)
    {
        FolderName = folderName;
        DisplayName = displayName;
        Version = version;
        ModDirectory = modDirectory;
        DataDirectory = dataDirectory;
        ComplexDataDirectory = complexDataDirectory;
        CsvPatchFiles = csvPatchFiles;
        ComplexDataPatchFiles = complexDataPatchFiles;
    }

    public string FolderName { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public int LoadOrder { get; internal set; }

    public bool IsEnabled { get; internal set; }

    public string ModDirectory { get; }

    public string DataDirectory { get; }

    public string ComplexDataDirectory { get; }

    public IReadOnlyList<string> CsvPatchFiles { get; }

    public IReadOnlyList<string> ComplexDataPatchFiles { get; }
}
