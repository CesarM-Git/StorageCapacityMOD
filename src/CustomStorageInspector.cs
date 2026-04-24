using System;
using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Syncers;
using Mafi.Core.Trains;
using Mafi.Core.Vehicles;
using Mafi.Core.Vehicles.Trucks;
using Mafi.Localization;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui;
using Mafi.Unity.Ui.Inspectors;
using Mafi.Unity.Ui.Vehicles;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;
using Mafi.Unity.UiToolkit.Library;

namespace StorageCapacityMod;

/// <summary>
/// Custom storage inspector that extends the default storage inspector with
/// capacity override controls. Allows the player to dynamically change
/// the capacity of any placed storage building (unit, fluid, or loose).
///
/// Intentionally abstract: InspectorsManager's assembly scan skips abstract classes,
/// preventing a duplicate-key crash with the built-in StorageInspector.
/// A concrete subclass is generated at runtime via IL emit in StorageCapacityMod.Initialize().
/// </summary>
public abstract class CustomStorageInspector : BaseStorageInspector<Storage>
{
    private Label m_currentCapLabel;
    private Label m_defaultCapLabel;
    private TextField m_capacityInput;

    protected CustomStorageInspector(
        UiContext context,
        VehicleBuffersRegistry vehicleBuffersRegistry,
        AssignedBuildingsHighlighter highlighter,
        BuildingsAssigner buildingsAssigner,
        TruckJobsFilterManager trucksJobFilter,
        VehiclesManagementWindow.Controller vehiclesMgmtController,
        TrainStationManager trainStationManager,
        NewInstanceOf<AssignedBuildingsHighlighter> trainStationHighlighter
    ) : base(
        context,
        vehicleBuffersRegistry,
        highlighter,
        buildingsAssigner,
        trucksJobFilter,
        vehiclesMgmtController,
        trainStationManager,
        trainStationHighlighter)
    {
        BuildCapacityOverridePanel();
    }

    private void BuildCapacityOverridePanel()
    {
        // ── Header row ──
        AddPanelRow(
            row => row.JustifyItemsSpaceBetween(),
            new Label("Capacity Override".AsLoc())
                .TextAlign(TextAlignment.LeftMiddle)
                .FontStyle(FontStyle.Bold)
                .FlexGrow(1f)
        );

        // ── Info row: current and default capacity ──
        AddPanelRow(
            row => row.JustifyItemsSpaceBetween(),
            m_currentCapLabel = new Label("Current: --".AsLoc())
                .TextAlign(TextAlignment.LeftMiddle)
                .FlexGrow(0.5f),
            m_defaultCapLabel = new Label("Default: --".AsLoc())
                .TextAlign(TextAlignment.RightMiddle)
                .FlexGrow(0.5f)
        );

        // ── Custom capacity input ──
        AddPanelRow(
            row => row.JustifyItemsSpaceBetween(),
            m_capacityInput = new TextField()
                .NumericOnly()
                .CharLimit(9)
                .Placeholder("Enter capacity".AsLoc())
                .OnEditEnd(_ => ApplyCapacityFromInput()),
            CreateCapacityButton("Set", () => ApplyCapacityFromInput())
        );

        // ── Quick adjustment buttons ──
        AddPanelRow(
            row => row.JustifyItemsSpaceBetween(),
            CreateCapacityButton("Half", () => ApplyCapacityMultiplier(0.5)),
            CreateCapacityButton("-500", () => ApplyCapacityDelta(-500)),
            CreateCapacityButton("-100", () => ApplyCapacityDelta(-100)),
            CreateCapacityButton("+100", () => ApplyCapacityDelta(100)),
            CreateCapacityButton("+500", () => ApplyCapacityDelta(500)),
            CreateCapacityButton("x2", () => ApplyCapacityMultiplier(2.0))
        );

        // ── Larger adjustments and reset ──
        AddPanelRow(
            row => row.JustifyItemsSpaceBetween(),
            CreateCapacityButton("-5000", () => ApplyCapacityDelta(-5000)),
            CreateCapacityButton("-1000", () => ApplyCapacityDelta(-1000)),
            CreateCapacityButton("+1000", () => ApplyCapacityDelta(1000)),
            CreateCapacityButton("+5000", () => ApplyCapacityDelta(5000)),
            CreateCapacityButton("Reset", () => ResetToDefaultCapacity())
        );

        // ── Observe capacity changes to keep labels updated ──
        this.Observe(() => Entity.Capacity)
            .Do(cap =>
            {
                m_currentCapLabel.Value($"Current: {cap.Value}".AsLoc());
            });

        this.Observe(() => Entity.Prototype.Capacity)
            .Do(defaultCap =>
            {
                m_defaultCapLabel.Value($"Default: {defaultCap.Value}".AsLoc());
            });
    }

    private ButtonText CreateCapacityButton(string label, Action onClick)
    {
        return new ButtonText(label.AsLoc())
            .TextAlign(TextAlignment.CenterMiddle)
            .OnClick(onClick)
            .FlexGrow(1f);
    }

    private void ApplyCapacityFromInput()
    {
        if (Entity == null) return;
        string text = m_capacityInput.GetText();
        if (int.TryParse(text, out int newCap))
        {
            CapacityOverrideManager.Instance.SetCapacity(Entity, newCap);
        }
        else
        {
            Log.Warning($"StorageCapacityMod: Invalid capacity input: '{text}'");
        }
    }

    private void ApplyCapacityDelta(int delta)
    {
        if (Entity == null) return;
        int newCap = Entity.Capacity.Value + delta;
        CapacityOverrideManager.Instance.SetCapacity(Entity, newCap);
    }

    private void ApplyCapacityMultiplier(double multiplier)
    {
        if (Entity == null) return;
        int newCap = (int)(Entity.Capacity.Value * multiplier);
        CapacityOverrideManager.Instance.SetCapacity(Entity, newCap);
    }

    private void ResetToDefaultCapacity()
    {
        if (Entity == null) return;
        CapacityOverrideManager.Instance.SetCapacity(Entity, Entity.Prototype.Capacity.Value);
    }
}
