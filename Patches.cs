using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace Sts2Agent;

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
public static class CombatSetupPatch
{
    [HarmonyPostfix]
    public static void Postfix(CombatManager __instance)
    {
        try
        {
            GameStabilityDetector.SubscribeToCombat(__instance);
            Plugin.Log("Combat events wired to stability detector.");
        }
        catch (Exception e)
        {
            Plugin.Log($"Error in CombatSetupPatch: {e}");
        }
    }
}

[HarmonyPatch(typeof(NOverlayStack), nameof(NOverlayStack.Push))]
public static class OverlayPushPatch
{
    [HarmonyPostfix]
    public static void Postfix() => GameStabilityDetector.OnOverlayChanged();
}

[HarmonyPatch(typeof(NOverlayStack), nameof(NOverlayStack.Remove))]
public static class OverlayRemovePatch
{
    [HarmonyPostfix]
    public static void Postfix() => GameStabilityDetector.OnOverlayChanged();
}

[HarmonyPatch(typeof(EventModel), "SetEventState")]
public static class EventStateChangedPatch
{
    [HarmonyPostfix]
    public static void Postfix() => GameStabilityDetector.ScheduleStabilityCheck();
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
public static class MapScreenOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        Plugin.Log("Map screen opened — scheduling stability check");
        GameStabilityDetector.OnRoomEntered();
    }
}

[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
public static class HandSelectCardsPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        // SelectCards sets CurrentMode before awaiting, so this fires
        // while the hand is already in selection mode
        GameStabilityDetector.OnHandSelectionEntered();
    }
}
