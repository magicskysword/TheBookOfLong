using System.Text;

namespace TheBookOfLong;

internal static class ComplexDataTargets
{
    internal static readonly string[] MissionDataFieldNames =
    {
        "bountyMissionDataBase",
        "MainMissionDataBase",
        "BranchMissionDataBase",
        "LittleMissionDataBase",
        "TreasureMapMissionDataBase",
        "SpeKillerMissionDataBase"
    };

    internal static bool TryGetReadyControllers(
        out global::Il2Cpp.WorldPlotEventController? worldPlotEventController,
        out global::Il2Cpp.MissionDataController? missionDataController)
    {
        worldPlotEventController = global::Il2Cpp.WorldPlotEventController.Instance;
        missionDataController = global::Il2Cpp.MissionDataController.Instance;
        return AreTargetsReady(worldPlotEventController, missionDataController);
    }

    internal static bool AreTargetsReady(
        global::Il2Cpp.WorldPlotEventController? worldPlotEventController,
        global::Il2Cpp.MissionDataController? missionDataController)
    {
        if (worldPlotEventController is null || missionDataController is null)
        {
            return false;
        }

        if (!HasNonNullMember(worldPlotEventController, "WorldPlotEventDataBase"))
        {
            return false;
        }

        for (int i = 0; i < MissionDataFieldNames.Length; i += 1)
        {
            if (!HasNonNullMember(missionDataController, MissionDataFieldNames[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static string BuildTargetSignature(
        global::Il2Cpp.WorldPlotEventController worldPlotEventController,
        global::Il2Cpp.MissionDataController missionDataController)
    {
        StringBuilder builder = new(256);
        AppendObjectIdentity(builder, "WorldPlotEventController", worldPlotEventController);
        AppendMemberIdentity(builder, worldPlotEventController, "WorldPlotEventDataBase");
        AppendObjectIdentity(builder, "MissionDataController", missionDataController);

        for (int i = 0; i < MissionDataFieldNames.Length; i += 1)
        {
            AppendMemberIdentity(builder, missionDataController, MissionDataFieldNames[i]);
        }

        return builder.ToString();
    }

    private static bool HasNonNullMember(object target, string memberName)
    {
        return ComplexTypeAccessor.TryGetMemberValue(target, memberName, out object? value) && value is not null;
    }

    private static void AppendObjectIdentity(StringBuilder builder, string name, object? value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(ComplexTypeAccessor.GetObjectIdentity(value));
        builder.Append(';');
    }

    private static void AppendMemberIdentity(StringBuilder builder, object target, string memberName)
    {
        ComplexTypeAccessor.TryGetMemberValue(target, memberName, out object? value);
        AppendObjectIdentity(builder, memberName, value);
    }
}
