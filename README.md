# Storage Capacity Override

A mod for [Captain of Industry](https://www.captain-of-industry.com/) that lets
you change any storage building's capacity from its inspector, and unlocks the
vanilla fluids the game marks "cannot be stored".

**Version 1.2.2** · Game **0.8.7** · by Nimb

## What it does

### Capacity override

Click any storage building and the inspector gains a capacity panel:

- a **text field** to type an exact capacity
- **quick adjustments**: `Half`, `-500`, `-100`, `+100`, `+500`, `x2`
- **current vs default** capacity readout
- a **reset** back to the prototype default

Works on unit, loose, fluid and radioactive waste storages. Overrides are saved
per game and survive **moves and tier upgrades** — the game normally resets a
storage's buffer to the prototype default whenever the building is relocated or
upgraded, and this mod re-applies your value afterwards.

### Fluid unlock

Vanilla fluids flagged "cannot be stored" become selectable in any Fluid Storage:
**Steam** (super / high / low / depleted), **Exhaust**, **Core Fuel**, **Core Fuel
(spent)**, **Blanket Fuel**, **Blanket Fuel (enriched)**, and **Chilled Water**.

These also appear in the **fluid train station module's** product picker, so the
whole fluid-train chain — wagon filters, schedule load/unload filters, depart
conditions — accepts them.

Mod-added fluids using the same vanilla flag are unlocked automatically.

## Install

Extract the release zip into your Captain of Industry mods folder, then enable
**Storage Capacity Override** in the in-game mod list.

Safe to add to and remove from existing saves. Overrides are stored in a JSON
file inside the mod folder, one per save, so removing the mod leaves your saves
intact — storages simply return to their default capacities.

## Notes and limits

- **Overrides are absolute, not multipliers.** An override of 50,000 stays 50,000
  after a tier upgrade rather than scaling with the new tier's default. Adjust
  again after upgrading if you want the larger value.
- **Locomotive refuel stations are deliberately unchanged.** Their allowed-fuel
  list is curated explicitly by the game, so exhaust will not show up as a
  locomotive fuel.
- **Gameplay++ compatible.** The Warehouse's 4 product slots and per-port mapping
  panel, and the Parking HQ's full dashboard, both render correctly with this mod
  installed.

## Compatibility

Requires game **0.8.7**. For 0.8.3–0.8.6 use **v1.2.1**.

Every version is verified against the decompiled game assemblies before release —
see [`CHANGELOG.md`](CHANGELOG.md) for what was checked each time.

## Building from source

Requires the game installed and two environment variables:

| Variable | Value |
| --- | --- |
| `COI_ROOT` | your Captain of Industry install directory |
| `COI_MODS` | the game's mods directory |

```bash
dotnet build src/StorageCapacityMod.csproj -c Release
```

The build reads the version from `src/manifest.json`, deploys to
`%COI_MODS%\StorageCapacityMod\`, and produces `StorageCapacityMod_<version>.zip`
next to it.

## License

See [`LICENSE`](LICENSE).
