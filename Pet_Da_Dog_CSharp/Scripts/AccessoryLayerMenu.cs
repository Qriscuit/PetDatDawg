using System;
using Godot;

/// <summary>A native context menu for the outfit's bottom-to-top layer order.</summary>
[Tool]
public partial class AccessoryLayerMenu : PopupMenu
{
	private const int MoveUp = 1, MoveDown = 2;
	[ExportGroup("Target caption")]
	[Export] public string DogCaption { get; set; } = "Dog";
	[Export] public string AccessoryCaption { get; set; } = "Accessory";
	[Export] public string CaptionFormat { get; set; } = "{name}";
	[ExportGroup("Popup layout")]
	[Export] public bool FitToContents { get; set; } = true;
	private Vector2I _authoredSize;
	private AccessoryWardrobe? _wardrobe;
	private AccessoryEditingSession? _session;
	private string? _targetId;
	public event Action<bool>? OutfitSaved;

	public override void _Ready()
	{
		if (Engine.IsEditorHint()) return;
		_authoredSize = Size;
		// PopupMenu recomputes its size when it opens; preserve the scene's authored floor.
		MinSize = MinSize.Max(_authoredSize);
		Visible = false;
		World2D = new World2D();
		// Keep the target until our handler captures it, then close before changing the model.
		HideOnItemSelection = false;
		IdPressed += OnIdPressed;
		PopupHide += OnPopupHide;
		CloseRequested += HideMenu;
	}

	public void Configure(AccessoryWardrobe wardrobe, AccessoryEditingSession session)
	{
		if (Engine.IsEditorHint()) return;
		DisconnectModel();
		HideMenu();
		_wardrobe = wardrobe; _session = session;
		_wardrobe.Changed += RefreshMenu;
		_session.StateChanged += RefreshMenu;
		_session.ActiveChanged += OnActiveChanged;
	}

	public override void _ExitTree()
	{
		if (!Engine.IsEditorHint()) DisconnectModel();
	}

	public void ShowFor(string id, Vector2I screenPosition)
	{
		if (Engine.IsEditorHint()) return;
		HideMenu();
		if (_session?.Active != true || _wardrobe == null || _wardrobe.GetLayerIndex(id) < 0) return;
		PrepareTarget(id);
		if (_session.Active != true || _wardrobe.GetLayerIndex(id) < 0) return;
		_targetId = id;
		var caption = id == AccessoryWardrobe.DogLayerId ? DogCaption : _wardrobe.Find(id)?.Name ?? AccessoryCaption;
		if (ItemCount > 0) SetItemText(0, CaptionFormat.Replace("{name}", caption));
		RefreshMenu();
		if (FitToContents)
		{
			ResetSize();
			Size = Size.Max(_authoredSize);
		}
		else Size = _authoredSize.Max(MinSize);
		Popup(new Rect2I(screenPosition, Size));
	}

	public void HideMenu()
	{
		if (Engine.IsEditorHint()) return;
		_targetId = null;
		Hide();
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (!Engine.IsEditorHint() && @event is InputEventKey { Pressed: true, Keycode: Key.Escape })
		{
			HideMenu(); SetInputAsHandled();
		}
	}

	private void PrepareTarget(string id)
	{
		_session!.CompleteGesture();
		// These session methods also flush the inspector's pending color/text edits.
		if (id == AccessoryWardrobe.DogLayerId) _session.ClearSelection();
		else _session.SelectAccessory(id);
	}

	private void OnIdPressed(long action)
	{
		var id = _targetId;
		HideMenu();
		if (Engine.IsEditorHint() || id == null || _session?.Active != true || _wardrobe == null || action is not (MoveUp or MoveDown)) return;
		PrepareTarget(id);
		if (_wardrobe.MoveLayer(id, action == MoveUp ? 1 : -1)) OutfitSaved?.Invoke(_wardrobe.Save());
	}

	private void RefreshMenu()
	{
		if (Engine.IsEditorHint() || _targetId == null) return;
		var selected = _targetId == AccessoryWardrobe.DogLayerId ? _session?.SelectedId == null : _session?.SelectedId == _targetId;
		if (_session?.Active != true || _wardrobe == null || !selected || _wardrobe.GetLayerIndex(_targetId) < 0)
		{
			HideMenu(); return;
		}
		var upIndex = GetItemIndex(MoveUp);
		var downIndex = GetItemIndex(MoveDown);
		if (upIndex >= 0) SetItemDisabled(upIndex, !_wardrobe.CanMoveLayer(_targetId, 1));
		if (downIndex >= 0) SetItemDisabled(downIndex, !_wardrobe.CanMoveLayer(_targetId, -1));
	}

	private void OnActiveChanged(bool active) { if (!active) HideMenu(); }
	private void OnPopupHide() => _targetId = null;
	private void DisconnectModel()
	{
		if (_wardrobe != null) _wardrobe.Changed -= RefreshMenu;
		if (_session == null) return;
		_session.StateChanged -= RefreshMenu;
		_session.ActiveChanged -= OnActiveChanged;
	}
}
