using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace NO_AR;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("AryxWeaponryExpansion", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.minec.bote", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static ConfigEntry<string> ProviderAircraftJsonKeys = null!;
    internal static ConfigEntry<bool> ProviderEnabledByDefault = null!;
    internal static ConfigEntry<float> ServiceRange = null!;
    internal static ConfigEntry<float> ServiceTime = null!;
    internal static ConfigEntry<float> CheckInterval = null!;
    internal static ConfigEntry<float> MinimumRadarAltitude = null!;
    internal static ConfigEntry<bool> EnableAmmoRearm = null!;
    internal static ConfigEntry<float> AmmoTransferMultiplier = null!;
    internal static ConfigEntry<float> RearmCooldownSeconds = null!;
    internal static ConfigEntry<bool> EnableFuelRefuel = null!;
    internal static ConfigEntry<float> FuelTransferMultiplier = null!;
    internal static ConfigEntry<bool> RefillVirtualAmmoOnGround = null!;
    internal static ConfigEntry<bool> RefillVirtualFuelOnGround = null!;
    internal static ConfigEntry<float> ProviderInternalFuelReservePercent = null!;
    internal static ConfigEntry<float> RearmRewardMultiplier = null!;
    internal static ConfigEntry<float> RefuelRewardPer1000Litres = null!;
    internal static ConfigEntry<bool> EnableHud = null!;
    internal static ConfigEntry<bool> SendHudUpdates = null!;
    internal static ConfigEntry<float> HudDisplayRange = null!;
    internal static ConfigEntry<float> HudXOffset = null!;
    internal static ConfigEntry<float> HudYOffset = null!;
    private static Plugin? _instance;
    
    private float _serviceTimeAccumulator;
    internal new static ManualLogSource Logger { get; private set; } = null!;
    private Harmony? Harmony { get; set; }
    
    private void Awake()
    {
        Logger = base.Logger;
        _instance = this;
        
        ServiceRange = Config.Bind("1. General (Host authoritative)", "1. Resupply Range", 1000f,
            "Maximum distance in meters between supplier and receiver.");
        ServiceTime = Config.Bind("1. General (Host authoritative)", "2. Resupply Time", 10f,
            "Seconds the receiver must remain in resupply range.");
        CheckInterval = Config.Bind("1. General (Host authoritative)", "3. Check Interval", 1f,
            new ConfigDescription("Server sided check interval in seconds.", new AcceptableValueRange<float>(0.1f, 15f)));
        MinimumRadarAltitude = Config.Bind("1. General (Host authoritative)",
            "4. Minimum Radar Altitude", 30f,
            "Both supplier and receiver must be at or above this radar altitude.");
        
        ProviderAircraftJsonKeys = Config.Bind("2. Supplier (Host authoritative)",
            "1. Aircraft jsonKeys", "QuadVTOL1,UtilityHelo1,Aryx_CargoPlane1",
            "Comma/semicolon/newline separated aircraft definition jsonKeys that may opt into the supplier role.");
        ProviderEnabledByDefault = Config.Bind("2. Supplier (Host authoritative)",
            "2. Enabled By Default", true,
            "Default/fallback supplier role for a supplier capable player plane when the server doesn't receive " +
            "a preference. Enabled = opt-out by default, disabled = opt-in by default.");
        
        EnableFuelRefuel = Config.Bind("3. Fuel (Host authoritative)", "1. Enable Fuel Resupply", true,
            "Enable airborne refuelling (uses fuel cargo container > drop tank > main tank fuel).");
        FuelTransferMultiplier = Config.Bind("3. Fuel (Host authoritative)", "2. Transfer Multiplier", 1f,
            "Fuel transfer multiplier. 1 means one supplier liter supplies one liter, " +
            "2 means one liter supplies two liters, 0.5 means two supplier liters are consumed per liter " +
            "supplied, etc.");
        RefillVirtualFuelOnGround = Config.Bind("3. Fuel (Host authoritative)",
            "3. Refill Virtual Fuel On Ground", true,
            "Also refill virtual tracked fuel type cargo containers while the supplier is stopped on the " +
            "ground near a rearmer (ammo truck/bunker/container etc, for simplicity both virtual cargo ammo and fuel come  " +
            "from ammo ground sources).");
        ProviderInternalFuelReservePercent = Config.Bind("3. Fuel (Host authoritative)",
            "4. Supplier Internal Fuel Reserve Percent", 15f,
            "Percent of the supplier aircraft's main fuel capacity that can't be handed out to others. " +
                 "External drop tanks/cargo containers don't count in this, those can be fully transferred.");
        
        EnableAmmoRearm = Config.Bind("4. Ammo (Host authoritative)", "1. Enable Ammo Resupply", true,
            "Enable airborne rearming. Uses ammo containers (only the ones able to rearm ground units " +
            "count), their capacity is virtually tracked (they're not actually emptied, if you'd deploy them " +
            "they'd still be full, this virtual tracking reduces complexity).");
        AmmoTransferMultiplier = Config.Bind("4. Ammo (Host authoritative)", "2. Transfer Multiplier", 1f,
            "Ammo transfer multiplier. 1 means one kg of supplier ammo supplies one kg, " +
            "2 means one kg supplies two kg, 0.5 means two supplier kg are consumed per kg supplied.");
        RefillVirtualAmmoOnGround = Config.Bind("4. Ammo (Host authoritative)", "3. Refill Virtual Ammo On Ground", true,
            "Refill virtual tracked ammo cargo while the supplier is stopped on the ground near a rearmer " +
            "(ammo truck/bunker/container etc).");
        RearmCooldownSeconds = Config.Bind("4. Ammo (Host authoritative)", "4. Rearm Cooldown", 300f,
            "Cooldown in seconds after a successful weapon rearm before the same plane can be rearmed " +
            "again by any supplier aircraft. Refuelling has no cooldown.");
        
        RefuelRewardPer1000Litres = Config.Bind("5. Rewards (Host authoritative)",
            "1. Refuel Reward Per 1000Litres", 2f,
            "Score reward per 1000 litres delivered by airborne refuelling. " +
            "(0 disables the refuel supplier reward)");
        RearmRewardMultiplier = Config.Bind("5. Rewards (Host authoritative)",
            "2. Rearm Reward Multiplier", 2f,
            "Multiplier applied to the airborne supplier reward for rearming ammo. " +
            "(0 disables the rearm supplier reward)");
        
        SendHudUpdates = Config.Bind("6. HUD (Host authoritative)", "1. Send HUD Updates", true,
            "When enabled, server sends HUD updates to clients, with this off clients won't have the " +
            "HUD elements of the mod.");
        HudDisplayRange = Config.Bind("6. HUD (Host authoritative)", "2. Display Range", 5000f,
            "Distance in meters within which receivers see a nearby supplier on HUD.");
        
        EnableHud = Config.Bind("7. HUD (Client)", "1. HUD Enabled", true,
            "Show airborne resupply related status on the flight HUD.");
        HudXOffset = Config.Bind("7. HUD (Client)", "2. Position X Offset", 600f,
            "Horizontal offset from the flight HUD center.");
        HudYOffset = Config.Bind("7. HUD (Client)", "3. Position Y Offset", -60f,
            "Vertical offset from the flight HUD center.");
        
        MirageSerializerBootstrap.Initialise();
        BoteCompatibility.Initialise();
        AirResupplyHud.Initialise();
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Harmony?.PatchAll();
        base.Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }
    
    private void Update()
    {
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            _serviceTimeAccumulator = 0f;
            return;
        }
        
        var serviceInterval = Mathf.Max(0.1f, CheckInterval.Value);
        _serviceTimeAccumulator += Mathf.Max(0f, Time.deltaTime);
        if (!(_serviceTimeAccumulator >= serviceInterval))
            return;
        
        var elapsed = _serviceTimeAccumulator;
        _serviceTimeAccumulator = 0f;
        AirResupplyManager.Update(elapsed);
    }
    
    private void OnDestroy()
    {
        AirResupplyManager.Reset();
        AirResupplyHud.Shutdown();
        AirResupplyMapStatus.Clear();
        ProviderClientRoleState.Clear();
        ProviderSelectionUi.ResetSession();
        if (_instance == this)
            _instance = null;
        
        Harmony?.UnpatchSelf();
    }
    
    internal static void ResetUpdateSchedule()
    {
        if (_instance != null)
            _instance._serviceTimeAccumulator = 0f;
        
        AirResupplyHud.Clear();
        AirResupplyMapStatus.Clear();
    }
}