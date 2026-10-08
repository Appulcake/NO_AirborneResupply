using System;
using System.Collections.Generic;
using Mirage;
using NuclearOption.Networking;
using UnityEngine;

namespace NO_AR;

// Legacy v1 support partial fuel transfer message (server doesn't resolve UnitName any more so just sends "Player")
[NetworkMessage]
public struct FuelTransferMessage
{
    public float Litres;
    public float TargetRatio;
    public bool FullRefill;
    [MaxLength(128)] public string ProviderName;
}

// New v2+ that sends persistent ID of a provider's aircraft, so a client can resolve its proper name
[NetworkMessage]
public struct FuelTransferMessageV2
{
    public float Litres;
    public float TargetRatio;
    public bool FullRefill;
    public PersistentID ProviderId;
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

// Permanent legacy v1 support because I didn't implement the versioning at that point yet so we have to make sure
// future versions support handling v1 clients/servers
[NetworkMessage]
public struct ProviderPolicyRequestMessage
{
    public byte Version;
}

// New v2+ protocol support to handle mismatching versions between client and server
[NetworkMessage]
public struct AirResupplyProtocolStatusMessage
{
    public byte ServerVersion;
    public bool Compatible;
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

internal enum ProviderRewardType : byte
{
    Rearm = 1,
    Refuel = 2,
    SortieAssist = 3
}

[NetworkMessage]
public struct ProviderRewardMessage
{
    public byte Type;
    public float Amount;
}

internal static class AirResupplyNetworking
{
    private const byte LegacyProtocolVersion = 1;
    private const byte ProtocolVersion = 2;
    private static bool _customMessagingAvailable = true;
    private static NetworkServer? _lifecycleServer;
    private static NetworkClient? _lifecycleClient;
    private static NetworkServer? _registeredServer;
    private static readonly HashSet<string> LoggedSendFailures = new(StringComparer.Ordinal);
    private static readonly Dictionary<INetworkPlayer, byte> PeerProtocolVersions = new();
    private static byte _serverProtocolVersion;
    private static bool _clientProtocolCompatible;
    private static bool _protocolWarningPending;
    private static bool _protocolWarningShown;
    
    private static bool ClientSupportsCommonMessages =>
        _serverProtocolVersion == LegacyProtocolVersion || _clientProtocolCompatible;
    
    private static bool TryGetPeerProtocolVersion(INetworkPlayer? player, out byte version)
    {
        version = 0;
        return player != null && PeerProtocolVersions.TryGetValue(player, out version);
    }
    
    internal static bool IsPeerProtocolSupported(INetworkPlayer? player) =>
        TryGetPeerProtocolVersion(player, out var version) &&
        (version == LegacyProtocolVersion || version == ProtocolVersion);
    
    // ReSharper disable once MemberCanBePrivate.Global
    internal static bool IsPeerProtocolCurrent(INetworkPlayer? player) =>
        TryGetPeerProtocolVersion(player, out var version) && version == ProtocolVersion;
    
    internal static bool IsPeerProtocolLegacy(INetworkPlayer? player) =>
        TryGetPeerProtocolVersion(player, out var version) && version == LegacyProtocolVersion;
    
    internal static void RegisterClientHandlers(NetworkClient client)
    {
        ResetClientProtocolState();
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
            client.MessageHandler.RegisterHandler<FuelTransferMessageV2>(OnFuelTransferV2);
            client.MessageHandler.RegisterHandler<ProviderFuelDrainMessage>(OnProviderFuelDrain);
            
            client.MessageHandler.RegisterHandler<AirResupplyHudStateMessage>(OnHudState);
            client.MessageHandler.RegisterHandler<AirResupplyMapStatusMessage>(OnMapStatus);
            client.MessageHandler.RegisterHandler<ProviderRoleAssignmentMessage>(OnProviderRoleAssignment);
            
            client.MessageHandler.RegisterHandler<ProviderPolicyMessage>(OnProviderPolicyReceived);
            client.MessageHandler.RegisterHandler<AirResupplyProtocolStatusMessage>(OnProtocolStatus);
            client.MessageHandler.RegisterHandler<ProviderRewardMessage>(OnProviderReward);
            _customMessagingAvailable = true;
            ProviderClientRoleState.Clear();
        }
        catch (Exception ex)
        {
            _customMessagingAvailable = false;
            Plugin.Logger.LogError("Failed to register custom Mirage message handlers.\n" + ex);
        }
    }
    
    private static void OnHudState(AirResupplyHudStateMessage message)
    {
        if (ClientSupportsCommonMessages)
            AirResupplyHud.OnStateReceived(message);
    }
    
    private static void OnMapStatus(AirResupplyMapStatusMessage message)
    {
        if (ClientSupportsCommonMessages)
            AirResupplyMapStatus.OnStateReceived(message);
    }
    
    private static void OnProviderRoleAssignment(ProviderRoleAssignmentMessage message)
    {
        if (ClientSupportsCommonMessages)
            ProviderClientRoleState.OnAssignment(message);
    }
    
    internal static void AttachNetworkLifecycle(NetworkServer server, NetworkClient client)
    {
        if (server != null && !ReferenceEquals(_lifecycleServer, server))
        {
            if (_lifecycleServer != null)
            {
                _lifecycleServer.Started.RemoveListener(OnServerStarted);
                _lifecycleServer.Stopped.RemoveListener(OnServerStopped);
                _lifecycleServer.Disconnected.RemoveListener(OnServerPlayerDisconnected);
            }
            
            _lifecycleServer = server;
            _lifecycleServer.Started.AddListener(OnServerStarted);
            _lifecycleServer.Stopped.AddListener(OnServerStopped);
            _lifecycleServer.Disconnected.AddListener(OnServerPlayerDisconnected);
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
        PeerProtocolVersions.Clear();
    }
    
    private static void OnClientAuthenticated(INetworkPlayer _)
    {
        RequestProviderPolicy();
    }
    
    private static void OnServerPlayerDisconnected(INetworkPlayer? player)
    {
        if (player != null)
            PeerProtocolVersions.Remove(player);
    }
    
    private static void RegisterServerHandlers(NetworkServer server)
    {
        if (server == null || !server.Active || ReferenceEquals(_registeredServer, server) ||
            !MirageSerializerBootstrap.Ready)
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
                Version = ProtocolVersion
            });
        }
        catch (Exception ex)
        {
            LogSendFailure(nameof(ProviderPolicyRequestMessage), ex);
        }
    }
    
    private static void OnProtocolStatus(AirResupplyProtocolStatusMessage message)
    {
        _serverProtocolVersion = message.ServerVersion;
        _clientProtocolCompatible = message is { Compatible: true, ServerVersion: ProtocolVersion };
        if (_clientProtocolCompatible)
            return;
        
        SetProtocolMismatch(message.ServerVersion);
    }
    
    private static void OnProviderPolicyReceived(ProviderPolicyMessage message)
    {
        if (_serverProtocolVersion == 0)
        {
            // v2+ servers send a v2+ protocol first, then v1, so if v1 arrives first it's a v1 server
            _serverProtocolVersion = LegacyProtocolVersion;
            Plugin.Logger.LogInfo("Connected to Air Resupply protocol v1 server, using legacy compatibility mode.");
            ProviderSelectionUi.OnServerPolicyReceived(message);
            return;
        }
        
        if (_clientProtocolCompatible)
            ProviderSelectionUi.OnServerPolicyReceived(message);
    }
    
    private static void SetProtocolMismatch(byte serverVersion)
    {
        _serverProtocolVersion = serverVersion;
        _clientProtocolCompatible = false;
        _protocolWarningPending = true;
        ProviderSelectionUi.ResetSession();
        ProviderClientRoleState.Clear();
        AirResupplyHud.Clear();
        AirResupplyMapStatus.Clear();
        Plugin.Logger.LogWarning($"Air Resupply protocol mismatch: client is on version {ProtocolVersion}, but " +
                                 $"server is on {serverVersion}. Custom networking is disabled for this server.");
        
        TryShowProtocolWarning();
    }
    
    internal static void TryShowProtocolWarning()
    {
        if (!_protocolWarningPending || _protocolWarningShown)
            return;
        
        var messageUi = SceneSingleton<MessageUI>.i;
        if (messageUi == null)
            return;
        
        messageUi.GameMessage($"Airborne Resupply version mismatch: client v{ProtocolVersion}, " +
                              $"server v{_serverProtocolVersion}. Mod networking is disabled for this server.");
        _protocolWarningShown = true;
        _protocolWarningPending = false;
    }
    
    internal static void ResetClientProtocolState()
    {
        _serverProtocolVersion = 0;
        _clientProtocolCompatible = false;
        _protocolWarningPending = false;
        _protocolWarningShown = false;
    }
    
    internal static bool TrySendProviderPreference(string aircraftJsonKey, bool enabled)
    {
        if (string.IsNullOrEmpty(aircraftJsonKey) || !_customMessagingAvailable || !ClientSupportsCommonMessages)
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
            LogSendFailure(nameof(ProviderPreferenceMessage), ex);
            return false;
        }
    }
    
    internal static bool TrySendProviderRoleAssignment(INetworkPlayer? player, PersistentID aircraftId, bool enabled)
    {
        if (player == null || !aircraftId.IsValid || !_customMessagingAvailable || !IsPeerProtocolSupported(player))
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
            LogSendFailure(nameof(ProviderRoleAssignmentMessage), ex);
            return false;
        }
    }
    
    private static void OnProviderPolicyRequest(INetworkPlayer? sender, ProviderPolicyRequestMessage message)
    {
        if (sender == null || !_customMessagingAvailable)
            return;
        
        PeerProtocolVersions[sender] = message.Version;
        try
        {
            // Legacy protocol
            if (message.Version == LegacyProtocolVersion)
            {
                sender.Send(new ProviderPolicyMessage
                {
                    ProviderAircraftJsonKeys = ProviderRoleManager.GetConfiguredWhitelistRaw(),
                    EnabledByDefault = Plugin.ProviderEnabledByDefault.Value
                });
                return;
            }
            
            if (message.Version >= ProtocolVersion)
            {
                var compatible = message.Version == ProtocolVersion;
                sender.Send(new AirResupplyProtocolStatusMessage
                {
                    ServerVersion = ProtocolVersion,
                    Compatible = compatible
                });
                if (!compatible)
                {
                    Plugin.Logger.LogWarning($"Air Resupply protocol mismatch for {sender}: client version is " +
                                             $"{message.Version}, server version is {ProtocolVersion}.");
                    
                    return;
                }
            }
            else
            {
                return;
            }
            
            sender.Send(new ProviderPolicyMessage
            {
                ProviderAircraftJsonKeys = ProviderRoleManager.GetConfiguredWhitelistRaw(),
                EnabledByDefault = Plugin.ProviderEnabledByDefault.Value
            });
        }
        catch (Exception ex)
        {
            LogSendFailure(nameof(ProviderPolicyMessage), ex);
        }
    }
    
    private static void OnProviderPreference(INetworkPlayer? sender, ProviderPreferenceMessage message)
    {
        if (sender == null || !IsPeerProtocolSupported(sender) || !sender.TryGetPlayer<Player>(out var player) ||
            player == null)
            return;
        
        ProviderRoleManager.ReceivePreference(player, message.AircraftJsonKey ?? string.Empty, message.Enabled);
    }
    
    internal static bool TrySendFuelTransfer(Aircraft receiver, Aircraft provider, float litres, float targetRatio,
        bool fullRefill)
    {
        var owner = receiver != null ? receiver.Player?.Owner : null;
        if (owner == null || litres <= 0f || !_customMessagingAvailable ||
            !TryGetPeerProtocolVersion(owner, out var version))
            return false;
        
        try
        {
            if (version == LegacyProtocolVersion)
            {
                owner.Send(new FuelTransferMessage
                {
                    Litres = litres,
                    TargetRatio = Mathf.Clamp01(targetRatio),
                    FullRefill = fullRefill,
                    ProviderName = provider != null ? provider.unitName : "airborne supplier"
                });
                return true;
            }
            
            if (version != ProtocolVersion)
                return false;
            
            owner.Send(new FuelTransferMessageV2
            {
                Litres = litres,
                TargetRatio = Mathf.Clamp01(targetRatio),
                FullRefill = fullRefill,
                ProviderId = provider != null ? provider.persistentID : PersistentID.None
            });
            return true;
        }
        catch (Exception ex)
        {
            LogSendFailure(
                version == LegacyProtocolVersion ? nameof(FuelTransferMessage) : nameof(FuelTransferMessageV2), ex);
            return false;
        }
    }
    
    internal static bool TrySendProviderFuelDrain(Aircraft provider, float litres)
    {
        if (provider == null || provider.Player?.Owner == null || litres <= 0f || !_customMessagingAvailable ||
            !IsPeerProtocolSupported(provider.Player.Owner))
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
            LogSendFailure(nameof(ProviderFuelDrainMessage), ex);
            return false;
        }
    }
    
    internal static void TrySendHudState(INetworkPlayer? player, AirResupplyHudStateMessage state)
    {
        if (player == null || !_customMessagingAvailable || !IsPeerProtocolSupported(player))
            return;
        
        try
        {
            player.Send(state);
        }
        catch (Exception ex)
        {
            LogSendFailure(nameof(AirResupplyHudStateMessage), ex);
        }
    }
    
    internal static void TrySendMapStatus(INetworkPlayer? player, AirResupplyMapStatusMessage state)
    {
        if (player == null || !_customMessagingAvailable || !IsPeerProtocolSupported(player))
            return;
        
        try
        {
            player.Send(state);
        }
        catch (Exception ex)
        {
            LogSendFailure(nameof(AirResupplyMapStatusMessage), ex);
        }
    }
    
    internal static void TrySendProviderReward(Aircraft provider, ProviderRewardType type, float amount)
    {
        var owner = provider != null ? provider.Player?.Owner : null;
        if (owner == null || amount <= 0f || !_customMessagingAvailable || !IsPeerProtocolCurrent(owner))
            return;
        
        try
        {
            owner.Send(new ProviderRewardMessage
            {
                Type = (byte)type,
                Amount = amount
            });
        }
        catch (Exception ex)
        {
            LogSendFailure(nameof(ProviderRewardMessage), ex);
        }
    }
    
    private static void OnProviderReward(ProviderRewardMessage message)
    {
        if (!_clientProtocolCompatible)
            return;
        
        var report = SceneSingleton<AircraftActionsReport>.i;
        if (report == null || message.Amount <= 0f)
            return;
        
        var text = (ProviderRewardType)message.Type switch
        {
            ProviderRewardType.Rearm => $"Airborne Rearm + {message.Amount:F1}",
            ProviderRewardType.Refuel => $"Airborne Refuel + {message.Amount:F1}",
            ProviderRewardType.SortieAssist => $"Sortie Assist + {message.Amount:F1}",
            _ => null
        };
        
        if (!string.IsNullOrEmpty(text))
            report.ReportText(text, 5f);
    }
    
    private static void LogSendFailure(string messageName, Exception ex)
    {
        if (!LoggedSendFailures.Add(messageName))
            return;
        
        Plugin.Logger.LogWarning($"Custom Mirage send failed for {messageName}. " +
                                 $"This send was skipped, but custom messaging remains enabled.\n{ex}");
    }
    
    // v1 legacy server support
    private static void OnFuelTransfer(FuelTransferMessage message)
    {
        if (_serverProtocolVersion != LegacyProtocolVersion || !GameManager.GetLocalAircraft(out var aircraft) ||
            message.Litres <= 0f)
            return;
        
        var applied =
            AirResupplyManager.ApplyFuelToAircraft(aircraft, message.Litres, Mathf.Clamp01(message.TargetRatio));
        if (message.FullRefill)
            AirResupplyManager.SetNeedsFuel(aircraft, false);
        
        ReportLocalFuelTransfer(message.ProviderName, applied);
    }
    
    // Used on v2+ servers
    private static void OnFuelTransferV2(FuelTransferMessageV2 message)
    {
        if (!_clientProtocolCompatible || !GameManager.GetLocalAircraft(out var aircraft) || message.Litres <= 0f)
            return;
        
        var applied =
            AirResupplyManager.ApplyFuelToAircraft(aircraft, message.Litres, Mathf.Clamp01(message.TargetRatio));
        if (message.FullRefill)
            AirResupplyManager.SetNeedsFuel(aircraft, false);
        
        ReportLocalFuelTransfer(ResolveProviderName(message.ProviderId), applied);
    }
    
    private static string ResolveProviderName(PersistentID providerId)
    {
        if (providerId.IsValid && UnitRegistry.TryGetPersistentUnit(providerId, out var provider) && provider != null &&
            !string.IsNullOrWhiteSpace(provider.unitName))
            return provider.unitName;
        
        return "airborne supplier";
    }
    
    private static void OnProviderFuelDrain(ProviderFuelDrainMessage message)
    {
        if (!ClientSupportsCommonMessages || !GameManager.GetLocalAircraft(out var aircraft) || message.Litres <= 0f)
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