using System;
using UnityEngine;

namespace NO_AR;

internal static class AirResupplyDiagnostics
{
    // Diagnostics - local receiver proximity snapshots
    private const float ClientNearbySnapshotInterval = 15f;
    
    private static PersistentID _lastClientAircraftId = PersistentID.None;
    private static bool _clientWasInRange;
    private static float _nextClientNearbySnapshot;
    
    internal static void OnClientHudState(AirResupplyHudStateMessage state)
    {
        if (!AirResupplyManager.DiagnosticLogging)
#pragma warning disable CS0162 // Unreachable code detected
            // ReSharper disable once HeuristicUnreachableCode
            return;
#pragma warning restore CS0162 // Unreachable code detected
        
        var inRange = state is { Visible: true, Mode: AirResupplyHudStateMessage.ReceiverMode, ServiceRange: > 0f } &&
                      state.Distance <= state.ServiceRange;
        
        if (!inRange || !GameManager.GetLocalAircraft(out var aircraft) || aircraft == null || aircraft.disabled)
        {
            _clientWasInRange = false;
            return;
        }
        
        if (_lastClientAircraftId != aircraft.persistentID)
        {
            _lastClientAircraftId = aircraft.persistentID;
            _clientWasInRange = false;
        }
        
        var now = Time.unscaledTime;
        
        if (_clientWasInRange && now < _nextClientNearbySnapshot)
            return;
        
        _clientWasInRange = true;
        _nextClientNearbySnapshot = now + ClientNearbySnapshotInterval;
        
        AirResupplyManager.Diagnostic($"Client Nearby Supplier | aircraft: {aircraft.persistentID.Id} | " +
                                      $"provider: {state.ProviderName} | distance: {state.Distance:F1}m | " +
                                      $"serviceRange: {state.ServiceRange:F1}m | " +
                                      $"servicing: {state.Servicing} | latched: {state.Latched} | " +
                                      $"progress: {state.Progress:F1}/{state.ServiceTime:F1}s | " +
                                      $"rearmCooldown: {state.RearmCooldownRemaining:F1}s");
        DumpStations(aircraft, "Client in range");
    }
    
    internal static void ResetClientNearby()
    {
        _lastClientAircraftId = PersistentID.None;
        _clientWasInRange = false;
        _nextClientNearbySnapshot = 0f;
    }
    
    internal static void DumpStations(Aircraft aircraft, string phase, int[]? deltas = null)
    {
        if (!AirResupplyManager.DiagnosticLogging || aircraft == null || aircraft.weaponStations == null)
            return;
        
        for (var i = 0; i < aircraft.weaponStations.Count; i++)
            try
            {
                DumpStation(aircraft, phase, deltas, i);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Air Resupply Diagnostic: {phase} | " +
                                         $"Station snapshot failed | station: {i} | {e}");
            }
    }
    
    private static void DumpStation(Aircraft aircraft, string phase, int[]? deltas, int i)
    {
        var station = aircraft.weaponStations[i];
        if (station == null)
            return;
        
        var count = station.Weapons?.Count ?? 0;
        var delta = deltas != null && i < deltas.Length ? deltas[i].ToString() : "N/A";
        var info = station.WeaponInfo;
        var category = info == null ? "no info" :
            info.cargo ? "Cargo" :
            info.nuclear ? "Nuclear" :
            info.massPerRound <= 0f ? "Zero mass" : "Eligible";
        
        AirResupplyManager.Diagnostic($"{phase} Station | aircraft: {aircraft.persistentID.Id} | " +
                                      $"station: {i} | weapon: {info?.shortName ?? "?"} | category: {category} | " +
                                      $"RPCdelta: {delta} | Ammo: {station.Ammo} | " +
                                      $"GetAmmoTotal: {station.GetAmmoTotal()} | " +
                                      $"GetAmmoLoaded: {station.GetAmmoLoaded()} | FullAmmo: {station.FullAmmo} | " +
                                      $"Reloading: {station.Reloading} | Weapons: {count} | " +
                                      $"LocalSim: {aircraft.LocalSim} | IsServer: {aircraft.IsServer}");
        
        if (station.Weapons == null)
            return;
        
        for (var w = 0; w < count; w++)
            try
            {
                var weapon = station.Weapons[w];
                if (weapon == null)
                    continue;
                
                var extra = weapon switch
                {
                    Gun gun => $" | CurrentAmmo: {gun.GetCurrentAmmo()}" +
                               $" | MagazineCapacity: {gun.magazineCapacity}" + $" | Magazines: {gun.magazines}" +
                               $" | MaxMagazines: {gun.maxMagazines}" + $" | ReloadTimer: {gun.timeUntilReload:F2}",
                    MountedMissile missile => $" | Fired: {missile.fired}" +
                                              $" | Active: {missile.gameObject.activeSelf}",
                    MissileLauncher launcher => $" | ReloadingAmmo: {launcher.reloadingAmmo}" +
                                                $" | ReloadInProgress: {launcher.reloadInProgress}",
                    _ => ""
                };
                
                AirResupplyManager.Diagnostic($"{phase} Weapon | station: {i} | index: {w} | " +
                                              $"type: {weapon.GetType().Name} | RawAmmo: {weapon.ammo} | " +
                                              $"Loaded: {weapon.GetAmmoLoaded()} | Total: {weapon.GetAmmoTotal()} | " +
                                              $"Full: {weapon.GetFullAmmo()}" + extra);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning(
                    $"Air Resupply Diagnostic: {phase} | " +
                    $"Weapon snapshot failed | station: {i} | " +
                    $"weaponIndex: {w} | {e}");
            }
    }
}