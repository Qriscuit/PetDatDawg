using System.Linq;
using Godot;

/// <summary>Native controls kept separate from the transparent desktop route canvas.</summary>
[Tool]
public partial class PatrolRouteToolbar : Window
{
	[ExportGroup("Desktop placement")]
	[Export] public Vector2I ScreenMargins { get; set; } = new(24, 56);
	[Export] public int TopOffset { get; set; } = 40;
	[Export] public int BottomMargin { get; set; } = 16;
	[ExportGroup("Dynamic text")]
	[Export(PropertyHint.MultilineText)] public string EmptyDraftText { get; set; } = "0 stops · Add at least two different stops.";
	[Export(PropertyHint.MultilineText)] public string OneStopDraftText { get; set; } = "1 stop · Add at least two different stops.";
	[Export(PropertyHint.MultilineText)] public string LoopDraftFormat { get; set; } = "{count} stops · {loop}";
	[Export(PropertyHint.MultilineText)] public string InvalidRouteHint { get; set; } = " · Move a stop to a different position.";
	private Vector2I _authoredSize;
	private PatrolRoute? _route;
	private Label? _routeSummary, _saveStatus;
	private Button? _undoButton, _clearButton, _doneButton;

	public override void _Ready()
	{
		if (Engine.IsEditorHint()) return;
		_authoredSize = Size;
		Visible = false;
		World2D = new World2D();
		_routeSummary = GetNode<Label>("%PatrolDraftSummary");
		_saveStatus = GetNode<Label>("%PatrolToolbarSaveStatus");
		_undoButton = GetNode<Button>("%PatrolUndoLast");
		_clearButton = GetNode<Button>("%PatrolClearDraft");
		_doneButton = GetNode<Button>("%PatrolDone");
		_undoButton.Pressed += () => _route?.UndoLastPoint();
		_clearButton.Pressed += () => _route?.ClearDraft();
		GetNode<Button>("%PatrolCancel").Pressed += CancelEditing;
		_doneButton.Pressed += FinishEditing;
		CloseRequested += CancelEditing;
		Refresh();
		if (_route?.IsEditing == true) OnEditingChanged(true);
	}

	public void Configure(PatrolRoute route)
	{
		if (Engine.IsEditorHint()) return;
		DisconnectModel();
		_route = route;
		_route.Changed += Refresh;
		_route.EditingChanged += OnEditingChanged;
		if (IsNodeReady()) { Refresh(); OnEditingChanged(_route.IsEditing); }
	}

	public override void _ExitTree()
	{
		if (!Engine.IsEditorHint()) DisconnectModel();
	}

	public void Reposition(Rect2I usable)
	{
		if (Engine.IsEditorHint() || usable.Size.X <= 0 || usable.Size.Y <= 0) return;
		// Reserve room for the native titlebar and keep the route canvas mostly clear.
		var maximum = new Vector2I(Mathf.Max(1, usable.Size.X - Mathf.Max(0, ScreenMargins.X)),
			Mathf.Max(1, usable.Size.Y - Mathf.Max(0, ScreenMargins.Y)));
		var desired = _authoredSize == Vector2I.Zero ? Size : _authoredSize;
		Size = desired.Min(maximum);
		Position = new Vector2I(usable.Position.X + (usable.Size.X - Size.X) / 2,
			Mathf.Min(usable.Position.Y + Mathf.Max(0, TopOffset),
				Mathf.Max(usable.Position.Y, usable.End.Y - Size.Y - Mathf.Max(0, BottomMargin))));
	}

	public override void _Input(InputEvent @event)
	{
		if (Engine.IsEditorHint() || !Visible || _route?.IsEditing != true || @event is not InputEventKey { Pressed: true, Echo: false } key) return;
		switch (key.Keycode)
		{
			case Key.Escape: CancelEditing(); break;
			case Key.Enter:
			case Key.KpEnter: FinishEditing(); break;
			case Key.Backspace: _route.UndoLastPoint(); break;
			default: return;
		}
		SetInputAsHandled();
	}

	private void Refresh()
	{
		if (Engine.IsEditorHint() || _routeSummary == null) return;
		var count = _route?.DraftPoints.Count ?? 0;
		var stops = count <= 10 ? string.Join(" → ", Enumerable.Range(1, count)) : $"1 → 2 → 3 → … → {count}";
		_routeSummary.Text = count == 0 ? EmptyDraftText : count == 1 ? OneStopDraftText
			: LoopDraftFormat.Replace("{count}", count.ToString()).Replace("{loop}", stops + " → 1");
		if (count >= 2 && _route?.CanFinish != true) _routeSummary.Text += InvalidRouteHint;
		_undoButton!.Disabled = count == 0;
		_clearButton!.Disabled = count == 0;
		_doneButton!.Disabled = _route?.CanFinish != true;
		_saveStatus!.Visible = _route?.LastSaveSucceeded == false;
	}

	private void OnEditingChanged(bool editing)
	{
		if (Engine.IsEditorHint() || !IsNodeReady()) return;
		Refresh();
		if (!editing) { Hide(); return; }
		// The route overlay also becomes topmost during this signal. Show afterward
		// so that its full-screen input surface cannot cover these native controls.
		CallDeferred(nameof(QueueShowForEditing));
	}
	private void QueueShowForEditing()
	{
		if (Engine.IsEditorHint() || !IsInsideTree() || _route?.IsEditing != true) return;
		// Queue behind native style repairs scheduled later in EditingChanged.
		CallDeferred(nameof(ShowForEditing));
	}
	private void ShowForEditing()
	{
		if (Engine.IsEditorHint() || !IsInsideTree() || _route?.IsEditing != true) return;
		if (Mode != ModeEnum.Windowed) Mode = ModeEnum.Windowed;
		Reposition(DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary));
		Show(); GrabFocus();
	}
	private void FinishEditing()
	{
		if (Engine.IsEditorHint() || _route?.IsEditing != true) return;
		_route.FinishEdit(); Refresh();
	}
	private void CancelEditing()
	{
		if (!Engine.IsEditorHint()) _route?.CancelEdit();
	}
	private void DisconnectModel()
	{
		if (_route == null) return;
		_route.Changed -= Refresh;
		_route.EditingChanged -= OnEditingChanged;
	}
	internal static string DescribeRoute(int count)
	{
		if (count < 2) return count == 1 ? "1 stop · Add another stop to make a route." : "No stops yet.";
		var stops = count <= 10 ? string.Join(" → ", Enumerable.Range(1, count)) : $"1 → 2 → 3 → … → {count}";
		return $"{count} stops · {stops} → 1";
	}
}
