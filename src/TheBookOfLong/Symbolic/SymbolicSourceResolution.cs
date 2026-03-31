namespace TheBookOfLong;

internal readonly struct SymbolicSourceResolution
{
    internal SymbolicSourceResolution(bool hasBaseMaxId, int baseMaxId, int maxAssignedId)
    {
        HasBaseMaxId = hasBaseMaxId;
        BaseMaxId = baseMaxId;
        MaxAssignedId = maxAssignedId;
    }

    internal bool HasBaseMaxId { get; }

    internal int BaseMaxId { get; }

    internal int MaxAssignedId { get; }
}
