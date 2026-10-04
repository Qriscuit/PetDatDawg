using System.Linq;
using Godot;

/// <summary>Native controls kept separate from the transparent desktop route canvas.</summary>
public partial class PatrolRouteToolbar : Window
{
	private static readonly Vector2I DefaultSize = new(590, 178);
	private PatrolRoute? _route;
	private Label? _routeSummary, _saveStatus;
	private Button? _undoButton, _clearButton, _doneButton;

	public override void _Ready()
	{
		Visible = false;
		Name = "PatrolRouteToolbar";
		Title = "Pet Da Dog · Patrol route";
		ForceNative = true; World2D = new World2D();
		Transparent = false; TransparentBg = false; Borderless = false;
		AlwaysOnTop = true; Unresizable = true; Exclusive = false; Transient = false;
		Size = DefaultSize;
		Theme = WoodlandTheme.Build();
		CloseRequested += CancelEditing;
		BuildUi(); Refresh();
		if (_route?.IsEditing == true) OnEditingChanged(true);
	}

	public void Configure(PatrolRoute route)
	{
		DisconnectModel();
		_route = route;
		_route.Changed += Refresh;
		_route.EditingChanged += OnEditingChanged;
		if (IsNodeReady()) { Refresh(); OnEditingChanged(_route.IsEditing); }
	}

	public override void _ExitTree() => DisconnectModel();

	public void Reposition(Rect2I usable)
	{
		if (usable.Size.X <= 0 || usable.Size.Y <= 0) return;
		// Reserve room for the native titlebar and keep the route canvas mostly clear.
		var maximum = new Vector2I(Mathf.Max(1, usable.Size.X - 24), Mathf.Max(1, usable.Size.Y - 56));
		Size = DefaultSize.Min(maximum);
		Position = new Vector2I(usable.Position.X + (usable.Size.X - Size.X) / 2,
			Mathf.Min(usable.Position.Y + 40, Mathf.Max(usable.Position.Y, usable.End.Y - Size.Y - 16)));
	}

	public override void _Input(InputEvent @event)
	{
		if (!Visible || _route?.IsEditing != true || @event is not InputEventKey { Pressed: true, Echo: false } key) return;
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

	private void BuildUi()
	{
		var panel = new PanelContainer();
		panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		panel.AddThemeStyleboxOverride("panel", WoodlandTheme.PanelStyle(true)); AddChild(panel);
		var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 7); panel.AddChild(content);
		var header = new HBoxContainer();
		header.AddChild(new Label { Text = "Set your patrol route", ThemeTypeVariation = "WoodlandHeading" });
		content.AddChild(header);
		var instructions = new Label { Text = "Click to add stops • Drag to move • Right-click to remove",
			AutowrapMode = TextServer.AutowrapMode.WordSmart };
		instructions.AddThemeFontSizeOverride("font_size", 13);
		instructions.AddThemeColorOverride("font_color", WoodlandTheme.Muted); content.AddChild(instructions);
		_routeSummary = new Label { Name = "PatrolDraftSummary", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_routeSummary.AddThemeFontSizeOverride("font_size", 13); content.AddChild(_routeSummary);
		var actions = new HBoxContainer(); actions.AddThemeConstantOverride("separation", 8);
		_undoButton = MakeButton("Undo last", "PatrolUndoLast", "Remove the last stop (Backspace).");
		_undoButton.Pressed += () => _route?.UndoLastPoint(); actions.AddChild(_undoButton);
		_clearButton = MakeButton("Clear", "PatrolClearDraft", "Remove all stops from this draft.");
		_clearButton.Pressed += () => _route?.ClearDraft(); actions.AddChild(_clearButton);
		actions.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		var cancel = MakeButton("Cancel", "PatrolCancel", "Discard changes (Escape).");
		cancel.Pressed += CancelEditing; actions.AddChild(cancel);
		_doneButton = MakeButton("Done", "PatrolDone", "Save this route and start patrolling (Enter).");
		WoodlandTheme.StyleButton(_doneButton, "ActionButton");
		_doneButton.Pressed += FinishEditing; actions.AddChild(_doneButton); content.AddChild(actions);
		_saveStatus = new Label { Name = "PatrolToolbarSaveStatus", Visible = false,
			Text = "Last save failed. Done will try saving this route again.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_saveStatus.AddThemeColorOverride("font_color", WoodlandTheme.Accent);
		_saveStatus.AddThemeFontSizeOverride("font_size", 12); content.AddChild(_saveStatus);
	}

	private void Refresh()
	{
		if (_routeSummary == null) return;
		var count = _route?.DraftPoints.Count ?? 0;
		_routeSummary.Text = count < 2 ? $"{count} {(count == 1 ? "stop" : "stops")} · Add at least two different stops."
			: DescribeRoute(count);
		if (count >= 2 && _route?.CanFinish != true) _routeSummary.Text += " · Move a stop to a different position.";
		_undoButton!.Disabled = count == 0;
		_clearButton!.Disabled = count == 0;
		_doneButton!.Disabled = _route?.CanFinish != true;
		_saveStatus!.Visible = _route?.LastSaveSucceeded == false;
	}

	private void OnEditingChanged(bool editing)
	{
		if (!IsNodeReady()) return;
		Refresh();
		if (!editing) { Hide(); return; }
		// The route overlay also becomes topmost during this signal. Show afterward
		// so that its full-screen input surface cannot cover these native controls.
		CallDeferred(nameof(QueueShowForEditing));
	}
	private void QueueShowForEditing()
	{
		if (!IsInsideTree() || _route?.IsEditing != true) return;
		// Queue behind native style repairs scheduled later in EditingChanged.
		CallDeferred(nameof(ShowForEditing));
	}
	private void ShowForEditing()
	{
		if (!IsInsideTree() || _route?.IsEditing != true) return;
		if (Mode != ModeEnum.Windowed) Mode = ModeEnum.Windowed;
		Reposition(DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary));
		Show(); GrabFocus();
	}
	private void FinishEditing()
	{
		if (_route?.IsEditing != true) return;
		_route.FinishEdit(); Refresh();
	}
	private void CancelEditing() => _route?.CancelEdit();
	private void DisconnectModel()
	{
		if (_route == null) return;
		_route.Changed -= Refresh;
		_route.EditingChanged -= OnEditingChanged;
	}
	private static Button MakeButton(string text, string name, string tooltip)
	{
		var button = new Button { Name = name, Text = text, TooltipText = tooltip, CustomMinimumSize = new Vector2(0, 30) };
		WoodlandTheme.StyleButton(button, "SecondaryButton"); return button;
	}
	internal static string DescribeRoute(int count)
	{
		if (count < 2) return count == 1 ? "1 stop · Add another stop to make a route." : "No stops yet.";
		var stops = count <= 10 ? string.Join(" → ", Enumerable.Range(1, count)) : $"1 → 2 → 3 → … → {count}";
		return $"{count} stops · {stops} → 1";
	}
}
