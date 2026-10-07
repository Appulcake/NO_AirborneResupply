using System;
using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NO_AR;

internal static class AirResupplyMapStatus
{
    private const float StatusExpirySeconds = 30f;
    private const float LabelFontSize = 14f;
    private const float LabelYOffset = -16f;
    private static readonly Dictionary<PersistentID, MapSupplyState> States = new();
    private static readonly Dictionary<UnitMapIcon, TextMeshProUGUI> Labels = new();
    private static TMP_FontAsset? _font;
    
    internal static void OnStateReceived(AirResupplyMapStatusMessage message)
    {
        if (!message.ProviderId.IsValid)
            return;
        
        if ((message.Flags & AirResupplyMapStatusMessage.SupplyMask) == 0)
        {
            States.Remove(message.ProviderId);
            return;
        }
        
        States[message.ProviderId] =
            new MapSupplyState(message.Flags, message.AmmoPercent, message.FuelPercent, Time.unscaledTime);
    }
    
    internal static void UpdateIcon(UnitMapIcon icon, float mapInverseScale, bool mapMaximized)
    {
        if (icon == null || icon.unit is not Aircraft aircraft ||
            !States.TryGetValue(aircraft.persistentID, out var state))
        {
            HideLabel(icon);
            return;
        }
        
        if (Time.unscaledTime - state.ReceivedAt > StatusExpirySeconds)
        {
            States.Remove(aircraft.persistentID);
            HideLabel(icon);
            return;
        }
        
        var friendly = aircraft.NetworkHQ != null &&
                       DynamicMap.GetFactionMode(aircraft.NetworkHQ, true) == FactionMode.Friendly;
        var visible = mapMaximized && friendly && icon.gameObject.activeInHierarchy && icon.iconImage != null &&
                      icon.iconImage.enabled;
        if (!visible)
        {
            HideLabel(icon);
            return;
        }
        
        var label = GetOrCreateLabel(icon);
        if (label == null)
            return;
        
        if (!string.Equals(label.text, state.Text, StringComparison.Ordinal))
            label.text = state.Text;
        
        if (icon.iconImage != null)
            label.transform.localPosition = icon.iconImage.transform.localPosition + new Vector3(0f, LabelYOffset, 0f);
        
        label.transform.localScale = Vector3.one * mapInverseScale;
        var theme = ThemeManager.Active;
        label.color = theme != null && theme.ColorTheme != null ? theme.ColorTheme.MapIconFriendly : Color.cyan;
        label.enabled = true;
    }
    
    internal static void RemoveIcon(UnitMapIcon icon)
    {
        if (icon == null || !Labels.TryGetValue(icon, out var label))
            return;
        
        if (label != null)
            Object.Destroy(label.gameObject);
        
        Labels.Remove(icon);
    }
    
    internal static void Clear()
    {
        States.Clear();
        foreach (var pair in Labels)
            if (pair.Value != null)
                Object.Destroy(pair.Value.gameObject);
        
        Labels.Clear();
        _font = null;
    }
    
    private static TextMeshProUGUI? GetOrCreateLabel(UnitMapIcon icon)
    {
        if (Labels.TryGetValue(icon, out var existing))
        {
            if (existing != null)
                return existing;
            
            Labels.Remove(icon);
        }
        
        if (icon.iconImage == null || icon.iconImage.transform.parent == null)
            return null;
        
        var go = new GameObject("NO_AR_MapSupply", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(icon.iconImage.transform.parent, false);
        var label = go.GetComponent<TextMeshProUGUI>();
        if (label == null)
        {
            Object.Destroy(go);
            return null;
        }
        
        label.font = ResolveFont();
        label.fontSize = LabelFontSize;
        label.enableAutoSizing = false;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        label.richText = false;
        var rect = label.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(220f, 32f);
        rect.localRotation = Quaternion.identity;
        label.enabled = false;
        Labels.Add(icon, label);
        return label;
    }
    
    private static TMP_FontAsset? ResolveFont()
    {
        if (_font != null)
            return _font;
        
        var combatHud = SceneSingleton<CombatHUD>.i;
        var reference = combatHud != null
            ? combatHud.GetComponentInChildren<TextMeshProUGUI>(true)
            : null;
        
        _font = reference != null ? reference.font : TMP_Settings.defaultFontAsset;
        return _font;
    }
    
    private static void HideLabel(UnitMapIcon? icon)
    {
        if (icon != null && Labels.TryGetValue(icon, out var label) && label != null && label.enabled) label.enabled = false;
    }
    
    private sealed class MapSupplyState
    {
        internal readonly float ReceivedAt;
        internal readonly string Text;
        
        internal MapSupplyState(byte flags, byte ammoPercent, byte fuelPercent, float receivedAt)
        {
            ReceivedAt = receivedAt;
            
            var ammo = (flags & AirResupplyMapStatusMessage.AmmoFlag) != 0;
            var fuel = (flags & AirResupplyMapStatusMessage.FuelFlag) != 0;
            
            if (ammo && fuel)
                Text = $"F: {fuelPercent}% | A: {ammoPercent}%";
            else if (ammo)
                Text = $"A: {ammoPercent}%";
            else if (fuel)
                Text = $"F: {fuelPercent}%";
            else
                Text = string.Empty;
        }
    }
}

[HarmonyPatch(typeof(UnitMapIcon))]
internal static class AirResupplyMapStatusUpdatePatch
{
    [HarmonyPatch(nameof(UnitMapIcon.UpdateIcon))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void UpdateIcon_Postfix(UnitMapIcon __instance, float mapInverseScale, bool mapMaximized)
    {
        AirResupplyMapStatus.UpdateIcon(__instance, mapInverseScale, mapMaximized);
    }
    
    [HarmonyPatch(nameof(UnitMapIcon.OnRemoveIcon))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static void OnRemoveIcon_Prefix(UnitMapIcon __instance)
    {
        AirResupplyMapStatus.RemoveIcon(__instance);
    }
}