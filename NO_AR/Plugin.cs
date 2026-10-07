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
    internal static ConfigEntry<bool> Enabled = null!;
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
    internal static ConfigEntry<float> FuelTargetOverride = null!;
    internal static ConfigEntry<float> RearmRewardMultiplier = null!;
    internal static ConfigEntry<float> RefuelRewardPer1000Litres = null!;
    internal static ConfigEntry<bool> EnableHud = null!;
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
        
        
        Enabled = Config.Bind("General", "Enabled", true,
            "Master switch.");
        
        ProviderAircraftJsonKeys = Config.Bind("Provider", "AircraftJsonKeys", "Aryx_CargoPlane1",
            "Comma/semicolon/newline-separated aircraft definition jsonKeys that may opt into the provider role. " +
            "jsonKeys are matched exactly and case-sensitively.");
        
        ProviderEnabledByDefault = Config.Bind("Provider", "EnabledByDefault", true,
            "Fallback provider role for a provider-capable player aircraft when the server does not receive an " +
            "explicit per-sortie preference. true = opt-out by default; false = opt-in by default.");
        
        ServiceRange = Config.Bind("General", "ServiceRange", 1000f,
            "Maximum distance in metres between provider and receiver.");
        
        ServiceTime = Config.Bind("General", "ServiceTime", 3f,
            "Seconds the receiver must continuously remain in service range.");
        
        CheckInterval = Config.Bind("General", "CheckInterval", 1f,
            "Server-side eligibility/proximity check interval in seconds.");
        
        MinimumRadarAltitude = Config.Bind("General", "MinimumRadarAltitude", 30f,
            "Both provider and receiver must be at least this radar altitude.");
        
        EnableAmmoRearm = Config.Bind("Ammo", "EnableAmmoRearm", true,
            "Enable virtual-cargo airborne rearming.");
        
        AmmoTransferMultiplier = Config.Bind("Ammo", "TransferMultiplier", 1f,
            "Effective airborne ammo transfer multiplier. 1 = one kg of provider ammo supplies one kg; " +
            "2 = one kg supplies two kg; 0.5 = two kg are consumed per kg supplied. " +
            "Only the transfer transaction is scaled; physical cargo mass/capacity is unchanged.");
        
        RearmCooldownSeconds = Config.Bind("Ammo", "RearmCooldownSeconds", 300f,
            "Seconds after a successful mid-air weapon rearm before the same aircraft can be " +
            "rearmed again by any logistics aircraft. Fuel refuelling remains available. " +
            "A newly spawned/replacement aircraft has its own cooldown state.");
        
        EnableFuelRefuel = Config.Bind("Fuel", "EnableFuelRefuel", true,
            "Enable airborne refuelling from FUEL cargo and transferable provider onboard fuel.");
        
        FuelTransferMultiplier = Config.Bind("Fuel", "TransferMultiplier", 1f,
            "Effective airborne fuel transfer multiplier. 1 = one provider litre supplies one litre; " +
            "2 = one provider litre supplies two litres; 0.5 = two provider litres are consumed per litre supplied. " +
            "Only the transfer transaction is scaled; physical fuel mass, endurance and tank capacity are unchanged.");
        
        RefillVirtualAmmoOnGround = Config.Bind("Ammo", "RefillVirtualAmmoOnGround", true,
            "Refill virtual ammo cargo while the provider is stopped beside a valid vanilla Rearmer. " +
            "The Rearmer's own capacity is not consumed.");
        
        RefillVirtualFuelOnGround = Config.Bind("Fuel", "RefillVirtualFuelOnGround", true,
            "Refill virtual FUEL cargo while the provider is stopped beside a valid vanilla Rearmer. " +
            "The Rearmer's own capacity is not consumed.");
        
        ProviderInternalFuelReservePercent = Config.Bind("Fuel", "ProviderInternalFuelReservePercent", 15f,
            "Percent of the provider aircraft's internal/main fuel capacity that cannot be donated. " +
            "External/drop-tank capacity does not increase this reserve and remains fully transferable.");
        
        FuelTargetOverride = Config.Bind("Fuel", "FuelTargetOverride", -1f,
            "Receiver target fuel ratio. -1 uses the aircraft's configured sortie fuel level; " +
            "0..1 overrides it.");
        
        RearmRewardMultiplier = Config.Bind("Rewards", "RearmRewardMultiplier", 2f,
            "Multiplier applied to the vanilla airborne supplier reward for rearming. " +
            "The vanilla base reward is sqrt(rearm cost). 0 disables the rearm supplier reward.");
        
        RefuelRewardPer1000Litres = Config.Bind("Rewards", "RefuelRewardPer1000Litres", 2f,
            "Score/allocation reward per 1000 litres actually delivered by airborne refuelling. " +
            "0 disables the refuel supplier reward.");
        
        EnableHud = Config.Bind("HUD", "Enabled", true,
            "Show projected airborne-resupply status on the flight HUD.");
        
        HudDisplayRange = Config.Bind("HUD", "DisplayRange", 5000f,
            "Maximum distance in metres at which receivers see a nearby tanker.");
        
        HudXOffset = Config.Bind("HUD", "XOffset", 320f,
            "Horizontal offset from the projected flight-HUD centre. Applies immediately.");
        
        HudYOffset = Config.Bind("HUD", "YOffset", -180f,
            "Vertical offset from the projected flight-HUD centre. Applies immediately.");
        
        MirageSerializerBootstrap.Initialise();
        BoteCompatibility.Initialise();
        AirResupplyHud.Initialise();
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Repatch();
        base.Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }
    
    private void Update()
    {
        if (!Enabled.Value || NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
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
    
    private void Repatch()
    {
        Harmony?.UnpatchSelf();
        if (!Enabled.Value)
            return;
        
        Harmony?.PatchAll();
    }
}