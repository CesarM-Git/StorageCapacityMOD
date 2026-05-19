using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Entities;

namespace StorageCapacityMod;

/// <summary>
/// Tracks capacity overrides for storage buildings and persists them to a JSON
/// file alongside the mod. On game load, re-applies all saved overrides after
/// the game has finished resetting buffer capacities to prototype defaults.
///
/// This class is registered as a [GlobalDependency] service so it can be
/// injected into the inspector and resolved from the DI container.
/// </summary>
public class CapacityOverrideManager
{
    /// <summary>
    /// Global instance, set by StorageCapacityMod.Initialize().
    /// Accessed by the inspector since DI registration isn't straightforward.
    /// </summary>
    public static CapacityOverrideManager Instance { get; set; }

    /// <summary>
    /// Maximum allowed capacity to avoid potential issues with logistics,
    /// UI rendering, or integer overflow in calculations.
    /// Quantity wraps an int, but we cap well below int.MaxValue.
    /// </summary>
    public const int MAX_CAPACITY = 500_000_000;
    public const int MIN_CAPACITY = 1;

    private static readonly MethodInfo s_forceCapacityMethod = typeof(StorageBase)
        .GetMethod(
            "ForceNewCapacityTo",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            new Type[] { typeof(Quantity) },
            null
        );

    /// <summary>
    /// In-memory map of EntityId.Value -> overridden capacity.
    /// Only contains entries where the player has set a non-default capacity.
    /// </summary>
    private readonly Dictionary<int, int> m_overrides = new Dictionary<int, int>();

    private readonly IEntitiesManager m_entitiesManager;
    private readonly string m_savePath;

    public CapacityOverrideManager(IEntitiesManager entitiesManager, string savePath)
    {
        m_entitiesManager = entitiesManager;
        m_savePath = savePath;
    }

    /// <summary>
    /// Sets the capacity of a storage entity and tracks the override.
    /// If newCapacity matches the prototype default, the override is removed.
    /// </summary>
    public bool SetCapacity(Storage entity, int newCapacity)
    {
        newCapacity = Math.Max(MIN_CAPACITY, Math.Min(MAX_CAPACITY, newCapacity));

        if (s_forceCapacityMethod == null)
        {
            Log.Error("StorageCapacityMod: ForceNewCapacityTo method not found.");
            return false;
        }

        try
        {
            var quantity = new Quantity(newCapacity);
            s_forceCapacityMethod.Invoke(entity, new object[] { quantity });

            int entityId = entity.Id.Value;
            int defaultCap = entity.Prototype.Capacity.Value;

            if (newCapacity == defaultCap)
            {
                // Back to default — remove the override.
                m_overrides.Remove(entityId);
            }
            else
            {
                m_overrides[entityId] = newCapacity;
            }

            SaveOverrides();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"StorageCapacityMod: Failed to set capacity: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Re-applies all saved overrides. Call this after game load, once the
    /// game has finished resetting all buffer capacities to prototype defaults.
    /// </summary>
    public void ReapplyAllOverrides()
    {
        LoadOverrides();

        if (m_overrides.Count == 0) return;

        int applied = 0;
        int stale = 0;
        var staleIds = new List<int>();

        foreach (var kvp in m_overrides)
        {
            if (m_entitiesManager.TryGetEntity<Storage>(new EntityId(kvp.Key), out var storage))
            {
                try
                {
                    var quantity = new Quantity(kvp.Value);
                    s_forceCapacityMethod.Invoke(storage, new object[] { quantity });
                    applied++;
                }
                catch (Exception ex)
                {
                    Log.Warning($"StorageCapacityMod: Failed to reapply override for entity {kvp.Key}: {ex.Message}");
                }
            }
            else
            {
                // Entity no longer exists (demolished, etc.) — mark for cleanup.
                staleIds.Add(kvp.Key);
                stale++;
            }
        }

        // Clean up stale entries.
        foreach (int id in staleIds)
        {
            m_overrides.Remove(id);
        }

        if (stale > 0) SaveOverrides();

        Log.Info($"StorageCapacityMod: Reapplied {applied} capacity overrides ({stale} stale entries cleaned).");
    }

    /// <summary>
    /// Removes the override for a given entity (resets to prototype default).
    /// </summary>
    public void RemoveOverride(Storage entity)
    {
        m_overrides.Remove(entity.Id.Value);
        SaveOverrides();
    }

    /// <summary>
    /// Re-apply the saved override (if any) for a storage that was just moved
    /// or upgraded. The game's <c>Storage.TryReplaceSelf</c> calls
    /// <c>ForceNewCapacityTo(Prototype.Capacity)</c> during replace, wiping our
    /// override back to the prototype default. EntityId is preserved across
    /// replace (same entity instance, same id), so we can just look up by id
    /// and shove the saved value back in.
    ///
    /// Called from <see cref="M:Mafi.Core.Entities.IEntitiesManager.OnUpgradeJustPerformed" />
    /// which fires AFTER PerformReplace returns true — i.e. after the reset.
    /// We deliberately do NOT call SaveOverrides() here: the value on disk
    /// hasn't changed, this is purely a re-application.
    /// </summary>
    public void TryReapplyOverrideAfterReplace(Storage entity)
    {
        if (s_forceCapacityMethod == null) return;

        int id = entity.Id.Value;
        if (!m_overrides.TryGetValue(id, out int saved)) return;

        // Defensive clamp — the value on disk could be stale or out of range
        // if MAX_CAPACITY was lowered in a later mod version.
        saved = Math.Max(MIN_CAPACITY, Math.Min(MAX_CAPACITY, saved));

        try
        {
            s_forceCapacityMethod.Invoke(entity, new object[] { new Quantity(saved) });
            Log.Info($"StorageCapacityMod: reapplied capacity {saved} to entity {id} after move/upgrade.");
        }
        catch (Exception ex)
        {
            Log.Warning($"StorageCapacityMod: Failed to reapply override after move/upgrade for entity {id}: {ex.Message}");
        }
    }

    // ── Simple JSON persistence (no external dependencies) ──

    private void SaveOverrides()
    {
        try
        {
            // Write a minimal JSON object: { "123": 5000, "456": 10000 }
            using (var writer = new StreamWriter(m_savePath, false))
            {
                writer.Write("{");
                bool first = true;
                foreach (var kvp in m_overrides)
                {
                    if (!first) writer.Write(",");
                    writer.Write($"\"{kvp.Key}\":{kvp.Value}");
                    first = false;
                }
                writer.Write("}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"StorageCapacityMod: Could not save overrides: {ex.Message}");
        }
    }

    private void LoadOverrides()
    {
        m_overrides.Clear();

        if (!File.Exists(m_savePath)) return;

        try
        {
            string json = File.ReadAllText(m_savePath).Trim();
            if (json.Length < 3) return; // Empty or "{}"

            // Minimal parser for {"key":value,...} where keys and values are integers.
            json = json.Substring(1, json.Length - 2); // Strip outer braces
            if (string.IsNullOrWhiteSpace(json)) return;

            string[] pairs = json.Split(',');
            foreach (string pair in pairs)
            {
                string[] parts = pair.Split(':');
                if (parts.Length != 2) continue;

                string keyStr = parts[0].Trim().Trim('"');
                string valStr = parts[1].Trim();

                if (int.TryParse(keyStr, out int entityId) && int.TryParse(valStr, out int capacity))
                {
                    capacity = Math.Max(MIN_CAPACITY, Math.Min(MAX_CAPACITY, capacity));
                    m_overrides[entityId] = capacity;
                }
            }

            Log.Info($"StorageCapacityMod: Loaded {m_overrides.Count} capacity overrides from disk.");
        }
        catch (Exception ex)
        {
            Log.Warning($"StorageCapacityMod: Could not load overrides: {ex.Message}");
        }
    }
}
