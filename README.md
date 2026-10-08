### Airborne Resupply
Adds a simplistic airborne resupply to Nuclear Option, allowing players to opt into being a resupplier role, 
capable of refuelling and/or rearming players while in the air. Resupply works by being within x distance 
(by default 1000m) for y seconds (by default 10s) of a supplier while both airborne above a minimum radar 
altitude (by default 5m), upon which service completes and the logistics pilot is rewarded (for both 
refuel and rearming).

Mod is needed on server and client, but should be safe to have on client when joining servers that don't 
have it (it'll just not do anything).

Most parts are configurable, with majority of the settings being host/server authoritative, which get synced 
to clients.

To opt in being a supplier, enable this checkbox before deploying, near the Fly button:<br><br>
<img width="250" alt="1" src="https://github.com/user-attachments/assets/23b5f5c8-a706-4f6b-9c4e-3a32a0e22595" />

And equip yourself with valid resupply sources.
For refuelling, the priority is fuel cargo containers > Aryx Weaponry Pack external fuel tanks > main fuel.
The fuel cargo containers are those hardpoint options like 1500L, 10k L containers.
With this mod, if you're in a supplier role, Aryx' eternal tanks don't automatically drop, so that when they're 
emptied they can stay on and you can refill them again on the ground (you can still manually drop them like normal).

For rearming, you need to have munition containers on board that are capable of rearming ground units (so naval 
supply containers don't count), like the pallets, 10t container, Aryx' 50t container and so on.

For the fuel/ammo cargo containers, their contents are virtually tracked. Their quantity is 
deduced from their size (e.g. having 2x10t ammo containers means you have 20t worth of ammo to give out, having 
4x1.5k L fuel containers adds 6k L worth of fuel you can share on top of regular fuel), and doesn't actually 
modify the containers' contents. This means technically they can double dip - if you carry a 10t ammo container 
and resupply all 10t of it, then deploy it, it spawns a regular, fresh 10t ammo containers. This is to reduce 
complexity.
Both the fuel and ammo containers' virtual contents get refilled when you land on the ground and get rearmed by 
any ammo source (ammo truck/bunker/container etc).

Suppliers see stats on their HUD so they know how much fuel and ammo they have remaining to share:<br>

<img width="500" alt="image" src="https://github.com/user-attachments/assets/9c31ac21-3126-4619-8e14-f30883276e77" />

Additionally, everyone in the same faction can see suppliers on the map with how much % fuel and ammo they have 
remaining to hand give:<br>

<img width="234" alt="2" src="https://github.com/user-attachments/assets/c88eae2b-2949-4cb0-818b-8429b7ae7f03" />

Anyone not a supplier can resupply at them, when within a certain distance (by default 5km), you see the nearest 
supplier on your HUD, which also shows how close you are and how close you have to be to get serviced:

<img width="1024" alt="3" src="https://github.com/user-attachments/assets/d2cb4900-883c-4fdd-9af1-12af0fe0aa60" />

Once in range, you can see live state of servicing:

<img width="615" alt="mpv_38OOse28ei" src="https://github.com/user-attachments/assets/0c2378cf-5eb2-44f7-b635-d6c1525099a3" />

By default, rearming has a 5 minute cooldown, refuelling doesn't have any.
Settings configurable by and dictated by host, like general settings:
- Resupply range (default 1000m)
- Resupply time (time you need to spend in range to service, by default 10s)
- Check interval (how often server checks for service status, distance checks, pushes HUD updates, by default 1s)
- Minimum Radar Altitude (minimum RAlt both supplier and recipient has to be to be able to resupply, by default 5m)
- List of valid aircraft (by `jsonKey`) that can be suppliers (yes, you can make a cricket a refueller if you want), by
  default it's set to MC-260 Chimera, Tarantula, and Ibis
- Whether being a supplier is enabled by default (this is default checkbox state clients get the first time they select
  a supplier capable plane, and is what server defaults to if it doesn't get info from client for some reason)

Refuel specific settings (dictated by host):
- Whether airborne refuelling is enabled (on by default)
- Fuel transfer multiplier (by default 1, allows you to virtually increase/decrease how much fuel there's available, e.g. a
  4 multiplier means if you have 20k L onboard + 10k in a container, instead of 30k you have 120k to actually hand out, or in
  other words every 4L someone gets from you only consumes 1L from your stores)
- Refill virtual fuel on ground (by default enabled, with this on the fuel containers on board get restocked when rearming on
  the ground at an ammo source)
- Supplier internal fuel reserve percent (by default 15%, this is part of the base internal tank not available to hand out,
  so that you can't accidentally give away all your fuel resulting in engine flameout)

Rearm specific settings (dictated by host):
- Whether airborne rearming ammo is enabled (on by default)
- Ammo transfer multiplier (by default 1, same as with refueling, allows you to fine tune how much is actually available to
  give without modifying things like container size/mass etc, either lower or higher)
- Refill virtual ammo on ground (by default enabled, same as for the fuel containers, both fuel and ammo containers get restocked
  at an ammo source, this withdrawal doesn't consume any ammo from the source at the moment for simplicity)
- Rearm cooldown (by default 300s so 5 minutes, this is the cooldown someone gets when they rearm, to prevent someone just firing
  and rearming over and over)

Reward specific settings (dictated by host):
- Refuel reward per 1000L (by default 2 score per 1000L fuel someone gains from being airborne refuelled)
- Rearm reward multiplier (by default 2, multiplier on vanilla rearm bonus you get as supplier for rearming others)

HUD sections, one section that's server sided:
- Send HUD updates (on by default, this makes the HUD functionality work for clients, server sends all HUD related updates to
  make sure clients get updated, authoritative information)
- Display range (by default 5000m, this is how close someone needs to be to a valid supplier for someone to see them pop up on their
  HUD too, the map stats are globally visible but updated at lower interval)

And HUD section that's configurable by clients:
- HUD enabled (on by default, this is strictly for clients whether they want to opt into having a flight HUD element for viewing info
  related to either being a supplier or going to a supplier and getting serviced)
- X and Y position offsets that you can change (reflects live) so change where the text is on the flight HUD
