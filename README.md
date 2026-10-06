# ShipSystems

![About Y4NGZ](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shared/about-y4ngz.png)

I am Y4NGZ, an up and coming rapper out of NYC/Detroit. All of my mods are
inspired by the music I write. If you are interested in knowing more about me,
check out my SoundCloud: https://soundcloud.com/y4ngz

## Generative AI usage

The entire code base of this mod was created with generative AI. Assets are
free, licensed, paid for, or generated with AI tools. If you do not wish to play
with AI-generated content, do not install this mod.

## What ShipSystems does

Ship Systems adds a fuel system to the ship, with a fuel chute to deposit fuel items in. It also
adds a ship battery that enemies can destroy. A dead battery grounds the ship until auto-pilot
takes off, unless the crew restores power with a spare battery or the apparatus. Both systems
are configurable, and fuel can be turned off on its own.

## Requirements

Your mod manager installs these automatically:

- **BepInExPack**
- **DawnLib**
- **Y4NGZCore**

## Fuel

A movable fuel chute in the ship is where fuel is deposited. Every moon has a fuel cost by
default, and you cannot route to a moon you cannot afford. Some moons cost no fuel, so the
crew can never get stranded. Light fuel, dense fuel and natural fuel spawn inside the
facilities, and each one's fuel value is set in the config. The fuel storage refills when you
complete a quota.

![Fuel acceptance](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shipsystems/fuel-acceptance.gif)

![Light, dense, and natural fuel items](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shipsystems/items.png)

The ship monitor shows your stored fuel, and the terminal moon list shows each
route's fuel cost.

![Ship monitor fuel level](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shipsystems/monitor.png)

![Terminal moon fuel costs](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shipsystems/terminal.png)

## Battery

The battery exists to be destroyed. Masked enemies, or other enemies set in the config, can
destroy it when a player is inside the ship with the doors closed, or by walking to it on
their own by chance. With the battery gone, every light and monitor in the ship shuts down,
and the ship cannot leave until auto-pilot kicks in at midnight.

To restore power:

- **Apparatus** - always works. Hold it and interact with the battery slot.
- **Spare battery** - spawns inside the facility. Interiors without an apparatus always spawn
  one. Stocking up on spares makes outages a small problem.
- **Repair** - with Y4NGZ Upgrades installed, the Field Mechanic upgrade lets a player repair
  the broken battery by interacting with it.

![Battery critical warnings](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shipsystems/battery-critical.gif)

## Configuration

The configuration file is `BepInEx/config/com.y4ngz.company.shipsystems.cfg`. It has a master
switch and a fuel switch (turning fuel off keeps the battery). It also sets the tank size,
moon fuel prices, each fuel item's value and spawn weight, the fuel monitor screen, battery
durability, the battery sabotage chance and extra enemies allowed to damage the battery.

## Compatibility

Independent installation: Ship Systems needs only Y4NGZCore and the libraries listed under
Requirements. The other Y4NGZ packages are optional companions - install any combination
and each one lights up its extra behavior.

The required Core package is `Y4NGZ313-Y4NGZCore-1.0.10` or newer.

| Mod | What it adds |
| --- | --- |
| **Contracted** | The Stranger can destroy the ship battery; contract assignments show on the ship monitors. |
| **Y4NGZ Upgrades** | Field Mechanic battery repairs, the fuel-chute side of Scavenger, and fuel, power and battery upgrades. |
| **LethalConstellations** | Crossing into another constellation costs an extra entry fee, and terminal route screens name your constellations. |
| **GeneralImprovements** | With Better Monitors on, the fuel monitor takes a Better Monitors screen (Screen9 by default). |
| **2 Story Ship** | The battery sits in its own spot on the two-story ship. |
| **Wider Ship Mod** | The battery sits in its own spot on the wider ship. |
| **MoonsPlus** | Compatible with MoonsPlus. |

## GitHub

Source code: https://github.com/y4ngz313/ShipSystems

## Bugs

Report bugs at https://github.com/y4ngz313/ShipSystems/issues.
