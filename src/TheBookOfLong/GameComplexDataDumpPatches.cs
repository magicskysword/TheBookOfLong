using HarmonyLib;

namespace TheBookOfLong;

[HarmonyPatch(typeof(global::Il2Cpp.GameController), "Start")]
internal static class GameControllerStartComplexDataDumpPatch
{
    private static void Postfix()
    {
        GameComplexDataPatchManager.TryStartApply();
        GameComplexDataDumpManager.TryStartExport();
    }
}
