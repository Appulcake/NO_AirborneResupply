using System;
using HarmonyLib;

namespace NO_AR;

internal static class BoteCompatibility
{
    //private const string ShipBridgeTypeName = "BoscaliOceanTrainingExercise.ShipPartBridge";
    private const string ShipBridgeTypeName = "NOComponentWIP.ShipPartBridge";
    private static bool _resolved;
    private static Type? _shipBridgeType;

    internal static void Initialise()
    {
        if (_resolved)
            return;

        _resolved = true;
        _shipBridgeType = AccessTools.TypeByName(ShipBridgeTypeName);
    }

    internal static bool IsBoteShip(Aircraft aircraft)
    {
        if (aircraft == null)
            return false;

        if (!_resolved)
            Initialise();

        return _shipBridgeType != null && aircraft.GetComponent(_shipBridgeType) != null;
    }
}










