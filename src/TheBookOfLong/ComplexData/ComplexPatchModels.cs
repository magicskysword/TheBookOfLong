using System;
using System.Text.Json;

namespace TheBookOfLong;

internal enum ComplexControllerKind
{
    MissionData,
    WorldPlotEvent
}

internal enum ComplexPatchTargetKind
{
    ArrayByName,
    ObjectReplace
}

internal sealed class ComplexPatchTargetDefinition
{
    internal ComplexPatchTargetDefinition(
        ComplexControllerKind controllerKind,
        string memberName,
        ComplexPatchTargetKind patchTargetKind)
    {
        ControllerKind = controllerKind;
        MemberName = memberName;
        PatchTargetKind = patchTargetKind;
    }

    internal ComplexControllerKind ControllerKind { get; }

    internal string MemberName { get; }

    internal ComplexPatchTargetKind PatchTargetKind { get; }
}

internal sealed class ComplexJsonPatchFile
{
    public string ModName { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public int LoadOrder { get; set; }

    public JsonElement RootElement { get; set; }

    public ComplexPatchTargetDefinition Target { get; set; } = null!;
}

internal sealed class ComplexPatchApplyResult
{
    public string ModName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public ComplexPatchTargetKind PatchTargetKind { get; set; }

    public int AddedCount { get; set; }

    public int ModifiedCount { get; set; }

    public int ReplacedCount { get; set; }
}

internal sealed class ComplexPatchableMember
{
    internal ComplexPatchableMember(
        string name,
        Type valueType,
        Func<object, object?> getter,
        Action<object, object?> setter)
    {
        Name = name;
        ValueType = valueType;
        Getter = getter;
        Setter = setter;
    }

    internal string Name { get; }

    internal Type ValueType { get; }

    internal Func<object, object?> Getter { get; }

    internal Action<object, object?> Setter { get; }
}
