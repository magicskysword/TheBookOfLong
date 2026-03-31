using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace TheBookOfLong;

[HarmonyPatch(typeof(global::Il2Cpp.GameDataController), "LoadAllGameData")]
internal static class GameDataControllerLoadAllGameDataPatch
{
    private static void Prefix()
    {
        ConfigDumpManager.BeginCapture("GameDataController.LoadAllGameData");
    }

    private static void Postfix()
    {
        ConfigDumpManager.EndCapture("GameDataController.LoadAllGameData");
    }
}

[HarmonyPatch(typeof(global::Il2Cpp.GameDataController), "LoadPeotryData")]
internal static class GameDataControllerLoadPeotryDataPatch
{
    private static void Prefix()
    {
        ConfigDumpManager.BeginCapture("GameDataController.LoadPeotryData");
    }

    private static void Postfix()
    {
        ConfigDumpManager.EndCapture("GameDataController.LoadPeotryData");
    }
}

[HarmonyPatch(typeof(global::Il2Cpp.LTCSVLoader), "ReadMultiLine")]
internal static class LTCSVLoaderReadMultiLinePatch
{
    private static void Prefix(string str)
    {
        ConfigDumpManager.CaptureLoaderInput("ReadMultiLine", str);
    }
}

[HarmonyPatch(typeof(global::Il2Cpp.LTCSVLoader), "ReadFile")]
internal static class LTCSVLoaderReadFilePatch
{
    private static void Prefix(string fileName)
    {
        ConfigDumpManager.CaptureLoaderFile("ReadFile", fileName);
    }
}

[HarmonyPatch]
internal static class UnityResourcesLoadPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodInfo method in typeof(global::UnityEngine.Resources).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != nameof(global::UnityEngine.Resources.Load) || method.ContainsGenericParameters)
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(string))
            {
                yield return method;
            }
        }
    }

    private static void Postfix(string path, object __result)
    {
        ConfigDumpManager.CaptureLoadedResource(path, __result);
        DataModManager.CaptureLoadedResource(path, __result);
    }
}

[HarmonyPatch]
internal static class UnityTextAssetTextPatch
{
    private static MethodBase? TargetMethod()
    {
        return AccessTools.PropertyGetter(typeof(global::UnityEngine.TextAsset), "text");
    }

    private static void Postfix(global::UnityEngine.TextAsset __instance, ref string __result)
    {
        string originalText = __result;

        // 注入点之前，先尽可能还原并落盘原始文本，保证 Dump 看到的是游戏最初读到的数据。
        string preferredText = ConfigDumpManager.ResolveBestTextAssetContent(__instance, originalText, out _);
        ConfigDumpManager.CaptureTextAssetRead(__instance, originalText);

        if (!string.Equals(__result, preferredText, StringComparison.Ordinal))
        {
            __result = preferredText;
        }

        // Dump 完成后，CSV Mod 从这里开始接管返回给游戏的文本内容。
        DataModManager.TryApplyTextPatch(__instance, ref __result);

        if (!string.Equals(originalText, __result, StringComparison.Ordinal))
        {
            ConfigDumpManager.RegisterAdditionalTextAssetContent(__instance, __result);
        }
    }
}
