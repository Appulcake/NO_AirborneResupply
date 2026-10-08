using System;
using System.Text;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NO_AR;

internal static class AirResupplyHud
{
    private const string HudObjectName = "NO_AR_AirResupplyHud";
    private const float DynamicRefreshInterval = 0.1f;
    private const int LowSupplyWarningPercent = 20;
    private static AirResupplyHudStateMessage _state;
    private static float _receivedAt = -1000f;
    private static float _nextDynamicRefresh;
    private static FlightHud? _flightHud;
    private static Transform? _flightHudCenter;
    private static Aircraft? _localAircraft;
    private static bool _localAircraftExcluded;
    private static TextMeshProUGUI? _styleReference;
    private static TextMeshProUGUI? _text;
    private static bool _initialised;
    private static bool _hudAllowed;
    private const float HudStateExpirySeconds = 30f;
    
    internal static void Initialise()
    {
        if (_initialised)
            return;
        
        _initialised = true;
        _hudAllowed = Plugin.EnableHud.Value;
        Plugin.EnableHud.SettingChanged += HudVisibilitySettingChanged;
        Plugin.HudXOffset.SettingChanged += HudPositionSettingChanged;
        Plugin.HudYOffset.SettingChanged += HudPositionSettingChanged;
    }
    
    internal static void OnStateReceived(AirResupplyHudStateMessage state)
    {
        _state = state;
        _receivedAt = Time.unscaledTime;
        _nextDynamicRefresh = 0f;
        if (!CanDisplayHudState())
        {
            SetHudVisible(false);
            return;
        }
        
        TryCreateHudText();
        RefreshText(Time.unscaledTime);
    }
    
    internal static void Clear()
    {
        _state = AirResupplyHudStateMessage.Hidden;
        _receivedAt = -1000f;
        _nextDynamicRefresh = 0f;
        SetHudVisible(false);
    }
    
    // FlightHud.Update()
    internal static void UpdateFrame()
    {
        if (_text == null || !CanDisplayHudState())
        {
            SetHudVisible(false);
            return;
        }
        
        var now = Time.unscaledTime;
        if (now - _receivedAt > HudStateExpirySeconds)
        {
            SetHudVisible(false);
            return;
        }
        
        SetHudVisible(true);
        if (!HudNeedsDynamicRefresh(now) || now < _nextDynamicRefresh)
            return;
        
        _nextDynamicRefresh = now + DynamicRefreshInterval;
        RefreshText(now);
    }
    
    // FlightHud.Awake()
    internal static void OnFlightHudAwake(FlightHud flightHud)
    {
        if (flightHud == null || _flightHud == flightHud)
            return;
        
        DestroyHud();
        _flightHud = flightHud;
        _flightHudCenter = flightHud.GetHUDCenter();
        _localAircraft = null;
        _localAircraftExcluded = false;
        Clear();
        TryCreateHudText();
    }
    
    // FlightHud.SetAircraft()
    internal static void OnFlightHudSetAircraft(FlightHud hud, Aircraft aircraft)
    {
        if (hud == null)
            return;
        
        if (_flightHud != hud)
            OnFlightHudAwake(hud);
        
        if (_localAircraft == aircraft)
            return;
        
        _localAircraft = aircraft;
        _localAircraftExcluded = aircraft == null || BoteCompatibility.IsBoteShip(aircraft);
        Clear();
        if (_localAircraftExcluded)
        {
            SetHudVisible(false);
            return;
        }
        
        TryCreateHudText();
    }
    
    // FlightHud.OnDestroy()
    internal static void OnFlightHudDestroyed(FlightHud hud)
    {
        if (_flightHud != hud)
            return;
        
        DestroyHud();
        _flightHud = null;
        _flightHudCenter = null;
        _localAircraft = null;
        _localAircraftExcluded = false;
        _styleReference = null;
        _state = AirResupplyHudStateMessage.Hidden;
        _receivedAt = -1000f;
    }
    
    // FuelGauge.RefreshSettings()
    // Inspired by Autopilot, I take FuelGauge's text as base reference for its own modified font size and style
    // Those elements take HUD font size and downsize it, using HUD font alone would result in overly huge text
    internal static void OnFuelGaugeRefreshed(Aircraft aircraft, TextMeshProUGUI referenceFuelLabel)
    {
        if (referenceFuelLabel == null || (_localAircraft != null && aircraft != null && _localAircraft != aircraft))
            return;
        
        if (_localAircraft == null && aircraft != null)
        {
            _localAircraft = aircraft;
            _localAircraftExcluded = BoteCompatibility.IsBoteShip(aircraft);
        }
        
        _styleReference = referenceFuelLabel;
        if (_localAircraftExcluded)
        {
            SetHudVisible(false);
            return;
        }
        
        TryCreateHudText();
        ApplyReferenceStyle();
        ApplyPosition();
        if (CanDisplayHudState())
            RefreshText(Time.unscaledTime);
    }
    
    // FuelGauge.OnDestroy()
    internal static void OnFuelGaugeDestroyed(TextMeshProUGUI referenceFuelLabel)
    {
        if (_styleReference == referenceFuelLabel)
            _styleReference = null;
    }
    
    // ThemeManager.NotifyThemeGroupChanged()
    internal static void OnThemeChanged()
    {
        ApplyReferenceStyle();
        ApplyHudColorState(Time.unscaledTime);
    }
    
    internal static void Shutdown()
    {
        if (_initialised)
        {
            Plugin.EnableHud.SettingChanged -= HudVisibilitySettingChanged;
            Plugin.HudXOffset.SettingChanged -= HudPositionSettingChanged;
            Plugin.HudYOffset.SettingChanged -= HudPositionSettingChanged;
            _initialised = false;
        }
        
        DestroyHud();
        _flightHud = null;
        _flightHudCenter = null;
        _localAircraft = null;
        _localAircraftExcluded = false;
        _styleReference = null;
        _state = AirResupplyHudStateMessage.Hidden;
        _receivedAt = -1000f;
    }
    
    private static void TryCreateHudText()
    {
        if (_text != null || !_hudAllowed || _localAircraftExcluded || _localAircraft == null ||
            _flightHudCenter == null || _styleReference == null)
            return;
        
        // Inspiration by Autopilot, clone FuelGauge's label so it has same style like font size
        var hudObject = Object.Instantiate(_styleReference.gameObject, _flightHudCenter);
        hudObject.name = HudObjectName;
        _text = hudObject.GetComponent<TextMeshProUGUI>();
        if (_text == null)
        {
            Object.Destroy(hudObject);
            return;
        }
        
        _text.raycastTarget = false;
        _text.enableWordWrapping = false;
        _text.richText = true;
        _text.alignment = TextAlignmentOptions.TopLeft;
        _text.overflowMode = TextOverflowModes.Overflow;
        var rect = _text.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0f, 1f);
        rect.localRotation = Quaternion.identity;
        ApplyReferenceStyle();
        ApplyPosition();
        SetHudVisible(false);
    }
    
    private static void ApplyReferenceStyle()
    {
        if (_text == null || _styleReference == null)
            return;
        
        _text.font = _styleReference.font;
        _text.fontSharedMaterial = _styleReference.fontSharedMaterial;
        _text.fontSize = _styleReference.fontSize;
        _text.fontStyle = _styleReference.fontStyle;
        _text.fontWeight = _styleReference.fontWeight;
        _text.enableAutoSizing = _styleReference.enableAutoSizing;
        _text.fontSizeMin = _styleReference.fontSizeMin;
        _text.fontSizeMax = _styleReference.fontSizeMax;
        _text.characterSpacing = _styleReference.characterSpacing;
        _text.wordSpacing = _styleReference.wordSpacing;
        _text.lineSpacing = _styleReference.lineSpacing;
        _text.paragraphSpacing = _styleReference.paragraphSpacing;
        _text.rectTransform.localScale = _styleReference.rectTransform.localScale;
    }
    
    private static void ApplyPosition()
    {
        if (_text == null)
            return;
        
        _text.rectTransform.anchoredPosition = new Vector2(Plugin.HudXOffset.Value, Plugin.HudYOffset.Value);
    }
    
    private static void RefreshText(float now)
    {
        if (_text == null)
            return;
        
        var value = BuildHudText(now);
        if (string.IsNullOrEmpty(value))
        {
            SetHudVisible(false);
            return;
        }
        
        if (!string.Equals(_text.text, value, StringComparison.Ordinal))
            _text.text = value;
        
        ApplyHudColorState(now);
        SetHudVisible(true);
    }
    
    private static void ApplyHudColorState(float now)
    {
        if (_text == null)
            return;
        
        var group = ThemeManager.Active;
        if (group == null || group.ColorTheme == null)
        {
            _text.color = _styleReference != null ? _styleReference.color : Color.green;
            return;
        }
        
        var theme = group.ColorTheme;
        _text.color = GetHudColorState(now) switch
        {
            HudColorState.Warning => theme.Warning,
            HudColorState.Alert => theme.Alert,
            _ => theme.AllClear
        };
    }
    
    private static HudColorState GetHudColorState(float now)
    {
        var ammoConfigured = _state.AmmoMaxKg > 0.01f;
        var ammoAvailable = _state.AmmoRemainingKg > 0.01f;
        var fuelConfigured = _state.FuelTotalMaxL > 0.01f;
        var fuelAvailable = _state.FuelTotalRemainingL > 0.01f;
        var ammoLow = ammoConfigured &&
                      Percent(_state.AmmoRemainingKg, _state.AmmoMaxKg) <= LowSupplyWarningPercent;
        var fuelLow = fuelConfigured &&
                      Percent(_state.FuelTotalRemainingL, _state.FuelTotalMaxL) <= LowSupplyWarningPercent;
        if (_state.Mode == AirResupplyHudStateMessage.ProviderMode)
        {
            if (!ammoAvailable && !fuelAvailable)
                return HudColorState.Alert;
            
            if (ammoLow || fuelLow)
                return HudColorState.Warning;
            
            return HudColorState.AllClear;
        }
        
        if (_state.Mode != AirResupplyHudStateMessage.ReceiverMode || _state.Servicing || _state.Latched)
            return HudColorState.AllClear;
        
        if (!ammoAvailable && !fuelAvailable)
            return HudColorState.Alert;
        
        if (_state.Distance > Mathf.Max(1f, _state.ServiceRange) || GetRemainingRearmCooldown(now) > 0.5f || ammoLow ||
            fuelLow)
            return HudColorState.Warning;
        
        return HudColorState.AllClear;
    }
    
    private static bool CanDisplayHudState() =>
        _hudAllowed && !_localAircraftExcluded && _localAircraft != null && _state.Visible;
    
    private static bool HudNeedsDynamicRefresh(float now) => _state.Servicing || GetRemainingRearmCooldown(now) > 0.5f;
    
    private static float GetRemainingRearmCooldown(float now) =>
        Mathf.Max(0f, _state.RearmCooldownRemaining - Mathf.Max(0f, now - _receivedAt));
    
    private static void SetHudVisible(bool visible)
    {
        if (_text != null && _text.gameObject.activeSelf != visible)
            _text.gameObject.SetActive(visible);
    }
    
    private static void DestroyHud()
    {
        if (_text != null)
            Object.Destroy(_text.gameObject);
        
        _text = null;
    }
    
    private static string BuildHudText(float now)
    {
        var ammoVisible = _state is { AmmoRemainingKg: > 0.01f, AmmoMaxKg: > 0.01f };
        var ammoPercent = ammoVisible ? Percent(_state.AmmoRemainingKg, _state.AmmoMaxKg) : 0;
        var fuelVisible = _state is { FuelTotalRemainingL: > 0.01f, FuelTotalMaxL: > 0.01f };
        var fuelPercent = fuelVisible ? Percent(_state.FuelTotalRemainingL, _state.FuelTotalMaxL) : 0;
        if (_state.Mode == AirResupplyHudStateMessage.ProviderMode)
        {
            var sb = new StringBuilder(128);
            sb.Append("Supplier");
            if (fuelVisible)
                sb.Append($" | Fuel {fuelPercent}%");
            
            switch (ammoVisible)
            {
                case true:
                    sb.Append($" | Ammo {ammoPercent}%");
                    break;
                case false when !fuelVisible:
                    sb.Append(" | EMPTY");
                    break;
            }
            
            if (!fuelVisible)
                return sb.ToString();
            
            var breakdown = BuildFuelBreakdown();
            if (string.IsNullOrEmpty(breakdown))
                return sb.ToString();
            
            sb.Append('\n');
            sb.Append(breakdown);
            return sb.ToString();
        }
        
        if (_state.Mode != AirResupplyHudStateMessage.ReceiverMode)
            return string.Empty;
        
        var receiver = new StringBuilder(128);
        receiver.Append(_state.ProviderName.Substring(0, Math.Min(8, _state.ProviderName.Length)));
        receiver.Append(" | ");
        receiver.Append(FormatDistance(_state.Distance));
        receiver.Append(" | R: ");
        receiver.Append(FormatDistance(_state.ServiceRange));
        var supply = new StringBuilder(48);
        if (fuelVisible)
            supply.Append($"Fuel {fuelPercent}%");
        
        if (ammoVisible)
        {
            if (supply.Length > 0)
                supply.Append(" | ");
            
            supply.Append($"Ammo {ammoPercent}%");
        }
        
        var rearmCooldown = GetRemainingRearmCooldown(now);
        if (rearmCooldown > 0.5f)
        {
            if (supply.Length > 0)
                supply.Append(" | ");
            
            var seconds = Mathf.CeilToInt(rearmCooldown);
            supply.Append($"Rearm {seconds / 60}:{seconds % 60:00}");
        }
        
        if (supply.Length > 0)
        {
            receiver.Append('\n');
            receiver.Append(supply);
        }
        
        if (_state.Servicing)
        {
            var serviceTime = Mathf.Max(0.1f, _state.ServiceTime);
            var interpolated = Mathf.Clamp(_state.Progress + Mathf.Max(0f, now - _receivedAt), 0f, serviceTime);
            var progressPercent = Mathf.RoundToInt(interpolated / serviceTime * 100f);
            receiver.Append($"\nResupplying {progressPercent}%");
        }
        else if (_state.Latched)
        {
            receiver.Append("\nResupply complete!");
        }
        
        return receiver.ToString();
    }
    
    private static string BuildFuelBreakdown()
    {
        var sb = new StringBuilder(64);
        AppendFuelPart(sb, "Cargo", _state.FuelCargoRemainingL);
        AppendFuelPart(sb, "Ext", _state.FuelExternalRemainingL);
        AppendFuelPart(sb, "Main", _state.FuelInternalRemainingL);
        if (sb.Length > 0)
            sb.Append(" kL");
        
        return sb.ToString();
    }
    
    private static void AppendFuelPart(StringBuilder sb, string label, float litres)
    {
        if (litres <= 0.01f)
            return;
        
        if (sb.Length > 0)
            sb.Append(" | ");
        
        sb.Append(label);
        sb.Append(' ');
        sb.Append((litres / 1000f).ToString("F1"));
    }
    
    private static int Percent(float current, float maximum) =>
        maximum <= 0.001f ? 0 : Mathf.RoundToInt(Mathf.Clamp01(current / maximum) * 100f);
    
    private static string FormatDistance(float metres) =>
        metres >= 1000f ? $"{metres / 1000f:F2} km" : $"{metres:F0} m";
    
    private static void HudVisibilitySettingChanged(object? sender, EventArgs e)
    {
        _hudAllowed = Plugin.EnableHud.Value;
        if (!_hudAllowed)
        {
            SetHudVisible(false);
            return;
        }
        
        TryCreateHudText();
        if (CanDisplayHudState())
            RefreshText(Time.unscaledTime);
    }
    
    private static void HudPositionSettingChanged(object? sender, EventArgs e) => ApplyPosition();
    
    private enum HudColorState
    {
        AllClear,
        Warning,
        Alert
    }
}