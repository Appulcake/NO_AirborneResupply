using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NO_AR;

internal static class ProviderSelectionUi
{
    private const float ExtraPanelHeight = 30f;
    private static readonly Dictionary<string, bool> Preferences = new(StringComparer.Ordinal);
    private static readonly Dictionary<AircraftSelectionMenu, View> Views = new();
    private static HashSet<string> _serverProviderKeys = new(StringComparer.Ordinal);
    private static bool _serverDefaultEnabled;
    private static bool _hasServerPolicy;
    
    internal static void OnServerPolicyReceived(ProviderPolicyMessage message)
    {
        _serverProviderKeys = ProviderRoleManager.ParseProviderKeys(message.ProviderAircraftJsonKeys ?? string.Empty);
        _serverDefaultEnabled = message.EnabledByDefault;
        _hasServerPolicy = true;
        foreach (var pair in Views)
            UpdateForSelection(pair.Key);
    }
    
    internal static void ResetSession()
    {
        Preferences.Clear();
        _serverProviderKeys.Clear();
        _serverDefaultEnabled = false;
        _hasServerPolicy = false;
        foreach (var pair in Views)
            if (pair.Value.Row != null)
                pair.Value.Row.SetActive(false);
    }
    
    internal static void OnMenuInitialised(AircraftSelectionMenu menu, Transform infoPanel, Button flyButton)
    {
        if (menu == null || infoPanel == null || flyButton == null)
            return;
        
        RemoveView(menu);
        var flyText = flyButton.GetComponentInChildren<TextMeshProUGUI>(true);
        if (flyText == null)
            return;
        
        // Inspiration from BOTE on adding UI element near this panel
        
        if (!infoPanel.TryGetComponent<VerticalLayoutGroup>(out var vertical))
        {
            vertical = infoPanel.gameObject.AddComponent<VerticalLayoutGroup>();
            vertical.childControlWidth = true;
            vertical.childControlHeight = true;
            vertical.childForceExpandWidth = true;
            vertical.childForceExpandHeight = false;
            vertical.padding = new RectOffset(5, 5, 5, 5);
            vertical.spacing = 4f;
        }
        
        RectTransform? expandedParent = null;
        
        if (infoPanel.parent is RectTransform parentRect)
        {
            parentRect.sizeDelta += new Vector2(0f, ExtraPanelHeight);
            expandedParent = parentRect;
        }
        
        var row = new GameObject("NO_AR_AirResupplyPreference", typeof(RectTransform), typeof(LayoutElement),
            typeof(HorizontalLayoutGroup), typeof(Toggle));
        row.transform.SetParent(infoPanel, false);
        row.transform.SetSiblingIndex(Mathf.Max(0, flyButton.transform.GetSiblingIndex()));
        var rowLayout = row.GetComponent<LayoutElement>();
        rowLayout.minHeight = 26f;
        rowLayout.preferredHeight = 26f;
        rowLayout.flexibleWidth = 1f;
        var horizontal = row.GetComponent<HorizontalLayoutGroup>();
        horizontal.childAlignment = TextAnchor.MiddleCenter;
        horizontal.spacing = 8f;
        horizontal.childControlWidth = true;
        horizontal.childControlHeight = true;
        horizontal.childForceExpandWidth = false;
        horizontal.childForceExpandHeight = false;
        horizontal.padding = new RectOffset(4, 4, 1, 1);
        var box = new GameObject("Checkbox", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
            typeof(LayoutElement));
        box.transform.SetParent(row.transform, false);
        var boxLayout = box.GetComponent<LayoutElement>();
        boxLayout.minWidth = 22f;
        boxLayout.preferredWidth = 22f;
        boxLayout.flexibleWidth = 0f;
        boxLayout.minHeight = 22f;
        boxLayout.preferredHeight = 22f;
        var boxImage = box.GetComponent<Image>();
        if (flyButton.targetGraphic is Image flyImage)
        {
            boxImage.sprite = flyImage.sprite;
            boxImage.type = flyImage.type;
            boxImage.color = flyImage.color;
            boxImage.material = flyImage.material;
        }
        
        var check = Object.Instantiate(flyText, box.transform);
        check.name = "Checkmark";
        check.text = "X";
        check.alignment = TextAlignmentOptions.Center;
        check.enableWordWrapping = false;
        check.raycastTarget = false;
        check.fontSize = Mathf.Max(10f, flyText.fontSize * 0.75f);
        var checkRect = check.rectTransform;
        checkRect.anchorMin = Vector2.zero;
        checkRect.anchorMax = Vector2.one;
        checkRect.offsetMin = Vector2.zero;
        checkRect.offsetMax = Vector2.zero;
        checkRect.localScale = Vector3.one;
        var label = Object.Instantiate(flyText, row.transform);
        label.name = "Label";
        label.text = "Air Resupply";
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.enableWordWrapping = false;
        label.raycastTarget = false;
        var labelLayout = label.gameObject.GetComponent<LayoutElement>() ??
                          label.gameObject.AddComponent<LayoutElement>();
        labelLayout.flexibleWidth = 1f;
        labelLayout.minWidth = 110f;
        var toggle = row.GetComponent<Toggle>();
        toggle.targetGraphic = boxImage;
        toggle.graphic = check;
        toggle.transition = flyButton.transition;
        toggle.colors = flyButton.colors;
        toggle.navigation = new Navigation { mode = Navigation.Mode.None };
        var view = new View(row, toggle, expandedParent);
        Views[menu] = view;
        toggle.onValueChanged.AddListener(value =>
        {
            if (view.SuppressCallback || string.IsNullOrEmpty(view.JsonKey))
                return;
            
            Preferences[view.JsonKey] = value;
        });
        
        UpdateForSelection(menu);
    }
    
    internal static void UpdateForSelection(AircraftSelectionMenu menu)
    {
        if (menu == null || !Views.TryGetValue(menu, out var view))
            return;
        
        var definition = menu.GetSelectedType();
        var key = definition?.jsonKey ?? string.Empty;
        var visible = _hasServerPolicy && key.Length > 0 && _serverProviderKeys.Contains(key);
        view.Row.SetActive(visible);
        if (!visible)
        {
            view.JsonKey = string.Empty;
            return;
        }
        
        view.JsonKey = key;
        if (!Preferences.TryGetValue(key, out var enabled))
        {
            enabled = _serverDefaultEnabled;
            Preferences[key] = enabled;
        }
        
        view.SuppressCallback = true;
        view.Toggle.SetIsOnWithoutNotify(enabled);
        view.SuppressCallback = false;
    }
    
    internal static void SendCurrentPreference(AircraftSelectionMenu menu)
    {
        if (menu == null || !_hasServerPolicy)
            return;
        
        var definition = menu.GetSelectedType();
        var key = definition?.jsonKey ?? string.Empty;
        if (key.Length == 0 || !_serverProviderKeys.Contains(key))
            return;
        
        if (!Preferences.TryGetValue(key, out var enabled))
        {
            enabled = _serverDefaultEnabled;
            Preferences[key] = enabled;
        }
        
        if (!AirResupplyNetworking.TrySendProviderPreference(key, enabled))
            Plugin.Logger.LogWarning("Could not send provider preference, server default will apply.");
    }
    
    internal static void OnMenuDestroyed(AircraftSelectionMenu menu)
    {
        RemoveView(menu);
    }
    
    private static void RemoveView(AircraftSelectionMenu menu)
    {
        if (menu == null || !Views.TryGetValue(menu, out var existing))
            return;
        
        Views.Remove(menu);
        if (existing.ExpandedParent != null) existing.ExpandedParent.sizeDelta -= new Vector2(0f, ExtraPanelHeight);
        
        if (existing.Row != null)
            Object.Destroy(existing.Row);
    }
    
    private sealed class View
    {
        internal readonly RectTransform? ExpandedParent;
        internal readonly GameObject Row;
        internal readonly Toggle Toggle;
        internal string JsonKey = string.Empty;
        internal bool SuppressCallback;
        
        internal View(GameObject row, Toggle toggle, RectTransform? expandedParent)
        {
            Row = row;
            Toggle = toggle;
            ExpandedParent = expandedParent;
        }
    }
}

[HarmonyPatch(typeof(AircraftSelectionMenu))]
internal static class AirResupplySelectionInitializePatch
{
    [HarmonyPatch(typeof(AircraftSelectionMenu), nameof(AircraftSelectionMenu.Initialize))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void Initialize_Postfix(AircraftSelectionMenu __instance)
    {
        AirResupplyNetworking.TryShowProtocolWarning();
        ProviderSelectionUi.OnMenuInitialised(__instance, __instance.infoPanel, __instance.flyButton);
    }
    
    [HarmonyPatch(nameof(AircraftSelectionMenu.SpawnPreview))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void SpawnPreview_Postfix(AircraftSelectionMenu __instance)
    {
        ProviderSelectionUi.UpdateForSelection(__instance);
    }
    
    [HarmonyPatch(nameof(AircraftSelectionMenu.FlyAircraft))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static void FlyAircraft_Prefix(AircraftSelectionMenu __instance)
    {
        ProviderSelectionUi.SendCurrentPreference(__instance);
    }
    
    [HarmonyPatch(nameof(AircraftSelectionMenu.OnDestroy))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void OnDestroy_Postfix(AircraftSelectionMenu __instance)
    {
        ProviderSelectionUi.OnMenuDestroyed(__instance);
    }
}