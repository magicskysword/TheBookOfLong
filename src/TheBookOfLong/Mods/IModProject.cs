using System.Collections.Generic;

namespace TheBookOfLong;

/// <summary>
/// 一个 mod 文件夹在运行时的统一视图。
/// 其他补丁系统只依赖这个接口拿元数据和补丁文件列表，不再自己重复扫描目录。
/// </summary>
internal interface IModProject
{
    string FolderName { get; }

    string DisplayName { get; }

    string Version { get; }

    int LoadOrder { get; }

    bool IsEnabled { get; }

    string ModDirectory { get; }

    string DataDirectory { get; }

    string ComplexDataDirectory { get; }

    IReadOnlyList<string> CsvPatchFiles { get; }

    IReadOnlyList<string> ComplexDataPatchFiles { get; }
}
