using System.Collections.Generic;
using System.Linq;
using Godot;

public partial class StatusWindow
{
	private AccessoryWardrobe? _presetWardrobe;
	private LineEdit? _presetName;
	private Button? _savePreset, _openPresets, _openDogPresets;
	private Label? _presetSaveStatus;
	private DogPresetsWindow? _presetsWindow;
	private GridContainer? _dogPresetGrid;
	private Label? _emptyDogPresets, _dogPresetStatus;
	private PackedScene? _dogPresetCardScene;
	private readonly List<(DogPreset Preset, Button Apply, Label Availability)> _dogPresetCards = new();

	private void BindPresetControls()
	{
		_presetName = _accessoryEditor?.GetNodeOrNull<LineEdit>("%PresetName");
		_savePreset = _accessoryEditor?.GetNodeOrNull<Button>("%SavePreset");
		_openPresets = _accessoryEditor?.GetNodeOrNull<Button>("%OpenPresets");
		_presetSaveStatus = _accessoryEditor?.GetNodeOrNull<Label>("%PresetSaveStatus");
		_openDogPresets = GetNodeOrNull<Button>("%OpenDogPresets");
		_dogPresetGrid = GetNodeOrNull<GridContainer>("%DogPresetGrid");
		_emptyDogPresets = GetNodeOrNull<Label>("%EmptyDogPresets");
		_dogPresetStatus = GetNodeOrNull<Label>("%DogPresetStatus");
		if (_dogPresetGrid != null)
		{
			_dogPresetGrid.Resized += UpdateDogPresetColumns;
			GetNode<ScrollContainer>("%DogsScroll").Resized += UpdateDogPresetColumns;
		}
		if (_savePreset != null) _savePreset.Pressed += SaveCurrentPreset;
		if (_presetName != null) _presetName.TextSubmitted += _ => SaveCurrentPreset();
		if (_openPresets != null) _openPresets.Pressed += OpenPresetsWindow;
		if (_openDogPresets != null) _openDogPresets.Pressed += OpenPresetsWindow;
		VisibilityChanged += () => { if (!Visible) _presetsWindow?.Hide(); };
		RefreshPresetControls();
		RebuildDogPresetCards();
	}

	private void ConfigurePresetWardrobe(AccessoryWardrobe? wardrobe)
	{
		ReleasePresetWardrobe();
		_presetWardrobe = wardrobe;
		if (wardrobe != null)
		{
			wardrobe.PresetsChanged += RefreshPresetControls;
			wardrobe.PresetsChanged += RebuildDogPresetCards;
			wardrobe.Changed += RefreshPresetControls;
			_presetsWindow?.Configure(wardrobe);
		}
		else _presetsWindow?.Hide();
		RefreshPresetControls();
		RebuildDogPresetCards();
	}

	private void RefreshPresetControls()
	{
		if (_savePreset != null) _savePreset.Disabled = _presetWardrobe == null;
		if (_presetName != null) _presetName.Editable = _presetWardrobe != null;
		var count = _presetWardrobe?.Presets.Count ?? 0;
		if (_openPresets != null)
		{
			_openPresets.Disabled = _presetWardrobe == null;
			_openPresets.Text = count == 0 ? "Presets" : $"Presets · {count}";
		}
		if (_openDogPresets != null) _openDogPresets.Disabled = _presetWardrobe == null;
		RefreshDogPresetAvailability();
	}

	private void RebuildDogPresetCards()
	{
		if (_dogPresetGrid == null) return;
		foreach (var child in _dogPresetGrid.GetChildren()) { _dogPresetGrid.RemoveChild(child); child.QueueFree(); }
		_dogPresetCards.Clear();
		if (_presetWardrobe != null)
		{
			_dogPresetCardScene ??= ResourceLoader.Load<PackedScene>("res://UI/DogPresetCard.tscn");
			foreach (var preset in _presetWardrobe.Presets)
			{
				var card = _dogPresetCardScene.Instantiate<VBoxContainer>();
				card.SetMeta("preset_id", preset.Id);
				_dogPresetGrid.AddChild(card);
				card.GetNode<DogPresetPreview>("%PresetPreview").Configure(_presetWardrobe, preset);
				card.GetNode<Label>("%PresetName").Text = preset.Name;
				var dogName = SteamCosmeticCatalog.Find(preset.DogItemDefId)?.Name ?? "Starter dog";
				card.GetNode<Label>("%PresetDetails").Text = $"{dogName} · {preset.Accessories.Count} {(preset.Accessories.Count == 1 ? "accessory" : "accessories")}";
				var apply = card.GetNode<Button>("%ApplyPreset");
				apply.Pressed += () => ApplySavedPreset(preset.Id);
				card.GetNode<Button>("%DeletePreset").Pressed += () => ReportDogPresetResult(
					_presetWardrobe?.DeletePreset(preset.Id) == true, "Preset deleted.", "Could not delete preset. Please try again.");
				_dogPresetCards.Add((preset, apply, card.GetNode<Label>("%PresetAvailability")));
			}
		}
		if (_emptyDogPresets != null) _emptyDogPresets.Visible = _dogPresetCards.Count == 0;
		RefreshDogPresetAvailability(); UpdateDogPresetColumns();
	}

	private void RefreshDogPresetAvailability()
	{
		foreach (var (preset, apply, availability) in _dogPresetCards)
		{
			var usable = _presetWardrobe?.CanApplyPreset(preset) == true;
			apply.Disabled = !usable;
			apply.TooltipText = usable ? $"Use {preset.Name}." : "Some cosmetics in this preset are not in your current Steam inventory.";
			availability.Text = usable ? "Click preview to use" : "Items unavailable";
		}
	}

	private void UpdateDogPresetColumns()
	{
		if (_dogPresetGrid == null) return;
		var scroll = GetNode<ScrollContainer>("%DogsScroll");
		var width = scroll.Size.X - (scroll.GetVScrollBar().Visible ? scroll.GetVScrollBar().GetCombinedMinimumSize().X : 0);
		var cardWidth = _dogPresetCards.Count == 0 ? 216 : _dogPresetCards.Max(card => card.Apply.GetCombinedMinimumSize().X);
		_dogPresetGrid.Columns = width >= cardWidth * 2 + _dogPresetGrid.GetThemeConstant("h_separation") ? 2 : 1;
	}

	private void ReportDogPresetResult(bool success, string successText, string failureText)
	{
		if (_dogPresetStatus == null) return;
		_dogPresetStatus.Text = success ? successText : failureText;
		_dogPresetStatus.Visible = true;
	}

	private void SaveCurrentPreset()
	{
		if (_presetWardrobe == null) return;
		_accessoryEditor?.FinishPendingEdits();
		var saved = _presetWardrobe.SavePreset(_presetName?.Text ?? string.Empty);
		if (_presetSaveStatus != null)
		{
			_presetSaveStatus.Text = saved ? "Preset saved. Find it at the top of Dogs." : "Could not save preset. Please try again.";
			_presetSaveStatus.Visible = true;
		}
		if (saved && _presetName != null) _presetName.Clear();
	}

	private void OpenPresetsWindow()
	{
		if (_presetWardrobe == null) return;
		_accessoryEditor?.FinishPendingEdits();
		if (_presetsWindow == null)
		{
			_presetsWindow = ResourceLoader.Load<PackedScene>("res://DogPresetsWindow.tscn").Instantiate<DogPresetsWindow>();
			_presetsWindow.Name = "DogPresetsWindow";
			AddChild(_presetsWindow);
			_presetsWindow.BeforeSave += () => _accessoryEditor?.FinishPendingEdits();
			_presetsWindow.ApplyRequested += ApplySavedPreset;
			_presetsWindow.Configure(_presetWardrobe);
		}
		_presetsWindow.ShowPresets(this);
	}

	private void ApplySavedPreset(string id)
	{
		var preset = _presetWardrobe?.Presets.FirstOrDefault(item => item.Id == id);
		if (preset == null || _presetWardrobe?.CanApplyPreset(preset) != true)
		{
			_presetsWindow?.ReportApplyResult(false);
			ReportDogPresetResult(false, string.Empty, "Could not apply preset. Check that its dog and accessories are still owned.");
			return;
		}
		_accessoryEditor?.FinishPendingEdits();
		_editingSession?.ClearSelection();
		var success = _presetWardrobe.ApplyPreset(id);
		_presetsWindow?.ReportApplyResult(success);
		ReportDogPresetResult(success, "Preset applied to your desktop dog.", "Could not apply preset. Check that its dog and accessories are still owned.");
		UpdateInventoryControls();
	}

	private void ReleasePresetWardrobe()
	{
		if (_presetWardrobe == null) return;
		_presetWardrobe.PresetsChanged -= RefreshPresetControls;
		_presetWardrobe.PresetsChanged -= RebuildDogPresetCards;
		_presetWardrobe.Changed -= RefreshPresetControls;
	}
	private void ReleasePresetControls() => ReleasePresetWardrobe();
}
