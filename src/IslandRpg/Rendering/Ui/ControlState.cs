using OpenTK.Mathematics;

namespace IslandRpg.Rendering.Ui;

internal enum GameUiPanel
{
    None,
    Skills,
    Inventory
}

internal abstract class ControlState
{
    public Vector4 Bounds { get; internal set; }
    public bool Visible { get; internal set; } = true;
    public bool Enabled { get; set; } = true;
    public bool Hovered { get; internal set; }
    public bool Pressed { get; internal set; }

    public virtual bool HitTest(Vector2 point) =>
        Visible && Enabled &&
        point.X >= Bounds.X && point.X < Bounds.X + Bounds.Z &&
        point.Y >= Bounds.Y && point.Y < Bounds.Y + Bounds.W;

    internal virtual void Activate()
    {
    }
}

internal sealed class PanelControlState : ControlState
{
}

internal sealed class TabControlState(GameUiPanel panel) : ControlState
{
    public GameUiPanel Panel { get; } = panel;
    public bool Selected { get; internal set; }
    public event Action<TabControlState>? Clicked;

    internal override void Activate() => Clicked?.Invoke(this);
}

internal sealed class GameUiControlState
{
    private const float ButtonSize = 38;
    private const float ControlGap = 4;
    internal const int InventoryColumns = 4;
    internal const int InventoryRows = 7;
    internal const float InventorySlotSize = 32;
    internal const float InventoryGridTop = 42;
    internal const float InventoryColumnGap = 6;
    internal const float InventoryRowGap = 4;
    private const float PanelWidth = 172;
    private const float PanelBottomPadding = 8;
    private const float PanelHeight =
        InventoryGridTop +
        InventoryRows * InventorySlotSize +
        (InventoryRows - 1) * InventoryRowGap +
        PanelBottomPadding;
    private readonly ControlState[] _hitOrder;
    private ControlState? _captured;
    private bool _leftWasDown;

    public TabControlState SkillsButton { get; } = new(GameUiPanel.Skills);
    public TabControlState InventoryButton { get; } = new(GameUiPanel.Inventory);
    public PanelControlState Panel { get; } = new();
    public GameUiPanel ActivePanel { get; private set; }

    public GameUiControlState()
    {
        // Last visual control is tested first. Tabs sit above the panel.
        _hitOrder = [InventoryButton, SkillsButton, Panel];
        SkillsButton.Clicked += Toggle;
        InventoryButton.Clicked += Toggle;
    }

    public void Layout(Vector4 viewport)
    {
        InventoryButton.Bounds = new(
            Math.Max(viewport.X, viewport.X + viewport.Z - ButtonSize - 12),
            Math.Max(viewport.Y, viewport.Y + viewport.W - ButtonSize - 12),
            ButtonSize,
            ButtonSize);
        SkillsButton.Bounds = new(
            Math.Max(
                viewport.X,
                InventoryButton.Bounds.X - ButtonSize - ControlGap),
            InventoryButton.Bounds.Y,
            ButtonSize,
            ButtonSize);
        Panel.Bounds = new(
            Math.Max(
                viewport.X,
                viewport.X + viewport.Z - PanelWidth - 12),
            Math.Max(
                viewport.Y,
                InventoryButton.Bounds.Y - ControlGap - PanelHeight),
            PanelWidth,
            PanelHeight);
        Panel.Visible = ActivePanel != GameUiPanel.None;
    }

    public void UpdatePointer(Vector2 pointer, bool leftDown)
    {
        foreach (var control in _hitOrder)
            control.Hovered = control.HitTest(pointer);

        if (leftDown && !_leftWasDown)
        {
            _captured = _hitOrder.FirstOrDefault(control => control.HitTest(pointer));
            if (_captured is not null) _captured.Pressed = true;
        }
        else if (!leftDown && _leftWasDown)
        {
            var released = _captured;
            _captured = null;
            if (released is not null)
            {
                released.Pressed = false;
                if (released.HitTest(pointer)) released.Activate();
            }
        }

        if (_captured is not null)
            _captured.Pressed = leftDown;
        _leftWasDown = leftDown;
    }

    public bool BlocksWorldInput(Vector2 pointer) =>
        _hitOrder.Any(control => control.HitTest(pointer));

    private void Toggle(TabControlState tab)
    {
        ActivePanel = ActivePanel == tab.Panel ? GameUiPanel.None : tab.Panel;
        SkillsButton.Selected = ActivePanel == GameUiPanel.Skills;
        InventoryButton.Selected = ActivePanel == GameUiPanel.Inventory;
        Panel.Visible = ActivePanel != GameUiPanel.None;
    }
}
