using System;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using NuclearOption.Networking;
using NuclearOption.UIStyleSystem;
using TMPro;
// ReSharper disable InconsistentNaming

namespace NO_AR;

[HarmonyPatch]
internal static class HarmonyPatches
{
    [HarmonyPatch(typeof(Unit), nameof(Unit.InitializeUnit))]
    [HarmonyPostfix]
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
    private static void NetworkMission_ClientStarted_Postfix(NetworkMission __instance)
    {
        AirResupplyNetworking.RegisterClientHandlers(__instance.client);
    }
    
    [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.Awake))]
    [HarmonyPostfix]
    private static void FlightHud_Awake_Postfix(FlightHud __instance) => AirResupplyHud.OnFlightHudAwake(__instance);
    
    [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.SetAircraft))]
    [HarmonyPostfix]
    private static void FlightHud_SetAircraft_Postfix(FlightHud __instance, Aircraft aircraft) => AirResupplyHud.OnFlightHudSetAircraft(__instance, aircraft);
    
    [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.Update))]
    [HarmonyPostfix]
    private static void FlightHud_Update_Postfix() => AirResupplyHud.UpdateFrame();
    
    [HarmonyPatch(typeof(FlightHud), nameof(FlightHud.OnDestroy))]
    [HarmonyPostfix]
    private static void FlightHud_OnDestroy_Postfix(FlightHud __instance) => AirResupplyHud.OnFlightHudDestroyed(__instance);
    
    [HarmonyPatch(typeof(FuelGauge), nameof(FuelGauge.RefreshSettings))]
    [HarmonyPostfix]
    private static void FuelGauge_RefreshSettings_Postfix(Aircraft ___aircraft, TextMeshProUGUI ___fuelLabel) => AirResupplyHud.OnFuelGaugeRefreshed(___aircraft, ___fuelLabel);
    
    [HarmonyPatch(typeof(FuelGauge), nameof(FuelGauge.OnDestroy))]
    [HarmonyPostfix]
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
    private static bool Prefix(object __instance)
    {
        if (AircraftField?.GetValue(__instance) is not Aircraft aircraft)
            return true;
        
        return !AirResupplyManager.IsActiveProvider(aircraft);
    }
}

[HarmonyPatch(typeof(Unit), nameof(Unit.UserCode_RpcRearm_1512805087))]
internal static class AirborneRearmClientPatch
{
    [HarmonyPrefix]
    [UsedImplicitly]
    private static void Prefix(Unit __instance, RearmEventArgs args, out int[] __state)
    {
        __state = [];
        if (!IsAirborneGunRearm(__instance, args))
            return;
        
#pragma warning disable Harmony003
        var count = Math.Min(__instance.weaponStations.Count, args.Stations.Length);
#pragma warning restore Harmony003
        __state = new int[count];
        for (var i = 0; i < count; i++)
        {
            var station = __instance.weaponStations[i];
            if (AirResupplyGunSync.TryGetSingleGun(station, out var gun))
                __state[i] = AirResupplyGunSync.GetConservativeGunAmmo(station, gun);
        }
        
        if (AirResupplyManager.DiagnosticLogging)
#pragma warning disable Harmony003
            AirResupplyDiagnostics.DumpStations((Aircraft)__instance, "Before rearm RPC", args.Stations);
#pragma warning restore Harmony003
    }
    
    [HarmonyPostfix]
    [UsedImplicitly]
    private static void Postfix(Unit __instance, RearmEventArgs args, int[]? __state)
    {
        if (__state == null || __state.Length == 0)
            return;
        
        if (AirResupplyManager.DiagnosticLogging)
#pragma warning disable Harmony003
            AirResupplyDiagnostics.DumpStations((Aircraft)__instance, "After vanilla rearm RPC", args.Stations);
#pragma warning restore Harmony003
        
        for (var i = 0; i < __state.Length; i++)
        {
#pragma warning disable Harmony003
            var rounds = args.Stations[i];
#pragma warning restore Harmony003
            if (rounds <= 0)
                continue;
            
            var station = __instance.weaponStations[i];
            if (!AirResupplyGunSync.TryGetSingleGun(station, out var gun))
                continue;
            
            var desired = (int)Math.Min(station.FullAmmo, (long)__state[i] + rounds);
            AirResupplyGunSync.SetGunAmmo(station, gun, desired);
        }
        
        if (AirResupplyManager.DiagnosticLogging)
#pragma warning disable Harmony003
            AirResupplyDiagnostics.DumpStations((Aircraft)__instance, "After corrected rearm RPC", args.Stations);
#pragma warning restore Harmony003
    }
    
    private static bool IsAirborneGunRearm(Unit unit, RearmEventArgs args)
    {
#pragma warning disable Harmony003
        return unit is Aircraft aircraft && GameManager.IsLocalAircraft(aircraft) && args.Rearmer is Aircraft;
#pragma warning restore Harmony003
    }
}