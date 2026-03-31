using HarmonyLib;

namespace TheBookOfLong;

[HarmonyPatch(typeof(global::Il2Cpp.GameController), "Start")]
internal static class GameControllerStartComplexDataPipelinePatch
{
    private static void Postfix()
    {
        // 每次进入游戏场景，都重新启动一轮 ComplexData Dump + 注入。
        // 这些运行时对象会重新创建，所以不能只在第一次进入时处理一次。
        int dumpCycleId = GameComplexDataDumpManager.StartNewExportCycle();
        GameComplexDataPatchManager.StartApplyCycle(dumpCycleId);
    }
}
