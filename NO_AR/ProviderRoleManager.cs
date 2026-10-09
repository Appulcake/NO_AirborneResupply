using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace NO_AR;

internal static class ProviderRoleManager
{
    private const float RoleResolutionGraceSeconds = 2f;
    private const float PendingPreferenceLifetimeSeconds = 10f;
    private const float LateCurrentSpawnDiscardSeconds = 5f;
    
    internal const byte MinimumInternalFuelReservePercent = 5;
    private static readonly Dictionary<Aircraft, SortieRoleState> Roles = new();
    private static readonly Dictionary<Player, PendingPreference> PendingPreferences = new();
    private static readonly List<Player> PlayersToRemove = [];
    private static string _cachedWhitelistRaw = string.Empty;
    private static HashSet<string> _cachedWhitelist = new(StringComparer.Ordinal);
    
    internal static byte GetDefaultInternalFuelReservePercent() => (byte)Mathf.Clamp(
        Mathf.RoundToInt(Plugin.ProviderInternalFuelReservePercent.Value), MinimumInternalFuelReservePercent, 100);
    
    internal static byte ClampInternalFuelReservePercent(int value) =>
        (byte)Mathf.Clamp(value, MinimumInternalFuelReservePercent, 100);
    
    internal static byte GetInternalFuelReservePercent(Aircraft aircraft)
    {
        if (aircraft != null &&
            Roles.TryGetValue(aircraft, out var state) &&
            state.Resolved)
            return state.InternalFuelReservePercent;
        
        return GetDefaultInternalFuelReservePercent();
    }
    
    internal static float GetInternalFuelReserveRatio(Aircraft aircraft) =>
        GetInternalFuelReservePercent(aircraft) * 0.01f;
    
    internal static void RegisterAircraftRole(Aircraft aircraft)
    {
        if (aircraft == null || !aircraft.IsServer || Roles.ContainsKey(aircraft))
            return;
        
        var key = aircraft.definition?.jsonKey ?? string.Empty;
        Roles.Add(aircraft, new SortieRoleState
        {
            RegisteredAt = Time.unscaledTime,
            JsonKey = key,
            ProviderCapable = IsConfiguredProviderType(key)
        });
    }
    
    internal static void RemoveAircraftRole(Aircraft aircraft)
    {
        if (ReferenceEquals(aircraft, null))
            return;
        
        Roles.Remove(aircraft);
    }
    
    internal static void ResetServerState()
    {
        Roles.Clear();
        PendingPreferences.Clear();
        PlayersToRemove.Clear();
    }
    
    internal static void Update()
    {
        if (PendingPreferences.Count == 0)
            return;
        
        var now = Time.unscaledTime;
        PlayersToRemove.Clear();
        foreach (var pair in PendingPreferences)
            if (pair.Key == null || now - pair.Value.ReceivedAt > PendingPreferenceLifetimeSeconds)
                PlayersToRemove.Add(pair.Key!);
        
        foreach (var player in PlayersToRemove)
            PendingPreferences.Remove(player);
        
        PlayersToRemove.Clear();
    }
    
    internal static void UpdateAircraftRole(Aircraft aircraft)
    {
        if (aircraft == null || !aircraft.IsServer)
            return;
        
        if (!Roles.TryGetValue(aircraft, out var state))
        {
            RegisterAircraftRole(aircraft);
            if (!Roles.TryGetValue(aircraft, out state))
                return;
        }
        
        var currentKey = aircraft.definition?.jsonKey ?? string.Empty;
        if (!string.Equals(currentKey, state.JsonKey, StringComparison.Ordinal))
        {
            state.JsonKey = currentKey;
            state.ProviderCapable = IsConfiguredProviderType(currentKey);
        }
        
        if (state.Resolved)
        {
            TrySendAssignment(aircraft, state);
            return;
        }
        
        if (aircraft.disabled || !state.ProviderCapable)
        {
            Resolve(aircraft, state, false);
            return;
        }
        
        var age = Mathf.Max(0f, Time.unscaledTime - state.RegisteredAt);
        
        // PlayerRef happens before player aircraft network spawn, wait a bit to make sure it's not accidentally
        // seen as a playerless/AI plane
        if (!aircraft.playerRef.Valid() || aircraft.Player == null)
        {
            if (age >= RoleResolutionGraceSeconds)
                Resolve(aircraft, state, false);
            
            return;
        }
        
        var player = aircraft.Player;
        if (BoteCompatibility.IsBoteShip(aircraft))
        {
            Resolve(aircraft, state, false);
            return;
        }
        
        if (PendingPreferences.TryGetValue(player, out var pending) &&
            string.Equals(pending.JsonKey, state.JsonKey, StringComparison.Ordinal) &&
            Time.unscaledTime - pending.ReceivedAt <= PendingPreferenceLifetimeSeconds)
        {
            PendingPreferences.Remove(player);
            Resolve(aircraft, state, pending.Enabled, pending.InternalFuelReservePercent);
            return;
        }
        
        if (age >= RoleResolutionGraceSeconds)
            Resolve(aircraft, state, Plugin.ProviderEnabledByDefault.Value);
    }
    
    internal static bool IsActiveProvider(Aircraft aircraft) => aircraft != null &&
                                                                Roles.TryGetValue(aircraft, out var state) &&
                                                                state.Resolved && state.Active;
    
    internal static bool CanReceive(Aircraft aircraft)
    {
        if (aircraft == null)
            return false;
        
        var key = aircraft.definition?.jsonKey ?? string.Empty;
        if (!IsConfiguredProviderType(key))
            return true;
        
        return Roles.TryGetValue(aircraft, out var state) && state.Resolved && !state.Active;
    }
    
    internal static void ReceivePreference(Player player, string jsonKey, bool enabled, byte internalFuelReservePercent)
    {
        if (player == null || string.IsNullOrWhiteSpace(jsonKey) || !IsConfiguredProviderType(jsonKey))
            return;
        
        internalFuelReservePercent = ClampInternalFuelReservePercent(internalFuelReservePercent);
        var now = Time.unscaledTime;
        // Handle out-of-order preference messages arriving after aircraft spawn.
        foreach (var pair in Roles)
        {
            var aircraft = pair.Key;
            var state = pair.Value;
            
            if (aircraft == null || aircraft.Player != player ||
                !string.Equals(state.JsonKey, jsonKey, StringComparison.Ordinal))
                continue;
            
            var age = Mathf.Max(0f, now - state.RegisteredAt);
            switch (state.Resolved)
            {
                case false when age <= PendingPreferenceLifetimeSeconds:
                    Resolve(aircraft, state, enabled, internalFuelReservePercent);
                    return;
                
                case true when age <= LateCurrentSpawnDiscardSeconds:
                    return;
            }
        }
        
        PendingPreferences[player] = new PendingPreference(jsonKey, enabled, internalFuelReservePercent, now);
    }
    
    private static bool IsConfiguredProviderType(string jsonKey)
    {
        if (string.IsNullOrEmpty(jsonKey))
            return false;
        
        RefreshWhitelist();
        return _cachedWhitelist.Contains(jsonKey);
    }
    
    internal static string GetConfiguredWhitelistRaw() => Plugin.ProviderAircraftJsonKeys.Value ?? string.Empty;
    
    internal static HashSet<string> ParseProviderKeys(string raw)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
            return result;
        
        var parts = raw.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var key = part.Trim();
            if (key.Length > 0)
                result.Add(key);
        }
        
        return result;
    }
    
    private static void RefreshWhitelist()
    {
        var raw = GetConfiguredWhitelistRaw();
        if (string.Equals(raw, _cachedWhitelistRaw, StringComparison.Ordinal))
            return;
        
        _cachedWhitelistRaw = raw;
        _cachedWhitelist = ParseProviderKeys(raw);
    }
    
    private static void Resolve(Aircraft aircraft, SortieRoleState state, bool active)
    {
        Resolve(aircraft, state, active, GetDefaultInternalFuelReservePercent());
    }
    
    private static void Resolve(Aircraft aircraft, SortieRoleState state, bool active, byte internalFuelReservePercent)
    {
        if (state.Resolved)
            return;
        
        active = active && state.ProviderCapable && aircraft != null && aircraft.Player != null &&
                 aircraft.NetworkHQ != null && AirResupplyNetworking.IsPeerProtocolSupported(aircraft.Player.Owner) &&
                 !BoteCompatibility.IsBoteShip(aircraft);
        state.InternalFuelReservePercent = ClampInternalFuelReservePercent(internalFuelReservePercent);
        state.Resolved = true;
        state.Active = active;
        if (aircraft != null)
            TrySendAssignment(aircraft, state);
    }
    
    private static void TrySendAssignment(Aircraft aircraft, SortieRoleState state)
    {
        if (!state.Resolved || state.AssignmentSent || !state.ProviderCapable || aircraft == null ||
            aircraft.Player?.Owner == null || !aircraft.persistentID.IsValid)
            return;
        
        if (AirResupplyNetworking.TrySendProviderRoleAssignment(aircraft.Player.Owner, aircraft.persistentID,
                state.Active, state.InternalFuelReservePercent))
            state.AssignmentSent = true;
    }
    
    private sealed class SortieRoleState
    {
        internal bool Active;
        internal bool AssignmentSent;
        internal byte InternalFuelReservePercent;
        internal string JsonKey = string.Empty;
        internal bool ProviderCapable;
        internal float RegisteredAt;
        internal bool Resolved;
    }
    
    private readonly struct PendingPreference
    {
        internal readonly string JsonKey;
        internal readonly bool Enabled;
        internal readonly byte InternalFuelReservePercent;
        internal readonly float ReceivedAt;
        
        internal PendingPreference(string jsonKey, bool enabled, byte internalFuelReservePercent, float receivedAt)
        {
            JsonKey = jsonKey;
            Enabled = enabled;
            InternalFuelReservePercent = internalFuelReservePercent;
            ReceivedAt = receivedAt;
        }
    }
}

internal static class ProviderClientRoleState
{
    private static readonly HashSet<PersistentID> ActiveProviderAircraft = [];
    private static readonly Dictionary<PersistentID, byte> InternalFuelReservePercent = new();
    
    // Legacy v1
    internal static void OnAssignment(ProviderRoleAssignmentMessage message)
    {
        if (!message.AircraftId.IsValid)
            return;
        
        InternalFuelReservePercent.Remove(message.AircraftId);
        if (message.Enabled)
            ActiveProviderAircraft.Add(message.AircraftId);
        else
            ActiveProviderAircraft.Remove(message.AircraftId);
    }
    
    // v2
    internal static void OnAssignment(ProviderRoleAssignmentMessageV2 message)
    {
        if (!message.AircraftId.IsValid)
            return;
        
        if (message.Enabled)
        {
            ActiveProviderAircraft.Add(message.AircraftId);
            InternalFuelReservePercent[message.AircraftId] =
                ProviderRoleManager.ClampInternalFuelReservePercent(message.InternalFuelReservePercent);
        }
        else
        {
            ActiveProviderAircraft.Remove(message.AircraftId);
            InternalFuelReservePercent.Remove(message.AircraftId);
        }
    }
    
    internal static bool IsActiveProvider(Aircraft aircraft) => aircraft != null && aircraft.persistentID.IsValid &&
                                                                ActiveProviderAircraft.Contains(aircraft.persistentID);
    
    internal static float GetInternalFuelReserveRatio(Aircraft aircraft)
    {
        if (aircraft != null && aircraft.persistentID.IsValid &&
            InternalFuelReservePercent.TryGetValue(aircraft.persistentID, out var reservePercent))
            return reservePercent * 0.01f;
        
        return ProviderSelectionUi.GetServerDefaultInternalFuelReservePercent() * 0.01f;
    }
    
    internal static void Clear()
    {
        ActiveProviderAircraft.Clear();
        InternalFuelReservePercent.Clear();
    }
}