using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Entities;
using Mafi.Core.Game;
using Mafi.Core.GameLoop;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Unity.Ui;

namespace StorageCapacityMod;

public sealed class StorageCapacityMod : IMod, IDisposable
{
    public ModManifest Manifest { get; }
    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig { get; set; }
    public ModJsonConfig JsonConfig { get; }

    public StorageCapacityMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
        Log.Info("StorageCapacityMod: constructed");
    }

    public void RegisterPrototypes(ProtoRegistrator registrator)
    {
        Log.Info("StorageCapacityMod: registering prototypes");
    }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded)
    {
    }

    public void EarlyInit(DependencyResolver resolver) { }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
    {
        try
        {
            // ── Step 1: Patch InspectorsManager to use our custom inspector ──
            Type concreteType = BuildConcreteInspectorType();
            Log.Info($"StorageCapacityMod: created dynamic inspector type: {concreteType.FullName}");

            var inspectorsManager = resolver.Resolve<InspectorsManager>();

            FieldInfo dictField = typeof(InspectorsManager).GetField(
                "m_inspectorsImplTypes",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (dictField == null)
            {
                Log.Error("StorageCapacityMod: Could not find m_inspectorsImplTypes field.");
                return;
            }

            object dict = dictField.GetValue(inspectorsManager);
            Type dictType = dict.GetType();

            // Replace the built-in Storage -> StorageInspector entry.
            PropertyInfo indexer = dictType.GetProperty("Item");
            if (indexer != null)
            {
                indexer.SetValue(dict, concreteType, new object[] { typeof(Storage) });
                Log.Info("StorageCapacityMod: patched InspectorsManager via indexer.");
            }
            else
            {
                MethodInfo removeMethod = dictType.GetMethod("Remove", new[] { typeof(Type) });
                MethodInfo addMethod = dictType.GetMethod("Add", new[] { typeof(Type), typeof(Type) });

                if (removeMethod != null && addMethod != null)
                {
                    removeMethod.Invoke(dict, new object[] { typeof(Storage) });
                    addMethod.Invoke(dict, new object[] { typeof(Storage), concreteType });
                    Log.Info("StorageCapacityMod: patched InspectorsManager via Remove+Add.");
                }
                else
                {
                    Log.Error("StorageCapacityMod: Could not find indexer or Remove/Add on Dict.");
                }
            }

            // ── Step 2: Create the override manager (per-save file) ──
            var entitiesManager = resolver.Resolve<IEntitiesManager>();
            var gameNameConfig = resolver.Resolve<GameNameConfig>();
            string safeName = SanitizeFileName(gameNameConfig.GameName);
            string savePath = System.IO.Path.Combine(
                Manifest.RootDirectoryPath, $"capacity_overrides_{safeName}.json");
            var overrideManager = new CapacityOverrideManager(entitiesManager, savePath);
            CapacityOverrideManager.Instance = overrideManager;
            Log.Info($"StorageCapacityMod: override file for save '{gameNameConfig.GameName}': {savePath}");

            // ── Step 3: Re-apply saved capacity overrides on load ──
            // Deferred to InitState so it runs after InstantiateAllAndLock(),
            // ensuring all entities are fully initialized before we touch capacities.
            if (gameWasLoaded)
            {
                var gameLoopEvents = resolver.Resolve<IGameLoopEvents>();
                gameLoopEvents.RegisterInitState(this, () =>
                {
                    Log.Info("StorageCapacityMod: InitState fired, reapplying capacity overrides.");
                    overrideManager.ReapplyAllOverrides();
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"StorageCapacityMod: Failed to initialize: {ex}");
        }
    }

    /// <summary>
    /// Creates a concrete subclass of the abstract CustomStorageInspector at runtime
    /// using IL emit. The dynamic type lives in a runtime assembly that InspectorsManager
    /// never scans, avoiding the duplicate-key crash.
    /// </summary>
    private static Type BuildConcreteInspectorType()
    {
        ConstructorInfo baseCtor = typeof(CustomStorageInspector)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .First();

        Type[] paramTypes = baseCtor.GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();

        var assemblyName = new AssemblyName("StorageCapacityMod.Dynamic");
        AssemblyBuilder assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(
            assemblyName, AssemblyBuilderAccess.Run);
        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");

        TypeBuilder typeBuilder = moduleBuilder.DefineType(
            "StorageCapacityMod.CustomStorageInspector_Runtime",
            TypeAttributes.Public | TypeAttributes.Class,
            typeof(CustomStorageInspector));

        ConstructorBuilder ctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            paramTypes);

        ILGenerator il = ctorBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        for (int i = 0; i < paramTypes.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_S, (byte)(i + 1));
        }
        il.Emit(OpCodes.Call, baseCtor);
        il.Emit(OpCodes.Ret);

        return typeBuilder.CreateType();
    }

    /// <summary>
    /// Replaces characters that aren't safe for filenames.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "default";
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }
    public void Dispose() { }
}
