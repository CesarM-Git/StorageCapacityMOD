using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mafi;
using Mafi.Base.Prototypes.Trains;
using Mafi.Collections;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;

namespace StorageCapacityMod;

/// <summary>
/// Unlocks fluid/gas products that the vanilla game flags as <c>cannotBeStored</c>
/// so they can be selected in Fluid Storage buildings and on fluid train station
/// modules.
///
/// In vanilla, a fluid storage's product picker is driven by
/// <see cref="StorageProto.StorableProducts"/>, which is built once during
/// <see cref="StorageProto.OnInitialize"/> by filtering all <see cref="FluidProductProto"/>
/// through the predicate <c>x =&gt; x.IsStorable &amp;&amp; x.Radioactivity == 0</c>
/// (see <c>Mafi.Base.Prototypes.Storages.StoragesData.productFilter</c>).
/// The 10 vanilla fluids flagged unstorable via <c>FluidProductAttribute(cannotBeStored: true)</c>
/// are: CoreFuel, CoreFuelDirty, BlanketFuel, BlanketFuelEnriched, ChilledWater,
/// SteamSp, SteamHi, SteamLo, SteamDepleted, Exhaust. None have <c>Radioactivity &gt; 0</c>,
/// so flipping <c>IsStorable</c> is enough.
///
/// Train station modules (<see cref="TrainStationModuleProto"/>) follow the same
/// pattern: the inspector's product picker reads <see cref="TrainStationModuleProto.StorableProducts"/>,
/// also cached during <c>OnInitialize</c> using a near-identical predicate
/// <c>p.Type == ProductType &amp;&amp; p.IsStorable &amp;&amp; p.Radioactivity == 0</c>.
/// We rebuild that set too so unlocked fluids appear in the fluid station module's picker.
///
/// Fluid wagons (<see cref="Mafi.Core.Trains.CargoWagonProto"/>) don't need a rebuild:
/// their picker uses <c>ProductProto.CanBeLoadedOnTrain</c> which is a virtual
/// property that defaults to <c>IsStorable</c>. Since we flip <c>IsStorable</c>, the
/// wagon picker (and the runtime <c>CargoWagon.SubCargoWagon.CanReceive</c> check, which
/// only validates <c>ProductType</c>) accept the unlocked fluids automatically.
///
/// <see cref="TrainStationFuelProto"/> (locomotive fuel stations) is deliberately NOT
/// touched: its <c>StorableProducts</c> is populated explicitly from <c>AddFuel(...)</c>
/// calls during prototype registration, not from a filter, so it already lists the
/// correct locomotive fuels (Diesel, Heating Oil, etc.) — we don't want exhaust to
/// count as locomotive fuel.
///
/// Strategy: this runs from <see cref="StorageCapacityMod.Initialize"/>, which is
/// AFTER <c>ProtosDb.LockAndInitializeProtos</c> (so the cached
/// <see cref="StorageProto.StorableProducts"/> and <see cref="TrainStationModuleProto.StorableProducts"/>
/// sets already exist). We therefore both flip <c>IsStorable</c> AND manually rebuild
/// those sets via reflection on their private setters, so the new fluids appear in
/// the pickers immediately.
///
/// Future-proof against modded fluids: we don't hardcode the 10 product IDs — we just
/// flip every <see cref="FluidProductProto"/> with <c>IsStorable == false</c>.
/// </summary>
internal static class FluidStorageUnlocker
{
    private static FieldInfo s_isStorableField;
    private static PropertyInfo s_storableProductsProp;
    private static PropertyInfo s_stationStorableProductsProp;

    public static void Run(ProtosDb protosDb)
    {
        if (protosDb == null)
        {
            Log.Error("StorageCapacityMod: FluidStorageUnlocker received null protosDb.");
            return;
        }

        try
        {
            int flipped = FlipUnstorableFluids(protosDb);
            int rebuilt = RebuildFluidStorableSets(protosDb);
            int rebuiltStations = RebuildTrainStationModuleStorableSets(protosDb);
            Log.Info($"StorageCapacityMod: fluid unlock complete — {flipped} fluid(s) unlocked, "
                     + $"{rebuilt} fluid storage proto(s) rebuilt, "
                     + $"{rebuiltStations} train station module proto(s) rebuilt.");
            LogPickerVisibility(protosDb);
            LogStationModulePickerVisibility(protosDb);
        }
        catch (Exception ex)
        {
            Log.Error($"StorageCapacityMod: FluidStorageUnlocker failed: {ex}");
        }
    }

    /// <summary>
    /// Flips <see cref="ProductProto.IsStorable"/> from false to true on every fluid
    /// product currently flagged unstorable. The field is <c>public readonly</c>; .NET
    /// Framework 4.8 lets reflection bypass that.
    /// </summary>
    private static int FlipUnstorableFluids(ProtosDb protosDb)
    {
        if (s_isStorableField == null)
        {
            s_isStorableField = typeof(ProductProto).GetField(
                "IsStorable",
                BindingFlags.Instance | BindingFlags.Public);
            if (s_isStorableField == null)
            {
                Log.Error("StorageCapacityMod: ProductProto.IsStorable field not found via reflection.");
                return 0;
            }
        }

        int count = 0;
        foreach (FluidProductProto fluid in protosDb.All<FluidProductProto>())
        {
            if (fluid == null) continue;
            if (fluid.IsStorable) continue;
            // Radioactivity isn't a blocker for any vanilla fluid, but log it just in case
            // a mod-added fluid has radioactivity > 0 — flipping IsStorable alone wouldn't
            // be enough then.
            if (fluid.Radioactivity > 0)
            {
                Log.Warning($"StorageCapacityMod: fluid '{fluid.Id.Value}' has Radioactivity={fluid.Radioactivity} — "
                            + "unlocking IsStorable but the vanilla filter will still reject it. "
                            + "If this product needs to be storable, also patch Radioactivity to 0.");
            }
            try
            {
                s_isStorableField.SetValue(fluid, true);
                Log.Info($"StorageCapacityMod: unlocked fluid '{fluid.Id.Value}' (IsStorable: false → true).");
                count++;
            }
            catch (Exception ex)
            {
                Log.Error($"StorageCapacityMod: failed to flip IsStorable on '{fluid.Id.Value}': {ex.Message}");
            }
        }
        return count;
    }

    /// <summary>
    /// Re-runs the original <see cref="StorageProto.IsProductSupported"/> filter on
    /// every <see cref="FluidStorageProto"/> and replaces the cached
    /// <see cref="StorageProto.StorableProducts"/> set via reflection (the setter is
    /// <c>private</c>). This makes our newly-unlocked fluids visible in the picker without
    /// needing to touch the filter delegate itself, so we stay compatible with the
    /// vanilla and any mod-extended fluid storages alike.
    /// </summary>
    private static int RebuildFluidStorableSets(ProtosDb protosDb)
    {
        if (s_storableProductsProp == null)
        {
            s_storableProductsProp = typeof(StorageProto).GetProperty(
                "StorableProducts",
                BindingFlags.Instance | BindingFlags.Public);
            if (s_storableProductsProp == null)
            {
                Log.Error("StorageCapacityMod: StorageProto.StorableProducts property not found via reflection.");
                return 0;
            }
            if (!s_storableProductsProp.CanWrite)
            {
                Log.Error("StorageCapacityMod: StorageProto.StorableProducts has no setter — cannot rebuild.");
                return 0;
            }
        }

        // Target every fluid storage. Mod-added fluid storages that derive from FluidStorageProto
        // are included automatically. (UnitStorageProto and LooseStorageProto restrict ProductType
        // to non-fluid types so they're untouched.)
        var fluidStorages = protosDb.All<FluidStorageProto>().ToList();
        int rebuilt = 0;
        foreach (FluidStorageProto storage in fluidStorages)
        {
            if (storage == null) continue;
            try
            {
                var oldSet = storage.StorableProducts;
                var newList = protosDb.Filter<ProductProto>(storage.IsProductSupported).ToList();
                var newSet = new Set<ProductProto>(newList);
                s_storableProductsProp.SetValue(storage, newSet);
                int oldCount = oldSet?.Count ?? 0;
                int newCount = newSet.Count;
                int added = newCount - oldCount;
                Log.Info($"StorageCapacityMod: rebuilt StorableProducts on '{storage.Id.Value}' "
                         + $"({oldCount} → {newCount}, +{added}).");
                rebuilt++;
            }
            catch (Exception ex)
            {
                Log.Error($"StorageCapacityMod: failed to rebuild StorableProducts on '{storage.Id.Value}': {ex.Message}");
            }
        }
        return rebuilt;
    }

    /// <summary>
    /// Sanity log: spells out what the picker will actually show for the first fluid storage
    /// prototype, after the rebuild. The picker reads <see cref="StorageProto.StorableProducts"/>
    /// lazily on each open (the lambda passed into <c>SingleProductPickerUi</c> is invoked from
    /// <c>ProtoPickerPopup.m_optionsProvider()</c> at popup-render time), and pipes the result
    /// through <c>UnlockedProtosDbForUi.FilterUnlocked</c>. Fluid product protos are never added
    /// to a research node's <c>Units</c> list (only their producer machines/recipes are), so the
    /// unlock-filter never strips them; what <see cref="StorageProto.StorableProducts"/> contains
    /// is what the player sees. This log lets you eyeball the picker contents from Player.log
    /// instead of guessing.
    /// </summary>
    private static void LogPickerVisibility(ProtosDb protosDb)
    {
        try
        {
            FluidStorageProto sample = null;
            foreach (var s in protosDb.All<FluidStorageProto>()) { sample = s; break; }
            if (sample == null)
            {
                Log.Info("StorageCapacityMod: no FluidStorageProto found — nothing to verify.");
                return;
            }
            var visible = sample.StorableProducts;
            int count = visible?.Count ?? 0;
            Log.Info($"StorageCapacityMod: fluid picker on '{sample.Id.Value}' will offer {count} product(s):");
            if (visible != null)
            {
                foreach (ProductProto p in visible)
                {
                    Log.Info($"StorageCapacityMod:   - {p.Id.Value} (IsStorable={p.IsStorable}, IsAvailable={p.IsAvailable}, IsUnlocked={p.IsUnlocked})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"StorageCapacityMod: LogPickerVisibility failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-runs <see cref="TrainStationModuleProto.IsProductSupported"/> on every
    /// <see cref="TrainStationModuleProto"/> and replaces the cached
    /// <see cref="TrainStationModuleProto.StorableProducts"/> set via reflection on its
    /// private setter. The fluid-typed station module's picker (built by
    /// <c>TrainStationModuleInspector</c> through <c>Entity.Prototype.StorableProducts</c>)
    /// then immediately sees the newly-unlocked fluids without needing a UI rebuild.
    ///
    /// Loose / unit station modules are touched too but stay unchanged because their
    /// predicate filters out non-matching <c>ProductType</c>s — the rebuild simply re-runs
    /// the existing filter, so it's a no-op for them.
    /// </summary>
    private static int RebuildTrainStationModuleStorableSets(ProtosDb protosDb)
    {
        if (s_stationStorableProductsProp == null)
        {
            s_stationStorableProductsProp = typeof(TrainStationModuleProto).GetProperty(
                "StorableProducts",
                BindingFlags.Instance | BindingFlags.Public);
            if (s_stationStorableProductsProp == null)
            {
                Log.Error("StorageCapacityMod: TrainStationModuleProto.StorableProducts property not found via reflection.");
                return 0;
            }
            if (!s_stationStorableProductsProp.CanWrite)
            {
                Log.Error("StorageCapacityMod: TrainStationModuleProto.StorableProducts has no setter — cannot rebuild.");
                return 0;
            }
        }

        var stationModules = protosDb.All<TrainStationModuleProto>().ToList();
        int rebuilt = 0;
        foreach (TrainStationModuleProto stationModule in stationModules)
        {
            if (stationModule == null) continue;
            try
            {
                var oldSet = stationModule.StorableProducts;
                var newList = protosDb.Filter<ProductProto>(stationModule.IsProductSupported).ToList();
                var newSet = new Set<ProductProto>(newList);
                s_stationStorableProductsProp.SetValue(stationModule, newSet);
                int oldCount = oldSet?.Count ?? 0;
                int newCount = newSet.Count;
                int added = newCount - oldCount;
                Log.Info($"StorageCapacityMod: rebuilt StorableProducts on train station module '{stationModule.Id.Value}' "
                         + $"({oldCount} → {newCount}, +{added}).");
                rebuilt++;
            }
            catch (Exception ex)
            {
                Log.Error($"StorageCapacityMod: failed to rebuild StorableProducts on train station module '{stationModule.Id.Value}': {ex.Message}");
            }
        }
        return rebuilt;
    }

    /// <summary>
    /// Sanity log analogous to <see cref="LogPickerVisibility"/> but for fluid-typed
    /// train station modules. Picks the first <see cref="TrainStationModuleProto"/> whose
    /// <c>ProductType</c> is the fluid product type and dumps the rebuilt
    /// <see cref="TrainStationModuleProto.StorableProducts"/> set into Player.log.
    /// <para>
    /// Note: <see cref="ProductType"/> is a <c>struct</c>, not an enum. There's no
    /// <c>ProductType.Fluid</c> constant — the fluid product type is exposed as the
    /// static field <see cref="FluidProductProto.ProductType"/>.
    /// </para>
    /// </summary>
    private static void LogStationModulePickerVisibility(ProtosDb protosDb)
    {
        try
        {
            TrainStationModuleProto sample = null;
            foreach (var m in protosDb.All<TrainStationModuleProto>())
            {
                if (m.ProductType == FluidProductProto.ProductType) { sample = m; break; }
            }
            if (sample == null)
            {
                Log.Info("StorageCapacityMod: no fluid TrainStationModuleProto found — nothing to verify.");
                return;
            }
            var visible = sample.StorableProducts;
            int count = visible?.Count ?? 0;
            Log.Info($"StorageCapacityMod: fluid station module picker on '{sample.Id.Value}' will offer {count} product(s):");
            if (visible != null)
            {
                foreach (ProductProto p in visible)
                {
                    Log.Info($"StorageCapacityMod:   - {p.Id.Value} (IsStorable={p.IsStorable}, IsAvailable={p.IsAvailable}, IsUnlocked={p.IsUnlocked})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"StorageCapacityMod: LogStationModulePickerVisibility failed: {ex.Message}");
        }
    }
}
