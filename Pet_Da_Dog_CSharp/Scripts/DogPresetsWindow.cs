using System;
using System.Collections.Generic;
using Godot;

public partial class DogPresetsWindow : Window
{
	private AccessoryWardrobe? _wardrobe;
	private GridContainer _grid = null!;
	private LineEdit _name = null!;
	private Button _save = null!;
	private Label _empty = null!, _status = null!;
	private PackedScene _cardScene = null!;
	private readonly List<(DogPreset Preset, Button Apply, Label Availability)> _cards = new();
	private bool _positioned;
	private float _uiScale = 1;
	public event Action? BeforeSave;
	public event Action<string>? ApplyRequested;

	public override void _Ready()
	{
		Visible = false;
		_grid = GetNode<GridContainer>("%PresetGrid");
		_name = GetNode<LineEdit>("%PresetName");
		_save = GetNode<Button>("%SavePreset");
		_empty = GetNode<Label>("%EmptyPresets");
		_status = GetNode<Label>("%PresetStatus");
		_cardScene = ResourceLoader.Load<PackedScene>("res://UI/DogPresetCard.tscn");
		_save.Pressed += SaveCurrent;
		_name.TextSubmitted += _ => SaveCurrent();
		CloseRequested += Hide;
		_grid.Resized += UpdateColumns;
		RebuildCards();
	}

	public void Configure(AccessoryWardrobe wardrobe)
	{
		DetachWardrobe();
		_wardrobe = wardrobe;
		wardrobe.PresetsChanged += RebuildCards;
		wardrobe.Changed += RefreshAvailability;
		if (IsNodeReady()) RebuildCards();
	}

	public void ShowPresets(Window owner)
	{
		var scale = owner.ContentScaleFactor;
		if (!Mathf.IsEqualApprox(scale, _uiScale))
		{
			Size = new Vector2I(Mathf.RoundToInt(Size.X / _uiScale * scale), Mathf.RoundToInt(Size.Y / _uiScale * scale));
			MinSize = new Vector2I(Mathf.RoundToInt(520 * scale), Mathf.RoundToInt(400 * scale));
			_uiScale = scale;
		}
		ContentScaleFactor = scale;
		if (Mode == ModeEnum.Minimized) Mode = ModeEnum.Windowed;
		if (DisplayServer.GetName() != "headless")
		{
			var usable = DisplayServer.ScreenGetUsableRect(owner.CurrentScreen);
			if (!_positioned) Position = owner.Position + new Vector2I(36, 36);
			Position = new Vector2I(Mathf.Clamp(Position.X, usable.Position.X, Math.Max(usable.Position.X, usable.End.X - Size.X)),
				Mathf.Clamp(Position.Y, usable.Position.Y, Math.Max(usable.Position.Y, usable.End.Y - Size.Y)));
		}
		_positioned = true;
		Show(); GrabFocus();
	}

	private void SaveCurrent()
	{
		if (_wardrobe == null) return;
		BeforeSave?.Invoke();
		var saved = _wardrobe.SavePreset(_name.Text);
		_status.Text = saved ? "Preset saved." : "Could not save preset. Please try again.";
		if (saved) _name.Clear();
	}

	private void RebuildCards()
	{
		if (!IsNodeReady()) return;
		foreach (var child in _grid.GetChildren()) { _grid.RemoveChild(child); child.QueueFree(); }
		_cards.Clear();
		if (_wardrobe != null)
		{
			foreach (var preset in _wardrobe.Presets)
			{
				var card = _cardScene.Instantiate<VBoxContainer>();
				card.SetMeta("preset_id", preset.Id);
				_grid.AddChild(card);
				card.GetNode<DogPresetPreview>("%PresetPreview").Configure(_wardrobe, preset);
				card.GetNode<Label>("%PresetName").Text = preset.Name;
				var dogName = SteamCosmeticCatalog.Find(preset.DogItemDefId)?.Name ?? "Starter dog";
				card.GetNode<Label>("%PresetDetails").Text = $"{dogName} · {preset.Accessories.Count} {(preset.Accessories.Count == 1 ? "accessory" : "accessories")}";
				var apply = card.GetNode<Button>("%ApplyPreset");
				apply.Pressed += () => RequestApply(preset);
				card.GetNode<Button>("%DeletePreset").Pressed += () =>
				{
					_status.Text = _wardrobe.DeletePreset(preset.Id) ? "Preset deleted." : "Could not delete preset. Please try again.";
				};
				_cards.Add((preset, apply, card.GetNode<Label>("%PresetAvailability")));
			}
		}
		_empty.Visible = _cards.Count == 0;
		RefreshAvailability(); UpdateColumns();
	}

	private void RefreshAvailability()
	{
		if (!IsNodeReady()) return;
		_save.Disabled = _wardrobe == null;
		_name.Editable = _wardrobe != null;
		foreach (var (preset, apply, availability) in _cards)
		{
			var usable = _wardrobe?.CanApplyPreset(preset) == true;
			apply.Disabled = !usable;
			apply.TooltipText = usable ? $"Use {preset.Name}." : "Some cosmetics in this preset are not in your current Steam inventory.";
			availability.Text = usable ? "Click preview to use" : "Items unavailable";
		}
	}

	private void RequestApply(DogPreset preset)
	{
		if (_wardrobe?.CanApplyPreset(preset) != true) { ReportApplyResult(false); return; }
		if (ApplyRequested != null) ApplyRequested.Invoke(preset.Id);
		else ReportApplyResult(_wardrobe.ApplyPreset(preset.Id));
	}
	public void ReportApplyResult(bool success) => _status.Text = success ? "Preset applied to your desktop dog." : "Could not apply preset. Check that its dog and accessories are still owned.";
	private void UpdateColumns() => _grid.Columns = _grid.Size.X < 490 ? 1 : 2;
	private void DetachWardrobe()
	{
		if (_wardrobe == null) return;
		_wardrobe.PresetsChanged -= RebuildCards;
		_wardrobe.Changed -= RefreshAvailability;
	}
	public override void _ExitTree() => DetachWardrobe();
}
