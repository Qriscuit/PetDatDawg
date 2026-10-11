using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public sealed record DogPreset(string Id, string Name, int DogItemDefId,
	IReadOnlyList<AccessoryPlacement> Accessories, IReadOnlyList<string> LayerOrder);

public partial class AccessoryWardrobe
{
	private const int PresetFormat = 1;
	private const int MaxPresets = 128;
	private const int MaxPresetNameLength = 64;
	private const string PresetSectionPrefix = "preset:";
	private readonly List<DogPreset> _presets = new();

	[Export] public string PresetStoragePath { get; set; } = "user://dog-presets.cfg";
	public IReadOnlyList<DogPreset> Presets => _presets.AsReadOnly();
	public event Action? PresetsChanged;

	public bool SavePreset(string name)
	{
		if (_presets.Count >= MaxPresets) return false;
		var accessories = Equipped.ToArray();
		var visibleLayers = new HashSet<string>(accessories.Select(item => item.Id), StringComparer.Ordinal) { DogLayerId };
		var layers = _layerOrder.Where(visibleLayers.Contains).ToArray();
		var preset = FreezePreset(new DogPreset(Guid.NewGuid().ToString("N"), PresetName(name), SelectedDogItemDefId,
			accessories, layers));
		if (!CanApplyPreset(preset)) return false;
		_presets.Add(preset);
		if (!SavePresets()) { _presets.RemoveAt(_presets.Count - 1); return false; }
		PresetsChanged?.Invoke();
		return true;
	}

	public bool CanApplyPreset(DogPreset preset) => IsValidPreset(preset)
		&& (preset.DogItemDefId == 0 || OwnsSteamItem(preset.DogItemDefId))
		&& preset.Accessories.All(item => CanEquip(item.Id));

	public bool ApplyPreset(string id)
	{
		var preset = _presets.Find(item => item.Id == id);
		// Validate every asset and its current ownership before changing outfit, edit or undo state.
		if (preset == null || !CanApplyPreset(preset)) return false;
		var colors = new Dictionary<string, Color>();
		var scales = new Dictionary<string, float>();
		var rotations = new Dictionary<string, float>();
		foreach (var accessory in preset.Accessories)
		{
			if (!accessory.Tint.IsEqualApprox(Colors.White)) colors[accessory.Id] = accessory.Tint;
			if (!Mathf.IsEqualApprox(accessory.Scale, 1)) scales[accessory.Id] = accessory.Scale;
			if (!Mathf.IsZeroApprox(accessory.RotationDegrees)) rotations[accessory.Id] = accessory.RotationDegrees;
		}
		var text = preset.Accessories.FirstOrDefault(item => item.Id == TextAccessoryId);
		var state = new OutfitState(preset.Accessories.ToArray(), preset.LayerOrder.ToArray(), colors, scales, rotations,
			text?.Text ?? DefaultText, text?.BackgroundVisible ?? true, preset.DogItemDefId);
		CommitEdit();
		if (MatchesState(state)) return Save();
		var previous = CaptureState();
		var previousHistory = _history.ToArray();
		PushHistory(previous, "apply preset");
		RestoreState(state);
		if (Save()) return true;
		// A failed local save must not leave a partially applied preset or a spurious undo entry.
		_history.Clear();
		_history.AddRange(previousHistory);
		RestoreState(previous);
		return false;
	}

	public bool DeletePreset(string id)
	{
		var index = _presets.FindIndex(item => item.Id == id);
		if (index < 0) return false;
		var preset = _presets[index];
		_presets.RemoveAt(index);
		if (!SavePresets()) { _presets.Insert(index, preset); return false; }
		PresetsChanged?.Invoke();
		return true;
	}

	private string PresetName(string name)
	{
		name = new string((name ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
		if (name.Length == 0)
			name = $"{SteamCosmeticCatalog.Find(SelectedDogItemDefId)?.Name ?? "Starter dog"} outfit {_presets.Count + 1}";
		var characters = StringInfo.ParseCombiningCharacters(name);
		return characters.Length > MaxPresetNameLength ? name[..characters[MaxPresetNameLength]] : name;
	}

	private static DogPreset FreezePreset(DogPreset preset) => preset with
	{
		Accessories = Array.AsReadOnly(preset.Accessories.ToArray()),
		LayerOrder = Array.AsReadOnly(preset.LayerOrder.ToArray())
	};

	private bool IsValidPreset(DogPreset preset)
	{
		if (preset == null || !Guid.TryParseExact(preset.Id, "N", out _) || string.IsNullOrWhiteSpace(preset.Name)
			|| preset.Name.Any(char.IsControl) || StringInfo.ParseCombiningCharacters(preset.Name).Length > MaxPresetNameLength
			|| preset.DogItemDefId < 0 || (preset.DogItemDefId != 0 && SteamCosmeticCatalog.Find(preset.DogItemDefId)?.Kind != "dog")
			|| preset.Accessories == null || preset.LayerOrder == null || preset.Accessories.Count > _catalog.Count) return false;
		var requiredLayers = new HashSet<string>(StringComparer.Ordinal) { DogLayerId };
		foreach (var placement in preset.Accessories)
		{
			if (placement == null || string.IsNullOrEmpty(placement.Id) || Find(placement.Id) is not { } definition
				|| !requiredLayers.Add(placement.Id) || !placement.Position.IsFinite()
				|| placement.Position != ClampPosition(placement.Id, placement.Position)
				|| !float.IsFinite(placement.Scale) || placement.Scale < MinScale || placement.Scale > MaxScale
				|| !float.IsFinite(placement.RotationDegrees) || placement.RotationDegrees < MinRotation || placement.RotationDegrees > MaxRotation
				|| !ValidPresetTint(placement.Tint) || (!definition.CanRecolor && !placement.Tint.IsEqualApprox(Colors.White))
				|| placement.Text == null || placement.Text.Any(char.IsControl)
				|| StringInfo.ParseCombiningCharacters(placement.Text).Length > MaxTextLength
				|| (!definition.IsText && (placement.Text.Length != 0 || !placement.BackgroundVisible))) return false;
		}
		return preset.LayerOrder.Count == requiredLayers.Count
			&& preset.LayerOrder.All(layer => layer != null && requiredLayers.Remove(layer)) && requiredLayers.Count == 0;
	}

	private static bool ValidPresetTint(Color tint) => float.IsFinite(tint.R) && float.IsFinite(tint.G)
		&& float.IsFinite(tint.B) && float.IsFinite(tint.A) && tint.R >= 0 && tint.R <= 1
		&& tint.G >= 0 && tint.G <= 1 && tint.B >= 0 && tint.B <= 1 && tint.A == 1;

	private bool SavePresets()
	{
		var config = new ConfigFile();
		config.SetValue("meta", "format", PresetFormat);
		foreach (var preset in _presets)
		{
			var section = PresetSectionPrefix + preset.Id;
			config.SetValue(section, "name", preset.Name);
			config.SetValue(section, "dog_itemdef", preset.DogItemDefId);
			var accessories = new Godot.Collections.Array<Godot.Collections.Dictionary>();
			foreach (var item in preset.Accessories)
				accessories.Add(new Godot.Collections.Dictionary
				{
					["id"] = item.Id, ["position"] = item.Position, ["tint"] = item.Tint,
					["scale"] = item.Scale, ["rotation"] = item.RotationDegrees,
					["text"] = item.Text, ["background"] = item.BackgroundVisible
				});
			config.SetValue(section, "accessories", accessories);
			config.SetValue(section, "layer_order", preset.LayerOrder.ToArray());
		}
		var error = config.Save(PresetStoragePath);
		if (error == Error.Ok) return true;
		GD.PushWarning($"Could not save dog presets to {PresetStoragePath}: {error}");
		return false;
	}

	private void LoadPresets()
	{
		var config = new ConfigFile();
		if (config.Load(PresetStoragePath) != Error.Ok) return;
		var format = config.GetValue("meta", "format", 0);
		if (format.VariantType != Variant.Type.Int || format.AsInt64() != PresetFormat) return;
		foreach (var section in config.GetSections())
		{
			if (_presets.Count >= MaxPresets) break;
			if (!section.StartsWith(PresetSectionPrefix, StringComparison.Ordinal)) continue;
			if (new[] { "name", "dog_itemdef", "accessories", "layer_order" }.Any(key => !config.HasSectionKey(section, key))) continue;
			var id = section[PresetSectionPrefix.Length..];
			var name = config.GetValue(section, "name");
			var dog = config.GetValue(section, "dog_itemdef");
			var accessories = config.GetValue(section, "accessories");
			var layers = config.GetValue(section, "layer_order");
			if (name.VariantType != Variant.Type.String || dog.VariantType != Variant.Type.Int
				|| dog.AsInt64() < 0 || dog.AsInt64() > int.MaxValue || accessories.VariantType != Variant.Type.Array
				|| layers.VariantType != Variant.Type.PackedStringArray) continue;
			var placements = new List<AccessoryPlacement>();
			var valid = true;
			foreach (var item in accessories.AsGodotArray())
			{
				if (item.VariantType != Variant.Type.Dictionary || !TryReadPresetPlacement(item.AsGodotDictionary(), out var placement))
				{ valid = false; break; }
				placements.Add(placement!);
				if (placements.Count > _catalog.Count) { valid = false; break; }
			}
			if (!valid) continue;
			var preset = new DogPreset(id, name.AsString(), dog.AsInt32(), placements, layers.AsStringArray());
			// Load known cosmetics without inventing ownership. CanApplyPreset checks the fresh Steam snapshot.
			if (IsValidPreset(preset)) _presets.Add(FreezePreset(preset));
		}
	}

	private static bool TryReadPresetPlacement(Godot.Collections.Dictionary fields, out AccessoryPlacement? placement)
	{
		placement = null;
		if (!PresetField(fields, "id", Variant.Type.String, out var id)
			|| !PresetField(fields, "position", Variant.Type.Vector2, out var position)
			|| !PresetField(fields, "tint", Variant.Type.Color, out var tint)
			|| !PresetNumber(fields, "scale", out var scale) || !PresetNumber(fields, "rotation", out var rotation)
			|| !PresetField(fields, "text", Variant.Type.String, out var text)
			|| !PresetField(fields, "background", Variant.Type.Bool, out var background)) return false;
		placement = new AccessoryPlacement(id.AsString(), position.AsVector2())
		{
			Tint = tint.AsColor(), Scale = scale, RotationDegrees = rotation,
			Text = text.AsString(), BackgroundVisible = background.AsBool()
		};
		return true;
	}

	private static bool PresetField(Godot.Collections.Dictionary fields, string key, Variant.Type type, out Variant value)
	{
		value = default;
		return fields.TryGetValue(key, out value) && value.VariantType == type;
	}

	private static bool PresetNumber(Godot.Collections.Dictionary fields, string key, out float value)
	{
		value = 0;
		if (!fields.TryGetValue(key, out var item) || item.VariantType is not (Variant.Type.Int or Variant.Type.Float)) return false;
		value = item.AsSingle();
		return float.IsFinite(value);
	}
}
