using System;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using NuclearOption.Networking;
using NuclearOption.UIStyleSystem;
using TMPro;

namespace NO_AR;

[HarmonyPatch]
internal static class HarmonyPatches
{
    [HarmonyPatch(typeof(Unit), nameof(Unit.InitializeUnit))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void InitializeUnit_Postfix(Unit __instance)
    {
        var aircraft = __instance as Aircraft;
        if (aircraft != null && aircraft.IsServer)
            AirResupplyManager.RegisterAircraft(aircraft);
    }
    
    [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.SetMission))]
    [HarmonyPrefix]
    private static void SetMission_Prefix()
    {
        ProviderClientRoleState.Clear();
        AirResupplyManager.Reset();
        Plugin.ResetUpdateSchedule();
    }
    
    [HarmonyPatch(typeof(NetworkManagerNuclearOption), nameof(NetworkManagerNuclearOption.Awake))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void NetworkManagerNuclearOption_Awake_Postfix(NetworkManagerNuclearOption __instance)
    {
        // Awake is apparently too early to touch Server.MessageHandler, Mirage has not called StartServer yet
        // Only subscribe here, and actual registration happens on Started
        AirResupplyNetworking.AttachNetworkLifecycle(__instance.Server, __instance.Client);
    }
    
    [HarmonyPatch(typeof(NetworkManagerNuclearOption), nameof(NetworkManagerNuclearOption.ClientDisconnected))]
    [HarmonyPostfix]
    private static void NetworkManagerNuclearOption_ClientDisconnected_Postfix()
    {
        ProviderSelectionUi.ResetSession();
        ProviderClientRoleState.Clear();
        AirResupplyNetworking.ResetClientProtocolState();
    }
    
    [HarmonyPatch(typeof(NetworkMission), nameof(NetworkMission.ClientStarted))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void NetworkMission_ClientStarted_Postfix(NetworkMission __instance)
    {
        AirResupplyNetworking.RegisterClientHandlers(__instance.client);
    }
    
    [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.Awake))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void FlightHud_Awake_Postfix(FlightHud __instance) => AirResupplyHud.OnFlightHudAwake(__instance);
    
    [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.SetAircraft))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void FlightHud_SetAircraft_Postfix(FlightHud __instance, Aircraft aircraft) =>
        AirResupplyHud.OnFlightHudSetAircraft(__instance, aircraft);
    
    [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.Update))]
    [HarmonyPostfix]
    private static void FlightHud_Update_Postfix() => AirResupplyHud.UpdateFrame();
    
    [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.OnDestroy))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void FlightHud_OnDestroy_Postfix(FlightHud __instance) =>
        AirResupplyHud.OnFlightHudDestroyed(__instance);
    
    [HarmonyPatch(typeof(FuelGauge), nameof(FuelGauge.RefreshSettings))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void FuelGauge_RefreshSettings_Postfix(Aircraft ___aircraft,
        // ReSharper disable once InconsistentNaming
        TextMeshProUGUI ___fuelLabel) => AirResupplyHud.OnFuelGaugeRefreshed(___aircraft, ___fuelLabel);
    
    [HarmonyPatch(typeof(FuelGauge), nameof(FuelGauge.OnDestroy))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void FuelGauge_OnDestroy_Postfix(TextMeshProUGUI ___fuelLabel) =>
        AirResupplyHud.OnFuelGaugeDestroyed(___fuelLabel);
    
    [HarmonyPatch(typeof(ThemeManager), nameof(ThemeManager.NotifyThemeGroupChanged))]
    [HarmonyPostfix]
    private static void ThemeManager_NotifyThemeGroupChanged_Postfix() => AirResupplyHud.OnThemeChanged();
}

[HarmonyPatch]
internal static class AryxWeaponPackDropTankAutoJettisonPatch
{
    private static readonly Type? DropTankType = AccessTools.TypeByName("AryxWeaponryExpansion.AryxDropTank");
    
    private static readonly FieldInfo? AircraftField =
        DropTankType != null ? AccessTools.Field(DropTankType, "aircraft") : null;
    
    [UsedImplicitly]
    private static bool Prepare() => DropTankType != null && AircraftField != null &&
                                     AccessTools.Method(DropTankType, "CheckAutoEject") != null;
    
    [UsedImplicitly]
    private static MethodBase? TargetMethod() =>
        DropTankType != null ? AccessTools.Method(DropTankType, "CheckAutoEject") : null;
    
    [HarmonyPrefix]
    [UsedImplicitly]
    // ReSharper disable once InconsistentNaming
    private static bool Prefix(object __instance)
    {
        if (AircraftField?.GetValue(__instance) is not Aircraft aircraft)
            return true;
        
        return !AirResupplyManager.IsActiveProvider(aircraft);
    }
}