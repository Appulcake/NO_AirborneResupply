using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NO_AR;

internal static class ProviderSelectionUi
{
    private const float ProviderSectionTopGap = 16f;
    private const float SummaryPanelGap = 12f;
    private const float SummaryBaseHeight = 36f;
    private const float SummaryRowHeight = 22f;
    private static readonly Dictionary<string, ProviderPreference> Preferences = new(StringComparer.Ordinal);
    private static readonly Dictionary<AircraftSelectionMenu, View> Views = new();
    private static HashSet<string> _serverProviderKeys = new(StringComparer.Ordinal);
    private static bool _serverDefaultEnabled;
    private static bool _hasServerPolicy;
    private static byte _serverDefaultReservePercent = 15;
    private static byte _serverMinimumReservePercent = 5;
    private static float _serverFuelTransferMultiplier = 1f;
    private static float _serverAmmoTransferMultiplier = 1f;
    private static bool _serverFuelEnabled = true;
    private static bool _serverAmmoEnabled = true;
    private static bool _hasServerSettings;
    
    internal static void OnServerPolicyReceived(ProviderPolicyMessage message)
    {
        _serverProviderKeys = ProviderRoleManager.ParseProviderKeys(message.ProviderAircraftJsonKeys ?? string.Empty);
        _serverDefaultEnabled = message.EnabledByDefault;
        _hasServerPolicy = true;
        foreach (var pair in Views)
            UpdateForSelection(pair.Key);
    }
    
    internal static void OnServerSettingsReceived(ProviderSettingsMessageV2 message)
    {
        _serverMinimumReservePercent = (byte)Mathf.Clamp(message.MinimumInternalFuelReservePercent, 0, 100);
        _serverDefaultReservePercent =
            (byte)Mathf.Clamp(message.DefaultInternalFuelReservePercent, _serverMinimumReservePercent, 100);
        _serverFuelTransferMultiplier = Mathf.Max(0.01f, message.FuelTransferMultiplier);
        _serverAmmoTransferMultiplier = Mathf.Max(0.01f, message.AmmoTransferMultiplier);
        _serverFuelEnabled = message.FuelEnabled;
        _serverAmmoEnabled = message.AmmoEnabled;
        _hasServerSettings = true;
        foreach (var preference in Preferences.Values)
            if (!preference.ReserveExplicitlySet)
                preference.ReservePercent = _serverDefaultReservePercent;
        foreach (var pair in Views)
            UpdateForSelection(pair.Key);
    }
    
    internal static byte GetServerDefaultInternalFuelReservePercent() =>
        _serverDefaultReservePercent;
    
    internal static void ResetSession()
    {
        Preferences.Clear();
        _serverProviderKeys.Clear();
        _serverDefaultEnabled = false;
        _hasServerPolicy = false;
        _serverDefaultReservePercent = 15;
        _serverMinimumReservePercent = 5;
        _serverFuelTransferMultiplier = 1f;
        _serverAmmoTransferMultiplier = 1f;
        _serverFuelEnabled = true;
        _serverAmmoEnabled = true;
        _hasServerSettings = false;
        foreach (var pair in Views)
        {
            var view = pair.Value;
            SetExpandedHeight(view, 0f);
            if (view.ToggleRow != null)
                view.ToggleRow.SetActive(false);
            
            if (view.ReserveRow != null)
                view.ReserveRow.SetActive(false);
            
            if (view.TopSpacer != null)
                view.TopSpacer.SetActive(false);
            
            if (view.SummaryPanel != null)
                view.SummaryPanel.SetActive(false);
            
            view.JsonKey = string.Empty;
        }
    }
    
    internal static void OnMenuInitialised(AircraftSelectionMenu menu, Transform infoPanel, Button flyButton,
        Slider fuelLevel)
    {
        // Inspiration from BOTE on adding UI element near this panel
        
        if (menu == null || infoPanel == null || flyButton == null || fuelLevel == null)
            return;
        
        RemoveView(menu);
        var flyText = flyButton.GetComponentInChildren<TextMeshProUGUI>(true);
        if (flyText == null)
            return;
        
        var flyImage = flyButton.targetGraphic as Image;
        
        var container = infoPanel.Find("Container");
        if (container == null)
            return;
        
        var containerRect = container as RectTransform;
        var containerLayout = container.GetComponent<VerticalLayoutGroup>();
        var containerSpacing = containerLayout != null ? containerLayout.spacing : 10f;
        var expandedParent = infoPanel.parent as RectTransform;
        
        var fuelSelector = fuelLevel.transform.parent;
        if (fuelSelector == null)
            return;
        
        var topSpacer = new GameObject("NO_AR_ProviderTopGap", typeof(RectTransform));
        topSpacer.transform.SetParent(container, false);
        var topSpacerRect = topSpacer.GetComponent<RectTransform>();
        topSpacerRect.sizeDelta = new Vector2(0f, ProviderSectionTopGap);
        topSpacer.SetActive(false);
        
        var reserveRow = Object.Instantiate(fuelSelector.gameObject, container);
        reserveRow.name = "NO_AR_MainFuelReserve";
        var reserveCanvasGroup = reserveRow.GetComponent<CanvasGroup>() ?? reserveRow.AddComponent<CanvasGroup>();
        
        var reserveLabel = reserveRow.transform.Find("FuelLabel")?.GetComponent<TextMeshProUGUI>();
        var originalPercentage = reserveRow.transform.Find("FuelPercentage")?.GetComponent<TextMeshProUGUI>();
        var reserveValue = reserveRow.transform.Find("FuelWeight")?.GetComponent<TextMeshProUGUI>();
        var reserveSlider = reserveRow.transform.Find("FuelSlider")?.GetComponent<Slider>();
        if (reserveLabel == null || reserveValue == null || reserveSlider == null)
        {
            Object.Destroy(reserveRow);
            return;
        }
        
        if (originalPercentage != null)
            originalPercentage.gameObject.SetActive(false);
        
        reserveLabel.text = "Main Fuel Reserve :";
        reserveLabel.enableWordWrapping = false;
        reserveLabel.overflowMode = TextOverflowModes.Overflow;
        reserveLabel.raycastTarget = false;
        var reserveLabelRect = reserveLabel.rectTransform;
        reserveLabelRect.sizeDelta = new Vector2(230f, reserveLabelRect.sizeDelta.y);
        reserveValue.text = $"{_serverDefaultReservePercent}%";
        reserveValue.alignment = TextAlignmentOptions.MidlineRight;
        reserveValue.enableWordWrapping = false;
        reserveValue.raycastTarget = false;
        reserveSlider.onValueChanged = new Slider.SliderEvent();
        reserveSlider.minValue = _serverMinimumReservePercent;
        reserveSlider.maxValue = 100f;
        reserveSlider.wholeNumbers = true;
        reserveSlider.navigation = new Navigation
        {
            mode = Navigation.Mode.None
        };
        
        GameObject? summaryPanel = null;
        RectTransform? summaryRect = null;
        TextMeshProUGUI? summaryLabels = null;
        TextMeshProUGUI? summaryMultipliers = null;
        TextMeshProUGUI? summaryValues = null;
        if (expandedParent != null)
        {
            summaryPanel = new GameObject("NO_AR_AirResupplyCapacity", typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Image), typeof(LayoutElement));
            summaryPanel.transform.SetParent(expandedParent, false);
            summaryRect = summaryPanel.GetComponent<RectTransform>();
            summaryRect.anchorMin = new Vector2(0f, 1f);
            summaryRect.anchorMax = new Vector2(1f, 1f);
            summaryRect.pivot = new Vector2(0.5f, 0f);
            summaryRect.anchoredPosition = new Vector2(0f, SummaryPanelGap);
            summaryRect.sizeDelta = new Vector2(0f, SummaryBaseHeight);
            var summaryLayout = summaryPanel.GetComponent<LayoutElement>();
            summaryLayout.ignoreLayout = true;
            var summaryImage = summaryPanel.GetComponent<Image>();
            if (expandedParent.TryGetComponent<Image>(out var sourceImage))
            {
                summaryImage.sprite = sourceImage.sprite;
                summaryImage.type = sourceImage.type;
                summaryImage.color = sourceImage.color;
                summaryImage.material = sourceImage.material;
            }
            else if (flyImage != null)
            {
                summaryImage.sprite = flyImage.sprite;
                summaryImage.type = flyImage.type;
                summaryImage.color = flyImage.color;
                summaryImage.material = flyImage.material;
            }
            
            summaryImage.preserveAspect = false;
            summaryImage.raycastTarget = false;
            summaryLabels = Object.Instantiate(reserveLabel, summaryPanel.transform);
            summaryLabels.name = "Labels";
            summaryLabels.text = "Air Resupply Capacity";
            summaryLabels.richText = true;
            summaryLabels.alignment = TextAlignmentOptions.TopLeft;
            summaryLabels.enableWordWrapping = false;
            summaryLabels.overflowMode = TextOverflowModes.Overflow;
            summaryLabels.raycastTarget = false;
            var labelsRect = summaryLabels.rectTransform;
            labelsRect.anchorMin = Vector2.zero;
            labelsRect.anchorMax = Vector2.one;
            labelsRect.pivot = new Vector2(0.5f, 0.5f);
            labelsRect.offsetMin = new Vector2(10f, 8f);
            labelsRect.offsetMax = new Vector2(-170f, -8f);
            labelsRect.localScale = Vector3.one;
            
            summaryMultipliers = Object.Instantiate(summaryLabels, summaryPanel.transform);
            summaryMultipliers.name = "Multipliers";
            summaryMultipliers.text = string.Empty;
            summaryMultipliers.richText = false;
            summaryMultipliers.alignment = TextAlignmentOptions.Top;
            var multipliersRect = summaryMultipliers.rectTransform;
            multipliersRect.anchorMin = Vector2.zero;
            multipliersRect.anchorMax = Vector2.one;
            multipliersRect.pivot = new Vector2(0.5f, 0.5f);
            multipliersRect.offsetMin = new Vector2(12f, 8f);
            multipliersRect.offsetMax = new Vector2(12f, -8f);
            multipliersRect.localScale = Vector3.one;
            
            summaryValues = Object.Instantiate(summaryLabels, summaryPanel.transform);
            summaryValues.name = "Values";
            summaryValues.text = string.Empty;
            summaryValues.richText = false;
            summaryValues.alignment = TextAlignmentOptions.TopRight;
            var valuesRect = summaryValues.rectTransform;
            valuesRect.offsetMin = new Vector2(200f, 8f);
            valuesRect.offsetMax = new Vector2(-10f, -8f);
            summaryPanel.SetActive(false);
        }
        
        var row = new GameObject("NO_AR_AirResupplyPreference", typeof(RectTransform), typeof(LayoutElement),
            typeof(HorizontalLayoutGroup), typeof(Toggle));
        row.transform.SetParent(container, false);
        
        // Reorder checkbox to be on top
        row.transform.SetSiblingIndex(reserveRow.transform.GetSiblingIndex());
        
        var rowRect = row.GetComponent<RectTransform>();
        rowRect.sizeDelta = new Vector2(0f, 30f);
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
        if (flyImage != null)
        {
            boxImage.sprite = flyImage.sprite;
            boxImage.type = flyImage.type;
            boxImage.color = flyImage.color;
            boxImage.material = flyImage.material;
        }
        
        var checkedBoxColor = flyButton.colors.normalColor * flyButton.colors.colorMultiplier;
        var uncheckedBoxColor = new Color(0.45f, 0.45f, 0.45f, 1f);
        
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
        toggle.transition = Selectable.Transition.None;
        toggle.navigation = new Navigation { mode = Navigation.Mode.None };
        
        UnityAction<float> fuelLevelChanged = _ => { RefreshCapabilitySummary(menu); };
        var view = new View(topSpacer, row, toggle, boxImage, checkedBoxColor, uncheckedBoxColor, reserveRow,
            reserveSlider, reserveValue, reserveCanvasGroup, fuelLevel, fuelLevelChanged, containerRect, expandedParent,
            containerSpacing, summaryPanel, summaryRect, summaryLabels, summaryMultipliers, summaryValues);
        Views[menu] = view;
        
        toggle.onValueChanged.AddListener(value =>
        {
            if (view.SuppressCallback || string.IsNullOrEmpty(view.JsonKey))
                return;
            
            var preference = GetOrCreatePreference(view.JsonKey);
            preference.Enabled = value;
            SetToggleVisual(view, value);
            if (_hasServerSettings)
            {
                view.ReserveRow.SetActive(true);
                SetReserveVisible(view, value);
            }
            
            RefreshCapabilitySummary(menu);
        });
        
        reserveSlider.onValueChanged.AddListener(value =>
        {
            if (view.SuppressCallback || string.IsNullOrEmpty(view.JsonKey))
                return;
            
            var preference = GetOrCreatePreference(view.JsonKey);
            preference.ReservePercent = (byte)Mathf.Clamp(Mathf.RoundToInt(value), _serverMinimumReservePercent, 100);
            preference.ReserveExplicitlySet = true;
            view.ReserveValue.text = $"{preference.ReservePercent}%";
            RefreshCapabilitySummary(menu);
        });
        
        fuelLevel.onValueChanged.AddListener(fuelLevelChanged);
        UpdateForSelection(menu);
    }
    
    internal static void UpdateForSelection(AircraftSelectionMenu menu)
    {
        if (menu == null || !Views.TryGetValue(menu, out var view))
            return;
        
        var definition = menu.GetSelectedType();
        var key = definition?.jsonKey ?? string.Empty;
        var visible = _hasServerPolicy && key.Length > 0 && _serverProviderKeys.Contains(key);
        if (!visible)
        {
            view.TopSpacer.SetActive(false);
            view.ToggleRow.SetActive(false);
            view.ReserveRow.SetActive(false);
            SetExpandedHeight(view, 0f);
            view.JsonKey = string.Empty;
            if (view.SummaryPanel != null)
                view.SummaryPanel.SetActive(false);
            
            return;
        }
        
        view.JsonKey = key;
        var preference = GetOrCreatePreference(key);
        var reserveSupported = _hasServerSettings;
        var reserveVisible = reserveSupported && preference.Enabled;
        view.TopSpacer.SetActive(true);
        view.ToggleRow.SetActive(true);
        view.ReserveRow.SetActive(reserveSupported);
        SetReserveVisible(view, reserveVisible);
        
        var requiredHeight = ProviderSectionTopGap + view.ContainerSpacing + 30f + view.ContainerSpacing;
        if (reserveSupported)
        {
            var reserveHeight = view.ReserveRow.GetComponent<RectTransform>().sizeDelta.y;
            requiredHeight += reserveHeight + view.ContainerSpacing;
        }
        
        SetExpandedHeight(view, requiredHeight);
        view.SuppressCallback = true;
        view.Toggle.SetIsOnWithoutNotify(preference.Enabled);
        SetToggleVisual(view, preference.Enabled);
        if (reserveSupported)
        {
            preference.ReservePercent = (byte)Mathf.Clamp(preference.ReservePercent, _serverMinimumReservePercent, 100);
            view.ReserveSlider.minValue = _serverMinimumReservePercent;
            view.ReserveSlider.maxValue = 100f;
            view.ReserveSlider.wholeNumbers = true;
            view.ReserveSlider.SetValueWithoutNotify(preference.ReservePercent);
            view.ReserveValue.text = $"{preference.ReservePercent}%";
        }
        
        view.SuppressCallback = false;
        RefreshCapabilitySummary(menu);
    }
    
    internal static void SendCurrentPreference(AircraftSelectionMenu menu)
    {
        if (menu == null || !_hasServerPolicy)
            return;
        
        var definition = menu.GetSelectedType();
        var key = definition?.jsonKey ?? string.Empty;
        if (key.Length == 0 || !_serverProviderKeys.Contains(key))
            return;
        
        var preference = GetOrCreatePreference(key);
        if (!AirResupplyNetworking.TrySendProviderPreference(key, preference.Enabled, preference.ReservePercent))
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
        if (existing.FuelLevel != null)
            existing.FuelLevel.onValueChanged.RemoveListener(existing.FuelLevelChanged);
        
        SetExpandedHeight(existing, 0f);
        
        if (existing.TopSpacer != null)
            Object.Destroy(existing.TopSpacer);
        
        if (existing.ToggleRow != null)
            Object.Destroy(existing.ToggleRow);
        
        if (existing.ReserveRow != null)
            Object.Destroy(existing.ReserveRow);
        
        if (existing.SummaryPanel != null)
            Object.Destroy(existing.SummaryPanel);
    }
    
    private static void SetExpandedHeight(View view, float requiredHeight)
    {
        requiredHeight = Mathf.Max(0f, requiredHeight);
        var delta = requiredHeight - view.AppliedExtraHeight;
        if (Mathf.Abs(delta) <= 0.01f)
            return;
        
        if (view.ExpandedParent != null)
            view.ExpandedParent.sizeDelta =
                new Vector2(view.ExpandedParent.sizeDelta.x, view.ExpandedParent.sizeDelta.y + delta);
        
        view.AppliedExtraHeight = requiredHeight;
        Canvas.ForceUpdateCanvases();
        if (view.ContainerRect != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(view.ContainerRect);
        
        if (view.ExpandedParent != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(view.ExpandedParent);
    }
    
    internal static void RefreshCapabilitySummary(AircraftSelectionMenu menu)
    {
        if (menu == null || !Views.TryGetValue(menu, out var view) || view.SummaryPanel == null ||
            view.SummaryRect == null || view.SummaryLabels == null || view.SummaryMultipliers == null ||
            view.SummaryValues == null) return;
        
        if (!_hasServerSettings || string.IsNullOrEmpty(view.JsonKey) || menu.previewAircraft == null)
        {
            view.SummaryPanel.SetActive(false);
            return;
        }
        
        var preference = GetOrCreatePreference(view.JsonKey);
        if (!preference.Enabled)
        {
            view.SummaryPanel.SetActive(false);
            return;
        }
        
        var startingFuelRatio = Mathf.Clamp01(view.FuelLevel.value);
        var reserveRatio = preference.ReservePercent * 0.01f;
        AirResupplyManager.GetProviderPreviewCapacity(menu.previewAircraft, startingFuelRatio, reserveRatio,
            out var mainFuel, out var externalFuel, out var fuelCargo, out var ammoKg);
        if (_serverFuelEnabled)
        {
            mainFuel *= _serverFuelTransferMultiplier;
            externalFuel *= _serverFuelTransferMultiplier;
            fuelCargo *= _serverFuelTransferMultiplier;
        }
        else
        {
            mainFuel = 0f;
            externalFuel = 0f;
            fuelCargo = 0f;
        }
        
        if (_serverAmmoEnabled)
            ammoKg *= _serverAmmoTransferMultiplier;
        else
            ammoKg = 0f;
        
        var labels = new List<string>
        {
            "<b>AIR RESUPPLY CAPACITY</b>"
        };
        
        var values = new List<string>
        {
            string.Empty
        };
        
        var multipliers = new List<string>
        {
            string.Empty
        };
        
        if (mainFuel > 0.01f)
        {
            labels.Add("Main Fuel");
            multipliers.Add(FormatMultiplier(_serverFuelTransferMultiplier));
            values.Add($"{mainFuel / 1000f:F1} kL");
        }
        
        if (externalFuel > 0.01f)
        {
            labels.Add("Drop Tanks");
            multipliers.Add(FormatMultiplier(_serverFuelTransferMultiplier));
            values.Add($"{externalFuel / 1000f:F1} kL");
        }
        
        if (fuelCargo > 0.01f)
        {
            labels.Add("Fuel Cargo");
            multipliers.Add(FormatMultiplier(_serverFuelTransferMultiplier));
            values.Add($"{fuelCargo / 1000f:F1} kL");
        }
        
        if (ammoKg > 0.01f)
        {
            labels.Add("Ammo Cargo");
            multipliers.Add(FormatMultiplier(_serverAmmoTransferMultiplier));
            values.Add($"{ammoKg / 1000f:F1} t");
        }
        
        if (labels.Count == 1)
        {
            labels.Add("No transferable supplies");
            multipliers.Add(string.Empty);
            values.Add(string.Empty);
        }
        
        view.SummaryLabels.text = string.Join("\n", labels);
        view.SummaryMultipliers.text = string.Join("\n", multipliers);
        view.SummaryValues.text = string.Join("\n", values);
        var rowCount = labels.Count - 1;
        view.SummaryRect.sizeDelta =
            new Vector2(view.SummaryRect.sizeDelta.x, SummaryBaseHeight + rowCount * SummaryRowHeight);
        view.SummaryPanel.SetActive(true);
    }
    
    private static void SetToggleVisual(View view, bool enabled)
    {
        if (view.ToggleBoxImage != null)
            view.ToggleBoxImage.color = enabled ? view.ToggleCheckedColor : view.ToggleUncheckedColor;
    }
    
    private static string FormatMultiplier(float multiplier) =>
        Mathf.Abs(multiplier - 1f) <= 0.001f ? string.Empty : $"({multiplier:F1}x)";
    
    private static void SetReserveVisible(View view, bool visible)
    {
        view.ReserveCanvasGroup.alpha = visible ? 1f : 0f;
        view.ReserveCanvasGroup.interactable = visible;
        view.ReserveCanvasGroup.blocksRaycasts = visible;
    }
    
    private static ProviderPreference GetOrCreatePreference(string jsonKey)
    {
        if (Preferences.TryGetValue(jsonKey, out var preference))
            return preference;
        
        preference = new ProviderPreference(_serverDefaultEnabled, _serverDefaultReservePercent);
        Preferences.Add(jsonKey, preference);
        return preference;
    }
    
    private sealed class ProviderPreference
    {
        internal bool Enabled;
        internal bool ReserveExplicitlySet;
        internal byte ReservePercent;
        
        internal ProviderPreference(bool enabled, byte reservePercent)
        {
            Enabled = enabled;
            ReservePercent = reservePercent;
            ReserveExplicitlySet = false;
        }
    }
    
    private sealed class View
    {
        internal readonly RectTransform? ContainerRect;
        internal readonly float ContainerSpacing;
        internal readonly RectTransform? ExpandedParent;
        internal readonly Slider FuelLevel;
        internal readonly UnityAction<float> FuelLevelChanged;
        internal readonly CanvasGroup ReserveCanvasGroup;
        internal readonly GameObject ReserveRow;
        internal readonly Slider ReserveSlider;
        internal readonly TextMeshProUGUI ReserveValue;
        internal readonly TextMeshProUGUI? SummaryLabels;
        internal readonly TextMeshProUGUI? SummaryMultipliers;
        internal readonly GameObject? SummaryPanel;
        internal readonly RectTransform? SummaryRect;
        internal readonly TextMeshProUGUI? SummaryValues;
        internal readonly Toggle Toggle;
        internal readonly Image ToggleBoxImage;
        internal readonly Color ToggleCheckedColor;
        internal readonly GameObject ToggleRow;
        internal readonly Color ToggleUncheckedColor;
        internal readonly GameObject TopSpacer;
        internal float AppliedExtraHeight;
        internal string JsonKey = string.Empty;
        internal bool SuppressCallback;
        
        internal View(GameObject topSpacer, GameObject toggleRow, Toggle toggle, Image boxImage, Color checkedBoxColor,
            Color uncheckedBoxColor, GameObject reserveRow, Slider reserveSlider, TextMeshProUGUI reserveValue,
            CanvasGroup reserveCanvasGroup, Slider fuelLevel, UnityAction<float> fuelLevelChanged,
            RectTransform? containerRect, RectTransform? expandedParent, float containerSpacing,
            GameObject? summaryPanel, RectTransform? summaryRect, TextMeshProUGUI? summaryLabels,
            TextMeshProUGUI? summaryMultipliers, TextMeshProUGUI? summaryValues)
        {
            TopSpacer = topSpacer;
            ToggleRow = toggleRow;
            Toggle = toggle;
            ToggleBoxImage = boxImage;
            ToggleCheckedColor = checkedBoxColor;
            ToggleUncheckedColor = uncheckedBoxColor;
            ReserveRow = reserveRow;
            ReserveSlider = reserveSlider;
            ReserveValue = reserveValue;
            ReserveCanvasGroup = reserveCanvasGroup;
            FuelLevel = fuelLevel;
            FuelLevelChanged = fuelLevelChanged;
            ContainerRect = containerRect;
            ExpandedParent = expandedParent;
            ContainerSpacing = containerSpacing;
            SummaryPanel = summaryPanel;
            SummaryRect = summaryRect;
            SummaryLabels = summaryLabels;
            SummaryMultipliers = summaryMultipliers;
            SummaryValues = summaryValues;
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
        ProviderSelectionUi.OnMenuInitialised(__instance, __instance.infoPanel, __instance.flyButton,
            __instance.fuelLevel);
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
    
    [HarmonyPatch(nameof(AircraftSelectionMenu.UpdateReadouts))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void UpdateReadouts_Postfix(AircraftSelectionMenu __instance)
    {
        ProviderSelectionUi.RefreshCapabilitySummary(__instance);
    }
}