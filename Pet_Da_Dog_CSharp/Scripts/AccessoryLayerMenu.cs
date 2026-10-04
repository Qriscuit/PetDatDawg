using System;
using Godot;

/// <summary>A native context menu for the outfit's bottom-to-top layer order.</summary>
public partial class AccessoryLayerMenu : PopupMenu
{
	private const int MoveUp = 1, MoveDown = 2;
	private AccessoryWardrobe? _wardrobe;
	private AccessoryEditingSession? _session;
	private string? _targetId;
	public event Action<bool>? OutfitSaved;

	public override void _Ready()
	{
		Name = "AccessoryLayerMenu";
		ForceNative = true;
		PreferNativeMenu = false;
		World2D = new World2D();
		Transparent = false;
		TransparentBg = false;
		Theme = WoodlandTheme.Build();
		Theme.SetColor("font_disabled_color", "PopupMenu", WoodlandTheme.Muted);
		Theme.SetColor("font_separator_color", "PopupMenu", WoodlandTheme.Muted);
		Theme.SetConstant("v_separation", "PopupMenu", 10);
		// Keep the target until our handler captures it, then close before changing the model.
		HideOnItemSelection = false;
		IdPressed += OnIdPressed;
		PopupHide += OnPopupHide;
		CloseRequested += HideMenu;
	}

	public void Configure(AccessoryWardrobe wardrobe, AccessoryEditingSession session)
	{
		DisconnectModel();
		HideMenu();
		_wardrobe = wardrobe; _session = session;
		_wardrobe.Changed += RefreshMenu;
		_session.StateChanged += RefreshMenu;
		_session.ActiveChanged += OnActiveChanged;
	}

	public override void _ExitTree() => DisconnectModel();

	public void ShowFor(string id, Vector2I screenPosition)
	{
		HideMenu();
		if (_session?.Active != true || _wardrobe == null || _wardrobe.GetLayerIndex(id) < 0) return;
		PrepareTarget(id);
		if (_session.Active != true || _wardrobe.GetLayerIndex(id) < 0) return;
		_targetId = id;
		Clear();
		AddSeparator(id == AccessoryWardrobe.DogLayerId ? "Dog" : _wardrobe.Find(id)?.Name ?? "Accessory");
		AddItem("Move up one layer", MoveUp);
		AddItem("Move down one layer", MoveDown);
		RefreshMenu();
		ResetSize();
		Popup(new Rect2I(screenPosition, Size));
	}

	public void HideMenu()
	{
		_targetId = null;
		Hide();
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
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
		if (id == null || _session?.Active != true || _wardrobe == null || action is not (MoveUp or MoveDown)) return;
		PrepareTarget(id);
		if (_wardrobe.MoveLayer(id, action == MoveUp ? 1 : -1)) OutfitSaved?.Invoke(_wardrobe.Save());
	}

	private void RefreshMenu()
	{
		if (_targetId == null) return;
		var selected = _targetId == AccessoryWardrobe.DogLayerId ? _session?.SelectedId == null : _session?.SelectedId == _targetId;
		if (_session?.Active != true || _wardrobe == null || !selected || _wardrobe.GetLayerIndex(_targetId) < 0)
		{
			HideMenu(); return;
		}
		SetItemDisabled(GetItemIndex(MoveUp), !_wardrobe.CanMoveLayer(_targetId, 1));
		SetItemDisabled(GetItemIndex(MoveDown), !_wardrobe.CanMoveLayer(_targetId, -1));
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
