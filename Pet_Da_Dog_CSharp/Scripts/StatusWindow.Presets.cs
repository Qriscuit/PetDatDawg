using System.Linq;
using Godot;

public partial class StatusWindow
{
	private AccessoryWardrobe? _presetWardrobe;
	private LineEdit? _presetName;
	private Button? _savePreset, _openPresets, _openDogPresets;
	private Label? _presetSaveStatus;
	private DogPresetsWindow? _presetsWindow;

	private void BindPresetControls()
	{
		_presetName = _accessoryEditor?.GetNodeOrNull<LineEdit>("%PresetName");
		_savePreset = _accessoryEditor?.GetNodeOrNull<Button>("%SavePreset");
		_openPresets = _accessoryEditor?.GetNodeOrNull<Button>("%OpenPresets");
		_presetSaveStatus = _accessoryEditor?.GetNodeOrNull<Label>("%PresetSaveStatus");
		_openDogPresets = GetNodeOrNull<Button>("%OpenDogPresets");
		if (_savePreset != null) _savePreset.Pressed += SaveCurrentPreset;
		if (_presetName != null) _presetName.TextSubmitted += _ => SaveCurrentPreset();
		if (_openPresets != null) _openPresets.Pressed += OpenPresetsWindow;
		if (_openDogPresets != null) _openDogPresets.Pressed += OpenPresetsWindow;
		VisibilityChanged += () => { if (!Visible) _presetsWindow?.Hide(); };
		RefreshPresetControls();
	}

	private void ConfigurePresetWardrobe(AccessoryWardrobe? wardrobe)
	{
		ReleasePresetWardrobe();
		_presetWardrobe = wardrobe;
		if (wardrobe != null)
		{
			wardrobe.PresetsChanged += RefreshPresetControls;
			wardrobe.Changed += RefreshPresetControls;
			_presetsWindow?.Configure(wardrobe);
		}
		else _presetsWindow?.Hide();
		RefreshPresetControls();
	}

	private void RefreshPresetControls()
	{
		if (_savePreset != null) _savePreset.Disabled = _presetWardrobe == null;
		if (_presetName != null) _presetName.Editable = _presetWardrobe != null;
		var count = _presetWardrobe?.Presets.Count ?? 0;
		foreach (var button in new[] { _openPresets, _openDogPresets })
		{
			if (button == null) continue;
			button.Disabled = _presetWardrobe == null;
			button.Text = count == 0 ? "Presets" : $"Presets · {count}";
		}
	}

	private void SaveCurrentPreset()
	{
		if (_presetWardrobe == null) return;
		_accessoryEditor?.FinishPendingEdits();
		var saved = _presetWardrobe.SavePreset(_presetName?.Text ?? string.Empty);
		if (_presetSaveStatus != null)
		{
			_presetSaveStatus.Text = saved ? "Preset saved. Open Presets to use it later." : "Could not save preset. Please try again.";
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
			return;
		}
		_accessoryEditor?.FinishPendingEdits();
		_editingSession?.ClearSelection();
		_presetsWindow?.ReportApplyResult(_presetWardrobe.ApplyPreset(id));
		UpdateInventoryControls();
	}

	private void ReleasePresetWardrobe()
	{
		if (_presetWardrobe == null) return;
		_presetWardrobe.PresetsChanged -= RefreshPresetControls;
		_presetWardrobe.Changed -= RefreshPresetControls;
	}
	private void ReleasePresetControls() => ReleasePresetWardrobe();
}
