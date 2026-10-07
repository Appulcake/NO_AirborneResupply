using System;
using Mirage;
using NuclearOption.Networking;
using UnityEngine;

namespace NO_AR;

[NetworkMessage]
public struct FuelTransferMessage
{
    public float Litres;
    public float TargetRatio;
    public bool FullRefill;
    [MaxLength(128)] public string ProviderName;
}

[NetworkMessage]
public struct ProviderFuelDrainMessage
{
    public float Litres;
}

[NetworkMessage]
public struct AirResupplyHudStateMessage
{
    internal const byte ProviderMode = 1;
    internal const byte ReceiverMode = 2;
    public bool Visible;
    public byte Mode;
    [MaxLength(128)] public string ProviderName;
    public float Distance;
    public float ServiceRange;
    public float Progress;
    public float ServiceTime;
    public bool Servicing;
    public bool Latched;
    public float AmmoRemainingKg;
    public float AmmoMaxKg;
    public float FuelCargoRemainingL;
    public float FuelExternalRemainingL;
    public float FuelInternalRemainingL;
    public float FuelTotalRemainingL;
    public float FuelTotalMaxL;
    public float RearmCooldownRemaining;
    
    internal static AirResupplyHudStateMessage Hidden => new()
    {
        Visible = false
    };
}

[NetworkMessage]
public struct AirResupplyMapStatusMessage
{
    internal const byte AmmoFlag = 1 << 0;
    internal const byte FuelFlag = 1 << 1;
    internal const byte SupplyMask = AmmoFlag | FuelFlag;
    public PersistentID ProviderId;
    public byte Flags;
    public byte AmmoPercent;
    public byte FuelPercent;
}

[NetworkMessage]
public struct ProviderPolicyRequestMessage
{
    public byte Version;
}

[NetworkMessage]
public struct ProviderPolicyMessage
{
    [MaxLength(2048)] public string ProviderAircraftJsonKeys;
    public bool EnabledByDefault;
}

[NetworkMessage]
public struct ProviderPreferenceMessage
{
    [MaxLength(128)] public string AircraftJsonKey;
    public bool Enabled;
}

[NetworkMessage]
public struct ProviderRoleAssignmentMessage
{
    public PersistentID AircraftId;
    public bool Enabled;
}

internal static class AirResupplyNetworking
{
    private static bool _customMessagingAvailable = true;
    private static bool _loggedSendFailure;
    private static NetworkServer? _lifecycleServer;
    private static NetworkClient? _lifecycleClient;
    private static NetworkServer? _registeredServer;
    
    internal static void RegisterClientHandlers(NetworkClient client)
    {
        AirResupplyHud.Clear();
        AirResupplyMapStatus.Clear();
        if (!MirageSerializerBootstrap.Ready)
        {
            _customMessagingAvailable = false;
            Plugin.Logger.LogError("Custom Mirage serializers are unavailable, HUD/map status and partial refuel " +
                                "networking are disabled.");
            return;
        }
        
        if (client == null)
            return;
        
        try
        {
            client.MessageHandler.RegisterHandler<FuelTransferMessage>(OnFuelTransfer);
            client.MessageHandler.RegisterHandler<ProviderFuelDrainMessage>(OnProviderFuelDrain);
            client.MessageHandler.RegisterHandler<AirResupplyHudStateMessage>(AirResupplyHud.OnStateReceived);
            client.MessageHandler.RegisterHandler<AirResupplyMapStatusMessage>(AirResupplyMapStatus.OnStateReceived);
            client.MessageHandler.RegisterHandler<ProviderPolicyMessage>(ProviderSelectionUi.OnServerPolicyReceived);
            client.MessageHandler.RegisterHandler<ProviderRoleAssignmentMessage>(ProviderClientRoleState.OnAssignment);
            _customMessagingAvailable = true;
            ProviderClientRoleState.Clear();
        }
        catch (Exception ex)
        {
            _customMessagingAvailable = false;
            Plugin.Logger.LogError("Failed to register custom Mirage message handlers.\n" + ex);
        }
    }
    
    internal static void AttachNetworkLifecycle(NetworkServer server, NetworkClient client)
    {
        if (server != null && !ReferenceEquals(_lifecycleServer, server))
        {
            if (_lifecycleServer != null)
            {
                _lifecycleServer.Started.RemoveListener(OnServerStarted);
                _lifecycleServer.Stopped.RemoveListener(OnServerStopped);
            }
            _lifecycleServer = server;
            _lifecycleServer.Started.AddListener(OnServerStarted);
            _lifecycleServer.Stopped.AddListener(OnServerStopped);
            if (_lifecycleServer.Active)
                RegisterServerHandlers(_lifecycleServer);
        }
        
        if (client == null || ReferenceEquals(_lifecycleClient, client))
            return;
        
        if (_lifecycleClient != null)
            _lifecycleClient.Authenticated.RemoveListener(OnClientAuthenticated);
        _lifecycleClient = client;
        _lifecycleClient.Authenticated.AddListener(OnClientAuthenticated);
    }
    
    private static void OnServerStarted()
    {
        if (_lifecycleServer != null)
            RegisterServerHandlers(_lifecycleServer);
    }
    
    private static void OnServerStopped()
    {
        _registeredServer = null;
    }
    
    private static void OnClientAuthenticated(INetworkPlayer _)
    {
        RequestProviderPolicy();
    }
    
    private static void RegisterServerHandlers(NetworkServer server)
    {
        if (server == null || !server.Active || ReferenceEquals(_registeredServer, server) || !MirageSerializerBootstrap.Ready)
            return;
        
        if (server.MessageHandler is not IMessageReceiver receiver)
        {
            Plugin.Logger.LogWarning("Server started, but Mirage MessageHandler is not ready. " +
                                  "Provider role handlers were not registered.");
            return;
        }
        
        try
        {
            receiver.RegisterHandler((MessageDelegateWithPlayer<ProviderPolicyRequestMessage>)OnProviderPolicyRequest);
            receiver.RegisterHandler((MessageDelegateWithPlayer<ProviderPreferenceMessage>)OnProviderPreference);
            _registeredServer = server;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError("Failed to register server sided custom Mirage handlers.\n" + ex);
        }
    }
    
    private static void RequestProviderPolicy()
    {
        var client = _lifecycleClient;
        if (client == null || !client.Active || !_customMessagingAvailable)
            return;
        
        try
        {
            client.Send(new ProviderPolicyRequestMessage
            {
                Version = 1
            });
        }
        catch (Exception ex)
        {
            DisableCustomMessaging(nameof(ProviderPolicyRequestMessage), ex);
        }
    }
    
    internal static bool TrySendProviderPreference(string aircraftJsonKey, bool enabled)
    {
        if (string.IsNullOrEmpty(aircraftJsonKey) || !_customMessagingAvailable)
            return false;
        
        var manager = NetworkManagerNuclearOption.i;
        var client = manager != null ? manager.Client : null;
        if (client == null || !client.Active)
            return false;
        
        try
        {
            client.Send(new ProviderPreferenceMessage
            {
                AircraftJsonKey = aircraftJsonKey,
                Enabled = enabled
            });
            return true;
        }
        catch (Exception ex)
        {
            DisableCustomMessaging(nameof(ProviderPreferenceMessage), ex);
            return false;
        }
    }
    
    internal static bool TrySendProviderRoleAssignment(INetworkPlayer? player, PersistentID aircraftId, bool enabled)
    {
        if (player == null || !aircraftId.IsValid || !_customMessagingAvailable)
            return false;
        
        try
        {
            player.Send(new ProviderRoleAssignmentMessage
            {
                AircraftId = aircraftId,
                Enabled = enabled
            });
            return true;
        }
        catch (Exception ex)
        {
            DisableCustomMessaging(nameof(ProviderRoleAssignmentMessage), ex);
            return false;
        }
    }
    
    private static void OnProviderPolicyRequest(INetworkPlayer? sender, ProviderPolicyRequestMessage _)
    {
        if (sender == null || !_customMessagingAvailable)
            return;
        
        try
        {
            sender.Send(new ProviderPolicyMessage
            {
                ProviderAircraftJsonKeys = ProviderRoleManager.GetConfiguredWhitelistRaw(),
                EnabledByDefault = Plugin.ProviderEnabledByDefault.Value
            });
        }
        catch (Exception ex)
        {
            DisableCustomMessaging(nameof(ProviderPolicyMessage), ex);
        }
    }
    
    private static void OnProviderPreference(INetworkPlayer? sender, ProviderPreferenceMessage message)
    {
        if (sender == null || !sender.TryGetPlayer<Player>(out var player) || player == null)
            return;
        
        ProviderRoleManager.ReceivePreference(player, message.AircraftJsonKey ?? string.Empty, message.Enabled);
    }
    
    internal static bool TrySendFuelTransfer(Aircraft receiver, Aircraft provider, float litres, float targetRatio, bool fullRefill)
    {
        if (receiver == null || receiver.Player?.Owner == null || litres <= 0f || !_customMessagingAvailable)
            return false;
        
        var message = new FuelTransferMessage
        {
            Litres = litres,
            TargetRatio = Mathf.Clamp01(targetRatio),
            FullRefill = fullRefill,
            ProviderName = provider != null ? provider.unitName : "airborne supplier"
        };
        
        try
        {
            receiver.Player.Owner.Send(message);
            return true;
        }
        catch (Exception ex)
        {
            DisableCustomMessaging(nameof(FuelTransferMessage), ex);
            return false;
        }
    }
    
    internal static bool TrySendProviderFuelDrain(Aircraft provider, float litres)
    {
        if (provider == null || provider.Player?.Owner == null || litres <= 0f || !_customMessagingAvailable)
            return false;
        
        var message = new ProviderFuelDrainMessage
        {
            Litres = litres
        };
        
        try
        {
            provider.Player.Owner.Send(message);
            return true;
        }
        catch (Exception ex)
        {
            DisableCustomMessaging(nameof(ProviderFuelDrainMessage), ex);
            return false;
        }
    }
    
    internal static bool TrySendHudState(INetworkPlayer? player, AirResupplyHudStateMessage state)
    {
        if (player == null || !_customMessagingAvailable)
            return false;
        
        try
        {
            player.Send(state);
            return true;
        }
        catch (Exception ex)
        {
            DisableCustomMessaging(nameof(AirResupplyHudStateMessage), ex);
            return false;
        }
    }
    
    internal static bool TrySendMapStatus(INetworkPlayer? player, AirResupplyMapStatusMessage state)
    {
        if (player == null || !_customMessagingAvailable)
            return false;
        
        try
        {
            player.Send(state);
            return true;
        }
        catch (Exception ex)
        {
            DisableCustomMessaging(nameof(AirResupplyMapStatusMessage), ex);
            return false;
        }
    }
    
    private static void DisableCustomMessaging(string messageName, Exception ex)
    {
        _customMessagingAvailable = false;
        
        if (_loggedSendFailure)
            return;
        
        _loggedSendFailure = true;
        Plugin.Logger.LogError($"Custom Mirage send failed for {messageName}. " +
                            "Custom HUD/map/provider role/partial refuel networking is disabled for this process.\n" + ex);
    }
    
    private static void OnFuelTransfer(FuelTransferMessage message)
    {
        if (!GameManager.GetLocalAircraft(out var aircraft) || message.Litres <= 0f)
            return;
        
        var applied = AirResupplyManager.ApplyFuelToAircraft(aircraft, message.Litres, Mathf.Clamp01(message.TargetRatio));
        if (message.FullRefill)
            AirResupplyManager.SetNeedsFuel(aircraft, false);
        
        ReportLocalFuelTransfer(message.ProviderName, applied);
    }
    
    private static void OnProviderFuelDrain(ProviderFuelDrainMessage message)
    {
        if (!GameManager.GetLocalAircraft(out var aircraft) || message.Litres <= 0f)
            return;
        
        AirResupplyManager.DrainOnboardFuel(aircraft, message.Litres);
    }
    
    internal static void ReportLocalFuelTransfer(Aircraft provider, float applied) =>
        ReportLocalFuelTransfer(provider != null ? provider.unitName : "airborne supplier", applied);
    
    private static void ReportLocalFuelTransfer(string providerName, float applied)
    {
        if (applied <= 0f || SceneSingleton<AircraftActionsReport>.i == null)
            return;
        
        if (string.IsNullOrEmpty(providerName))
            providerName = "airborne supplier";
        
        SceneSingleton<AircraftActionsReport>.i.ReportText(
            $"Refueled {applied:F0} L by {providerName}", 5f);
    }
}