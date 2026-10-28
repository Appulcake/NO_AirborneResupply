using UnityEngine;

namespace NO_AR;

internal static class AirResupplyGunSync
{
    internal static bool TryGetSingleGun(WeaponStation station, out Gun gun)
    {
        gun = null!;
        if (station?.Weapons == null || station.Weapons.Count != 1 || station.Weapons[0] is not Gun found)
            return false;
        
        gun = found;
        return true;
    }
    
    internal static int GetConservativeGunAmmo(WeaponStation station, Gun gun)
    {
        var full = Mathf.Max(0, Mathf.Min(station.FullAmmo, gun.GetFullAmmo()));
        return Mathf.Clamp(Mathf.Min(station.Ammo, gun.GetAmmoTotal()), 0, full);
    }
    
    internal static void SetGunAmmo(WeaponStation station, Gun gun, int desired)
    {
        var full = Mathf.Max(0, Mathf.Min(station.FullAmmo, gun.GetFullAmmo()));
        desired = Mathf.Clamp(desired, 0, full);
        var difference = desired - gun.ammo;
        if (difference != 0)
            gun.Rearm(difference, station);
        
        // Vanilla in Gun.Rearm() incorrectly takes full capacity including loaded magazine, instead of only counting
        // reserve magazines here
        var capacity = gun.magazineCapacity;
        if (capacity > 0)
        {
            var reserves = desired > 0 ? (desired - 1) / capacity : 0;
            reserves = Mathf.Clamp(reserves, 0, Mathf.Max(0, gun.maxMagazines));
            gun.magazines = reserves;
            gun.bulletsLoaded = desired - reserves * capacity;
            gun.timeUntilReload = 0f;
            gun.ReportReloading(false);
        }
        
        station.AccountAmmo();
        station.Updated();
    }
}