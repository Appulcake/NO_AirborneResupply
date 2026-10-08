using System;
using System.Collections.Generic;
using UnityEngine;

namespace NO_AR;

internal static class AirResupplyManager
{
    private const float OverrideRatio = -1f;
    private const float Epsilon = 0.001f;
    private const float MapStatusSyncInterval = 10f;
    private static readonly HashSet<Aircraft> TrackedAircraft = [];
    private static readonly Dictionary<Aircraft, ProviderState> Providers = new();
    private static readonly Dictionary<Aircraft, ReceiverSession> ReceiverSessions = new();
    private static readonly List<Aircraft> AircraftToRemove = [];
    private static readonly Dictionary<Aircraft, float> RearmCooldownUntil = new();
    private static readonly Dictionary<Type, bool> ExternalFuelTankTypeCache = new();
    private static float _mapStatusSyncAccumulator = MapStatusSyncInterval;
    
    // Unit.InitializeUnit()
    internal static void RegisterAircraft(Aircraft aircraft)
    {
        if (aircraft == null || !aircraft.IsServer || !TrackedAircraft.Add(aircraft))
            return;
        
        aircraft.onDisableUnit += OnAircraftDisabled;
        ProviderRoleManager.RegisterAircraftRole(aircraft);
    }
    
    private static void OnAircraftDisabled(Unit unit)
    {
        if (unit is Aircraft aircraft)
            RemoveTrackedAircraft(aircraft);
    }
    
    private static void RemoveTrackedAircraft(Aircraft aircraft)
    {
        if (aircraft == null)
            return;
        
        if (TrackedAircraft.Remove(aircraft))
            aircraft.onDisableUnit -= OnAircraftDisabled;
        
        Providers.Remove(aircraft);
        ReceiverSessions.Remove(aircraft);
        ProviderRoleManager.RemoveAircraftRole(aircraft);
        RearmCooldownUntil.Remove(aircraft);
        AircraftToRemove.Clear();
        foreach (var session in ReceiverSessions)
            if (session.Value.Provider == aircraft)
                AircraftToRemove.Add(session.Key);
        
        foreach (var aircraftScratch in AircraftToRemove)
            ReceiverSessions.Remove(aircraftScratch);
    }
    
    internal static void Reset()
    {
        foreach (var aircraft in TrackedAircraft)
            if (aircraft != null)
                aircraft.onDisableUnit -= OnAircraftDisabled;
        
        TrackedAircraft.Clear();
        Providers.Clear();
        ReceiverSessions.Clear();
        RearmCooldownUntil.Clear();
        AircraftToRemove.Clear();
        ProviderRoleManager.ResetServerState();
        _mapStatusSyncAccumulator = MapStatusSyncInterval;
    }
    
    internal static void Update(float elapsed)
    {
        RemoveInvalidAircraft();
        ProviderRoleManager.Update();
        
        foreach (var aircraft in TrackedAircraft)
        {
            if (aircraft == null || aircraft.disabled)
                continue;
            
            ProviderRoleManager.UpdateAircraftRole(aircraft);
            if (IsActiveProvider(aircraft))
            {
                var provider = CheckProviderState(aircraft);
                provider.RefreshCargoCapacity();
                TryGroundRefill(provider);
            }
            else
            {
                Providers.Remove(aircraft);
            }
        }
        
        _mapStatusSyncAccumulator =
            Mathf.Min(MapStatusSyncInterval, _mapStatusSyncAccumulator + Mathf.Max(0f, elapsed));
        if (Providers.Count > 0 && _mapStatusSyncAccumulator >= MapStatusSyncInterval)
        {
            _mapStatusSyncAccumulator = 0f;
            SendMapStatusStates();
        }
        
        foreach (var receiver in TrackedAircraft)
        {
            if (!IsValidReceiver(receiver))
            {
                ReceiverSessions.Remove(receiver);
                continue;
            }
            
            UpdateReceiver(receiver, elapsed);
        }
        
        if (Plugin.SendHudUpdates.Value)
            SendHudStates();
    }
    
    private static void RemoveInvalidAircraft()
    {
        AircraftToRemove.Clear();
        
        // Unity's null check overload means a destroyed Aircraft can still be == null while also exist
        // in reference collection
        foreach (var aircraft in TrackedAircraft)
            if (aircraft == null)
                AircraftToRemove.Add(aircraft!);
        
        foreach (var aircraft in AircraftToRemove)
        {
            TrackedAircraft.Remove(aircraft);
            Providers.Remove(aircraft);
            ReceiverSessions.Remove(aircraft);
            RearmCooldownUntil.Remove(aircraft);
            ProviderRoleManager.RemoveAircraftRole(aircraft);
        }
        
        AircraftToRemove.Clear();
        foreach (var session in ReceiverSessions)
            if (session.Key == null || session.Value.Provider == null)
                AircraftToRemove.Add(session.Key!);
        
        foreach (var aircraft in AircraftToRemove)
            ReceiverSessions.Remove(aircraft);
    }
    
    internal static bool IsActiveProvider(Aircraft aircraft)
    {
        if (aircraft == null)
            return false;
        
        return aircraft.IsServer
            ? ProviderRoleManager.IsActiveProvider(aircraft)
            : ProviderClientRoleState.IsActiveProvider(aircraft);
    }
    
    private static bool IsValidParticipant(Aircraft aircraft) => aircraft != null &&
                                                                 aircraft is { disabled: false, IsServer: true } &&
                                                                 aircraft.Player != null &&
                                                                 aircraft.NetworkHQ != null &&
                                                                 !BoteCompatibility.IsBoteShip(aircraft);
    
    private static ProviderState CheckProviderState(Aircraft aircraft)
    {
        if (Providers.TryGetValue(aircraft, out var state))
            return state;
        
        state = new ProviderState(aircraft);
        Providers.Add(aircraft, state);
        state.RefreshCargoCapacity();
        return state;
    }
    
    private static bool IsValidReceiver(Aircraft aircraft) =>
        IsValidParticipant(aircraft) && ProviderRoleManager.CanReceive(aircraft);
    
    private static void UpdateReceiver(Aircraft receiver, float elapsed)
    {
        if (ReceiverSessions.TryGetValue(receiver, out var session) && session.Latched)
        {
            if (IsPairEligibleAndInRange(session.Provider, receiver))
                return;
            
            ReceiverSessions.Remove(receiver);
            return;
        }
        
        if (!TryGetServiceNeed(receiver, out var need))
        {
            ReceiverSessions.Remove(receiver);
            return;
        }
        
        var provider = FindBestProvider(receiver, need);
        if (provider is null)
        {
            ReceiverSessions.Remove(receiver);
            return;
        }
        
        if (session == null || session.Provider != provider.Aircraft)
        {
            session = new ReceiverSession
            {
                Provider = provider.Aircraft,
                Progress = 0f,
                Latched = false
            };
            
            ReceiverSessions[receiver] = session;
        }
        
        session.Progress += elapsed;
        if (session.Progress < Mathf.Max(0.1f, Plugin.ServiceTime.Value))
            return;
        
        // Refresh current fuel/ammo levels that might've been expended during service timer
        TryGetServiceNeed(receiver, out var completionNeed);
        
        try
        {
            PerformAirborneService(provider, receiver, completionNeed);
        }
        catch (Exception ex)
        {
            session.Progress = 0f;
            session.Latched = true;
            Plugin.Logger.LogError($"Service completion failed for receiver {Describe(receiver)}, " +
                                   $"provider {Describe(provider.Aircraft)}\n{ex}");
            return;
        }
        
        session.Progress = 0f;
        session.Latched = true;
    }
    
    private static ProviderState? FindBestProvider(Aircraft receiver, ServiceNeed need)
    {
        ProviderState? best = null;
        var bestDistance = float.MaxValue;
        var bestCoverage = -1;
        foreach (var candidate in Providers.Values)
        {
            if (candidate.Aircraft == null)
                continue;
            
            if (!IsPairEligibleAndInRange(candidate.Aircraft, receiver))
                continue;
            
            var fuel = candidate.GetFuelAvailability();
            var canGetAmmo = need.NeedsAmmo && candidate.AmmoRemainingKg > Epsilon;
            var canGetFuel = need.NeedsFuel && fuel.Total > Epsilon;
            var coverage = (canGetAmmo ? 1 : 0) + (canGetFuel ? 1 : 0);
            if (coverage == 0)
                continue;
            
            var distance = Distance(receiver, candidate.Aircraft);
            if (coverage <= bestCoverage && (coverage != bestCoverage || !(distance < bestDistance)))
                continue;
            
            bestCoverage = coverage;
            bestDistance = distance;
            best = candidate;
        }
        
        return best;
    }
    
    private static bool IsPairEligibleAndInRange(Aircraft provider, Aircraft receiver)
    {
        if (provider == null || receiver == null || provider == receiver || provider.disabled || receiver.disabled ||
            !IsActiveProvider(provider) || !IsValidReceiver(receiver) || provider.NetworkHQ == null ||
            receiver.NetworkHQ == null || provider.NetworkHQ != receiver.NetworkHQ)
            return false;
        
        var minRadarAlt = Mathf.Max(0f, Plugin.MinimumRadarAltitude.Value);
        if (provider.radarAlt < minRadarAlt || receiver.radarAlt < minRadarAlt)
            return false;
        
        var serviceRange = Mathf.Max(1f, Plugin.ServiceRange.Value);
        var currentDistance = Distance(provider, receiver);
        return currentDistance <= serviceRange;
    }
    
    private static float Distance(Aircraft a, Aircraft b)
    {
        if (a == null || b == null)
            return float.MaxValue;
        
        return FastMath.Distance(a.GlobalPosition(), b.GlobalPosition());
    }
    
    private static bool TryGetServiceNeed(Aircraft aircraft, out ServiceNeed need)
    {
        var needsAmmo = false;
        if (Plugin.EnableAmmoRearm.Value)
        {
            needsAmmo = HasEligibleMissingAmmo(aircraft);
            if (needsAmmo)
            {
                var cooldownRemaining = GetRearmCooldownRemaining(aircraft);
                if (cooldownRemaining > Epsilon) needsAmmo = false;
            }
        }
        
        var missingFuel = Plugin.EnableFuelRefuel.Value ? GetFuelMissingToSortieTarget(aircraft) : 0f;
        need = new ServiceNeed(needsAmmo, missingFuel);
        return need.Any;
    }
    
    private static float GetRearmCooldownRemaining(Aircraft aircraft)
    {
        if (aircraft == null || Plugin.RearmCooldownSeconds.Value <= Epsilon)
            return 0f;
        
        if (!RearmCooldownUntil.TryGetValue(aircraft, out var until))
            return 0f;
        
        var remaining = until - Time.unscaledTime;
        if (remaining > Epsilon)
            return remaining;
        
        RearmCooldownUntil.Remove(aircraft);
        return 0f;
    }
    
    private static void StartRearmCooldown(Aircraft aircraft)
    {
        var duration = Mathf.Max(0f, Plugin.RearmCooldownSeconds.Value);
        if (aircraft == null || duration <= Epsilon)
            return;
        
        RearmCooldownUntil[aircraft] = Time.unscaledTime + duration;
    }
    
    private static float GetReceiverFuelTargetRatio(Aircraft aircraft)
    {
        if (aircraft == null)
            return 0f;
        
        // Pls leave me alone this is just for internal testing
#pragma warning disable CS0162 // Unreachable code detected
        // ReSharper disable once HeuristicUnreachableCode
        return OverrideRatio >= 0f ? Mathf.Clamp01(OverrideRatio) : Mathf.Clamp01(aircraft.fuelLevel);
#pragma warning restore CS0162 // Unreachable code detected
    }
    
    private static float GetFuelMissingToSortieTarget(Aircraft aircraft)
    {
        if (aircraft == null)
            return 0f;
        
        var targetRatio = GetReceiverFuelTargetRatio(aircraft);
        var missing = 0f;
        foreach (var tank in aircraft.GetFuelTanks())
        {
            if (tank == null)
                continue;
            
            var capacity = Mathf.Max(0f, tank.GetCapacity());
            if (capacity <= Epsilon)
                continue;
            
            var current = Mathf.Clamp(tank.GetLevel(), 0f, capacity);
            var target = capacity * targetRatio;
            if (target > current)
                missing += target - current;
        }
        
        return Mathf.Max(0f, missing);
    }
    
    private static bool HasEligibleMissingAmmo(Aircraft aircraft)
    {
        if (aircraft == null || aircraft.weaponStations == null)
            return false;
        
        foreach (var station in aircraft.weaponStations)
        {
            if (station == null || station.WeaponInfo == null)
                continue;
            
            var info = station.WeaponInfo;
            if (info.cargo || info.nuclear || info.massPerRound <= 0f)
                continue;
            
            var missing = Math.Max(0, station.FullAmmo - station.GetAmmoTotal());
            if (missing > 0)
                return true;
        }
        
        return false;
    }
    
    private static bool PerformAirborneRearm(ProviderState provider, Aircraft target)
    {
        if (provider.Aircraft == null || target == null || target.Player == null || target.weaponStations == null)
            return false;
        
        var successfulRearm = false;
        var transferMultiplier = GetAmmoTransferMultiplier();
        var availablePhysicalMass = Mathf.Max(0f, provider.AmmoRemainingKg);
        var availableTransferMass = availablePhysicalMass * transferMultiplier;
        var fundsRemaining = target.Player.Allocation;
        var stations = new int[target.weaponStations.Count];
        var transferCost = 0f;
        
        for (var i = 0; i < target.weaponStations.Count; i++)
        {
            var station = target.weaponStations[i];
            if (station == null || station.WeaponInfo == null)
                continue;
            
            var info = station.WeaponInfo;
            if (info.cargo || info.massPerRound <= 0f)
                continue;
            
            var ammoTotal = station.GetAmmoTotal();
            var missing = Math.Max(0, station.FullAmmo - ammoTotal);
            if (missing <= 0)
                continue;
            
            if (info.nuclear)
            {
                stations[i] = -3;
                continue;
            }
            
            var bySupply = SafeFloorRounds(availableTransferMass, info.massPerRound);
            var byFunds = info.costPerRound > 0f
                ? SafeFloorRounds(fundsRemaining, info.costPerRound)
                : int.MaxValue;
            
            if (byFunds <= 0)
                stations[i] = -2;
            
            if (bySupply <= 0)
                stations[i] = -1;
            
            var rounds = Math.Min(missing, Math.Min(bySupply, byFunds));
            if (rounds <= 0)
                continue;
            
            stations[i] = rounds;
            var transferredMass = rounds * info.massPerRound;
            var physicalMassUsed = transferredMass / transferMultiplier;
            var cost = rounds * info.costPerRound;
            availableTransferMass = Mathf.Max(0f, availableTransferMass - transferredMass);
            availablePhysicalMass = Mathf.Max(0f, availablePhysicalMass - physicalMassUsed);
            fundsRemaining -= cost;
            successfulRearm = true;
            transferCost += cost;
        }
        
        if (successfulRearm)
        {
            if (target.NetworkHQ != null)
                target.SuccessfulSortie();
            
            if (transferCost > 0f)
            {
                RewardAirborneRearm(provider, target, transferCost);
                target.Player.AddAllocation(-transferCost);
            }
            
            provider.AmmoRemainingKg = availablePhysicalMass;
        }
        
        target.RpcRearm(new RearmEventArgs
        {
            Rearmer = provider.Aircraft,
            Stations = stations
        });
        
        return successfulRearm;
    }
    
    private static void PerformAirborneService(ProviderState provider, Aircraft target, ServiceNeed need)
    {
        if (need.NeedsAmmo && Plugin.EnableAmmoRearm.Value && provider.AmmoRemainingKg > Epsilon)
            if (PerformAirborneRearm(provider, target))
                StartRearmCooldown(target);
        
        if (need.NeedsFuel && Plugin.EnableFuelRefuel.Value && provider.GetFuelAvailability().Total > Epsilon)
            PerformAirborneRefuel(provider, target);
    }
    
    private static void PerformAirborneRefuel(ProviderState provider, Aircraft target)
    {
        if (provider.Aircraft == null || target == null || target.Player == null)
            return;
        
        var transferMultiplier = GetFuelTransferMultiplier();
        var targetRatio = GetReceiverFuelTargetRatio(target);
        var missing = GetFuelMissingToSortieTarget(target);
        var availability = provider.GetFuelAvailability();
        var transferAvailable = availability.Total * transferMultiplier;
        var requested = Mathf.Min(missing, transferAvailable);
        if (requested <= Epsilon)
            return;
        
        var physicalRequested = requested / transferMultiplier;
        var willFullyRefill = requested >= missing - 0.05f;
        var normalSortieTarget = Mathf.Abs(targetRatio - Mathf.Clamp01(target.fuelLevel)) <= 0.0001f;
        var useVanillaReceiverSync = willFullyRefill && normalSortieTarget;
        if (!target.LocalSim && !useVanillaReceiverSync)
            if (!AirResupplyNetworking.TrySendFuelTransfer(target, provider.Aircraft, requested, targetRatio,
                    willFullyRefill))
                return;
        
        var predictedCargoUsed = Mathf.Min(provider.FuelCargoRemaining, physicalRequested);
        var predictedOnboardUsed = Mathf.Max(0f, physicalRequested - predictedCargoUsed);
        if (predictedOnboardUsed > Epsilon && provider.Aircraft.Player != null && !provider.Aircraft.LocalSim &&
            !AirResupplyNetworking.TrySendProviderFuelDrain(provider.Aircraft, predictedOnboardUsed))
            return;
        
        var applied = ApplyFuelToAircraft(target, requested, targetRatio);
        if (applied <= Epsilon)
            return;
        
        var remainingDebit = applied / transferMultiplier;
        var cargoUsed = Mathf.Min(provider.FuelCargoRemaining, remainingDebit);
        provider.FuelCargoRemaining = Mathf.Max(0f, provider.FuelCargoRemaining - cargoUsed);
        remainingDebit -= cargoUsed;
        if (remainingDebit > Epsilon)
            DrainOnboardFuel(provider.Aircraft, remainingDebit);
        
        var fullRefill = GetFuelMissingToSortieTarget(target) <= 0.05f;
        // Vanilla refuel RPC can only handle full refills, use custom RPC for partial refills
        if (!target.LocalSim)
        {
            if (useVanillaReceiverSync)
                target.Refuel(provider.Aircraft);
        }
        else
        {
            AirResupplyNetworking.ReportLocalFuelTransfer(provider.Aircraft, applied);
        }
        
        RewardAirborneRefuel(provider, target, applied);
        if (fullRefill)
            SetNeedsFuel(target, false);
    }
    
    internal static void SetNeedsFuel(Aircraft aircraft, bool value)
    {
        if (aircraft != null)
            aircraft.needsFuel = value;
    }
    
    internal static float ApplyFuelToAircraft(Aircraft aircraft, float requestedLitres, float targetRatio)
    {
        if (aircraft == null || requestedLitres <= Epsilon)
            return 0f;
        
        var tanks = aircraft.GetFuelTanks();
        targetRatio = Mathf.Clamp01(targetRatio);
        var totalDeficit = 0f;
        foreach (var tank in tanks)
        {
            if (tank == null)
                continue;
            
            var capacity = Mathf.Max(0f, tank.GetCapacity());
            if (capacity <= Epsilon)
                continue;
            
            var current = Mathf.Clamp(tank.GetLevel(), 0f, capacity);
            totalDeficit += Mathf.Max(0f, capacity * targetRatio - current);
        }
        
        var requested = Mathf.Min(Mathf.Max(0f, requestedLitres), totalDeficit);
        if (requested <= Epsilon || totalDeficit <= Epsilon)
            return 0f;
        
        var applied = 0f;
        foreach (var tank in tanks)
        {
            if (tank == null)
                continue;
            
            var capacity = Mathf.Max(0f, tank.GetCapacity());
            if (capacity <= Epsilon)
                continue;
            
            var current = Mathf.Clamp(tank.GetLevel(), 0f, capacity);
            var deficit = Mathf.Max(0f, capacity * targetRatio - current);
            if (deficit <= Epsilon)
                continue;
            
            var add = requested * (deficit / totalDeficit);
            var newRatio = Mathf.Clamp01((current + add) / capacity);
            
            if (tank.Refuel(newRatio))
                applied += Mathf.Max(0f, tank.GetLevel() - current);
        }
        
        return applied;
    }
    
    internal static void DrainOnboardFuel(Aircraft aircraft, float requestedLitres)
    {
        if (aircraft == null || requestedLitres <= Epsilon)
            return;
        
        var tanks = aircraft.GetFuelTanks();
        var remaining = requestedLitres;
        var drained = 0f;
        var externalAvailable = SumFuel(tanks, true);
        if (externalAvailable > Epsilon)
        {
            var externalDraw = Mathf.Min(remaining, externalAvailable);
            drained += DrainFuelGroup(tanks, externalDraw, true);
            remaining = Mathf.Max(0f, requestedLitres - drained);
        }
        
        if (remaining <= Epsilon)
            return;
        
        var internalFuel = SumFuel(tanks, false);
        var reserve = GetInternalFuelReserveLitres(tanks);
        var internalAvailable = Mathf.Max(0f, internalFuel - reserve);
        if (!(internalAvailable > Epsilon))
            return;
        
        var internalDraw = Mathf.Min(remaining, internalAvailable);
        DrainFuelGroup(tanks, internalDraw, false);
    }
    
    private static float DrainFuelGroup(List<FuelTank> tanks, float requested, bool external)
    {
        if (requested <= Epsilon)
            return 0f;
        
        var available = SumFuel(tanks, external);
        if (available <= Epsilon)
            return 0f;
        
        var draw = Mathf.Min(requested, available);
        var drained = 0f;
        
        foreach (var tank in tanks)
        {
            if (tank == null || IsExternalFuelTank(tank) != external)
                continue;
            
            var level = Mathf.Max(0f, tank.GetLevel());
            if (level <= Epsilon)
                continue;
            
            var amount = draw * (level / available);
            var before = tank.GetLevel();
            tank.UseFuel(amount);
            drained += Mathf.Max(0f, before - tank.GetLevel());
        }
        
        return drained;
    }
    
    private static float SumFuel(List<FuelTank> tanks, bool external)
    {
        var total = 0f;
        foreach (var tank in tanks)
        {
            if (tank == null || IsExternalFuelTank(tank) != external)
                continue;
            
            total += Mathf.Max(0f, tank.GetLevel());
        }
        
        return total;
    }
    
    private static float GetInternalFuelReserveLitres(List<FuelTank> tanks)
    {
        var internalCapacity = SumFuelCapacity(tanks, false);
        var reserveRatio = Mathf.Clamp(Plugin.ProviderInternalFuelReservePercent.Value, 0f, 100f) * 0.01f;
        return internalCapacity * reserveRatio;
    }
    
    private static float SumFuelCapacity(List<FuelTank> tanks, bool external)
    {
        var total = 0f;
        foreach (var tank in tanks)
        {
            if (tank == null || IsExternalFuelTank(tank) != external)
                continue;
            
            total += Mathf.Max(0f, tank.GetCapacity());
        }
        
        return total;
    }
    
    private static bool IsExternalFuelTank(FuelTank tank)
    {
        if (tank == null)
            return false;
        
        var type = tank.GetType();
        if (ExternalFuelTankTypeCache.TryGetValue(type, out var cached))
            return cached;
        
        var external = false;
        foreach (var tankInterface in type.GetInterfaces())
            if (string.Equals(tankInterface.FullName, "AryxWeaponryExpansion.IExternalFuelTank",
                    StringComparison.Ordinal))
            {
                external = true;
                break;
            }
        
        ExternalFuelTankTypeCache[type] = external;
        return external;
    }
    
    private static ProviderFuelAvailability GetProviderFuelAvailability(Aircraft aircraft, float cargoFuel,
        float cargoFuelMax)
    {
        var cargo = Mathf.Max(0f, cargoFuel);
        var cargoMax = Mathf.Max(0f, cargoFuelMax);
        if (aircraft == null)
            return new ProviderFuelAvailability(cargo, 0f, 0f, cargoMax);
        
        var tanks = aircraft.GetFuelTanks();
        var external = SumFuel(tanks, true);
        var internalFuel = SumFuel(tanks, false);
        var internalCapacity = SumFuelCapacity(tanks, false);
        var reserveRatio = Mathf.Clamp(Plugin.ProviderInternalFuelReservePercent.Value, 0f, 100f) * 0.01f;
        var reserve = internalCapacity * reserveRatio;
        var internalUsable = Mathf.Max(0f, internalFuel - reserve);
        var externalMax = SumFuelCapacity(tanks, true);
        var internalMax = Mathf.Max(0f, internalCapacity - reserve);
        var totalMax = cargoMax + externalMax + internalMax;
        return new ProviderFuelAvailability(cargo, external, internalUsable, totalMax);
    }
    
    private static float GetAmmoTransferMultiplier() => Mathf.Max(0.01f, Plugin.AmmoTransferMultiplier.Value);
    private static float GetFuelTransferMultiplier() => Mathf.Max(0.01f, Plugin.FuelTransferMultiplier.Value);
    private static float GetRearmRewardMultiplier() => Mathf.Max(0f, Plugin.RearmRewardMultiplier.Value);
    private static float GetRefuelRewardPer1000Litres() => Mathf.Max(0f, Plugin.RefuelRewardPer1000Litres.Value);
    
    private static void RewardAirborneRearm(ProviderState provider, Aircraft target, float rearmCost)
    {
        if (provider.Aircraft == null || target == null || rearmCost <= Epsilon)
            return;
        
        var providerPlayer = provider.Aircraft.Player;
        var hq = provider.Aircraft.NetworkHQ;
        if (providerPlayer == null || hq == null)
            return;
        
        var reward = Mathf.Sqrt(rearmCost) * GetRearmRewardMultiplier();
        if (reward <= Epsilon)
            return;
        
        hq.RewardPlayer(providerPlayer, target, reward, reward, FactionHQ.RewardType.Supply);
    }
    
    private static void RewardAirborneRefuel(ProviderState provider, Aircraft target, float litresDelivered)
    {
        if (provider.Aircraft == null || target == null || litresDelivered <= Epsilon)
            return;
        
        var providerPlayer = provider.Aircraft.Player;
        var hq = provider.Aircraft.NetworkHQ;
        if (providerPlayer == null || hq == null)
            return;
        
        var reward = litresDelivered / 1000f * GetRefuelRewardPer1000Litres();
        if (reward <= Epsilon)
            return;
        
        hq.RewardPlayer(providerPlayer, target, reward, reward, FactionHQ.RewardType.Refuel);
    }
    
    private static int SafeFloorRounds(float available, float perRound)
    {
        if (perRound <= 0f)
            return int.MaxValue;
        
        if (available <= 0f)
            return 0;
        
        var rounds = Math.Floor((double)available / perRound);
        if (rounds >= int.MaxValue)
            return int.MaxValue;
        
        return rounds <= 0d ? 0 : (int)rounds;
    }
    
    private static void TryGroundRefill(ProviderState provider)
    {
        if (provider.Aircraft == null)
            return;
        
        var needsAmmo = Plugin.RefillVirtualAmmoOnGround.Value && provider.AmmoMaxKg > Epsilon &&
                        provider.AmmoRemainingKg < provider.AmmoMaxKg - Epsilon;
        var needsFuel = Plugin.RefillVirtualFuelOnGround.Value && provider.FuelCargoMax > Epsilon &&
                        provider.FuelCargoRemaining < provider.FuelCargoMax - Epsilon;
        if (!needsAmmo && !needsFuel)
            return;
        
        var aircraft = provider.Aircraft;
        if (aircraft.NetworkHQ == null ||
            !aircraft.NetworkHQ.RearmMissionController.TryGetRearmer(aircraft, out var rearmer) || rearmer == null)
            return;
        
        if (needsAmmo)
            provider.AmmoRemainingKg = provider.AmmoMaxKg;
        
        if (needsFuel)
            provider.FuelCargoRemaining = provider.FuelCargoMax;
    }
    
    private static void SendMapStatusStates()
    {
        foreach (var recipient in TrackedAircraft)
        {
            if (!IsValidParticipant(recipient) || recipient.Player?.Owner == null)
                continue;
            
            foreach (var provider in Providers.Values)
            {
                var providerAircraft = provider.Aircraft;
                if (providerAircraft == null || providerAircraft.disabled || !providerAircraft.persistentID.IsValid ||
                    providerAircraft.NetworkHQ == null || providerAircraft.NetworkHQ != recipient.NetworkHQ)
                    continue;
                
                var fuel = provider.GetFuelAvailability();
                var ammoPercent = QuantiseMapPercent(provider.AmmoRemainingKg, provider.AmmoMaxKg);
                var fuelPercent = QuantiseMapPercent(fuel.Total, fuel.TotalMax);
                byte flags = 0;
                if (ammoPercent > 0)
                    flags |= AirResupplyMapStatusMessage.AmmoFlag;
                if (fuelPercent > 0)
                    flags |= AirResupplyMapStatusMessage.FuelFlag;
                AirResupplyNetworking.TrySendMapStatus(recipient.Player.Owner, new AirResupplyMapStatusMessage
                {
                    ProviderId = providerAircraft.persistentID,
                    Flags = flags,
                    AmmoPercent = ammoPercent,
                    FuelPercent = fuelPercent
                });
            }
        }
    }
    
    private static byte QuantiseMapPercent(float current, float maximum)
    {
        if (current <= Epsilon || maximum <= Epsilon)
            return 0;
        
        var rounded = Mathf.RoundToInt(Mathf.Clamp01(current / maximum) * 100f);
        return (byte)Mathf.Clamp(Mathf.Max(1, rounded), 1, 100);
    }
    
    private static void SendHudStates()
    {
        var maxDisplayRange = Mathf.Max(Plugin.ServiceRange.Value, Plugin.HudDisplayRange.Value);
        foreach (var aircraft in TrackedAircraft)
        {
            if (!IsValidParticipant(aircraft) || aircraft.Player?.Owner == null)
                continue;
            
            var ammoTransferMultiplier = GetAmmoTransferMultiplier();
            var fuelTransferMultiplier = GetFuelTransferMultiplier();
            if (Providers.TryGetValue(aircraft, out var ownProvider))
            {
                var fuel = ownProvider.GetFuelAvailability();
                AirResupplyNetworking.TrySendHudState(aircraft.Player.Owner, new AirResupplyHudStateMessage
                {
                    Visible = true,
                    Mode = AirResupplyHudStateMessage.ProviderMode,
                    ProviderName = aircraft.unitName ?? "Supplier",
                    AmmoRemainingKg = ownProvider.AmmoRemainingKg * ammoTransferMultiplier,
                    AmmoMaxKg = ownProvider.AmmoMaxKg * ammoTransferMultiplier,
                    FuelCargoRemainingL = ownProvider.FuelCargoRemaining * fuelTransferMultiplier,
                    FuelExternalRemainingL = fuel.External * fuelTransferMultiplier,
                    FuelInternalRemainingL = fuel.InternalUsable * fuelTransferMultiplier,
                    FuelTotalRemainingL = fuel.Total * fuelTransferMultiplier,
                    FuelTotalMaxL = fuel.TotalMax * fuelTransferMultiplier,
                    RearmCooldownRemaining = 0f
                });
                
                continue;
            }
            
            if (!IsValidReceiver(aircraft))
            {
                AirResupplyNetworking.TrySendHudState(aircraft.Player.Owner, AirResupplyHudStateMessage.Hidden);
                continue;
            }
            
            ProviderState? nearest = null;
            var nearestDistance = float.MaxValue;
            
            foreach (var candidate in Providers.Values)
            {
                if (candidate.Aircraft == null || candidate.Aircraft == aircraft || candidate.Aircraft.disabled ||
                    candidate.Aircraft.NetworkHQ == null || candidate.Aircraft.NetworkHQ != aircraft.NetworkHQ)
                    continue;
                
                var distance = Distance(aircraft, candidate.Aircraft);
                if (distance <= maxDisplayRange && distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }
            
            if (nearest is null)
            {
                AirResupplyNetworking.TrySendHudState(aircraft.Player.Owner, AirResupplyHudStateMessage.Hidden);
                continue;
            }
            
            var fuelAvailability = nearest.GetFuelAvailability();
            var progress = 0f;
            var servicing = false;
            var latched = false;
            if (ReceiverSessions.TryGetValue(aircraft, out var session) && session.Provider == nearest.Aircraft)
            {
                progress = session.Progress;
                servicing = !session.Latched;
                latched = session.Latched;
            }
            
            AirResupplyNetworking.TrySendHudState(aircraft.Player.Owner, new AirResupplyHudStateMessage
            {
                Visible = true,
                Mode = AirResupplyHudStateMessage.ReceiverMode,
                ProviderName = nearest.Aircraft.definition?.unitName ?? nearest.Aircraft.unitName ?? "Supplier",
                Distance = nearestDistance,
                ServiceRange = Mathf.Max(1f, Plugin.ServiceRange.Value),
                Progress = progress,
                ServiceTime = Mathf.Max(0.1f, Plugin.ServiceTime.Value),
                Servicing = servicing,
                Latched = latched,
                AmmoRemainingKg = nearest.AmmoRemainingKg * ammoTransferMultiplier,
                AmmoMaxKg = nearest.AmmoMaxKg * ammoTransferMultiplier,
                FuelCargoRemainingL = nearest.FuelCargoRemaining * fuelTransferMultiplier,
                FuelExternalRemainingL = fuelAvailability.External * fuelTransferMultiplier,
                FuelInternalRemainingL = fuelAvailability.InternalUsable * fuelTransferMultiplier,
                FuelTotalRemainingL = fuelAvailability.Total * fuelTransferMultiplier,
                FuelTotalMaxL = fuelAvailability.TotalMax * fuelTransferMultiplier,
                RearmCooldownRemaining = GetRearmCooldownRemaining(aircraft)
            });
        }
    }
    
    private static string Describe(Aircraft aircraft)
    {
        if (aircraft == null)
            return "(null)";
        
        var key = aircraft.definition != null ? aircraft.definition.jsonKey : "?";
        return $"{aircraft.unitName}[{key}]#{aircraft.persistentID.Id}";
    }
    
    private sealed class ProviderState
    {
        internal readonly Aircraft Aircraft;
        internal float AmmoMaxKg;
        internal float AmmoRemainingKg;
        internal float FuelCargoMax;
        internal float FuelCargoRemaining;
        private bool _initialised;
        
        internal ProviderState(Aircraft aircraft)
        {
            Aircraft = aircraft;
        }
        
        internal ProviderFuelAvailability GetFuelAvailability() =>
            GetProviderFuelAvailability(Aircraft, FuelCargoRemaining, FuelCargoMax);
        
        internal void RefreshCargoCapacity()
        {
            if (Aircraft == null || Aircraft.weaponStations == null)
                return;
            
            var newAmmoMax = 0f;
            var newFuelMax = 0f;
            
            foreach (var station in Aircraft.weaponStations)
            {
                if (station?.Weapons == null)
                    continue;
                
                foreach (var weapon in station.Weapons)
                {
                    var cargo = weapon as MountedCargo;
                    if (cargo == null || cargo.GetAmmoLoaded() <= 0 || weapon.info == null)
                        continue;
                    
                    var amount = Mathf.Max(0f, weapon.info.massPerRound);
                    if (amount <= Epsilon)
                        continue;
                    
                    var isFuelCargo = cargo.cargo != null &&
                                      string.Equals(cargo.cargo.code, "FUEL", StringComparison.OrdinalIgnoreCase);
                    if (isFuelCargo)
                        newFuelMax += amount;
                    else if (weapon.info.cargo && weapon.info.rearmGround) // Exclude naval only resupply containers
                        newAmmoMax += amount;
                }
            }
            
            var oldAmmoMax = AmmoMaxKg;
            var oldFuelMax = FuelCargoMax;
            if (!_initialised)
            {
                AmmoRemainingKg = newAmmoMax;
                FuelCargoRemaining = newFuelMax;
                _initialised = true;
            }
            else
            {
                ApplyCapacityChange(ref AmmoRemainingKg, oldAmmoMax, newAmmoMax);
                ApplyCapacityChange(ref FuelCargoRemaining, oldFuelMax, newFuelMax);
            }
            
            AmmoMaxKg = newAmmoMax;
            FuelCargoMax = newFuelMax;
        }
        
        private static void ApplyCapacityChange(ref float remaining, float oldMax, float newMax)
        {
            if (newMax > oldMax)
                remaining += newMax - oldMax;
            remaining = Mathf.Clamp(remaining, 0f, newMax);
        }
    }
    
    private sealed class ReceiverSession
    {
        internal bool Latched;
        internal float Progress;
        internal Aircraft Provider = null!;
    }
    
    private readonly struct ServiceNeed
    {
        internal readonly bool NeedsAmmo;
        private readonly float _missingFuel;
        
        internal bool NeedsFuel => _missingFuel > Epsilon;
        internal bool Any => NeedsAmmo || NeedsFuel;
        
        internal ServiceNeed(bool needsAmmo, float missingFuel)
        {
            NeedsAmmo = needsAmmo;
            _missingFuel = Mathf.Max(0f, missingFuel);
        }
    }
    
    private readonly struct ProviderFuelAvailability
    {
        internal readonly float External;
        internal readonly float InternalUsable;
        internal readonly float TotalMax;
        
        // ReSharper disable once ReplaceWithFieldKeyword
        private readonly float _cargo;
        
        private float Onboard => External + InternalUsable;
        internal float Total => _cargo + Onboard;
        
        internal ProviderFuelAvailability(float cargo, float external, float internalUsable, float totalMax)
        {
            _cargo = Mathf.Max(0f, cargo);
            External = Mathf.Max(0f, external);
            InternalUsable = Mathf.Max(0f, internalUsable);
            TotalMax = Mathf.Max(0f, totalMax);
        }
    }
}