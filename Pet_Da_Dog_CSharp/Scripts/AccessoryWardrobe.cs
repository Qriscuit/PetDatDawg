using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

public sealed record AccessoryDefinition(string Id, string Name, Texture2D Texture, Vector2 Size, bool CanRecolor = false)
{
	public bool IsText { get; init; }
	public int SteamItemDefId { get; init; }
}
public sealed record AccessoryPlacement(string Id, Vector2 Position)
{
	public Color Tint { get; init; } = Colors.White;
	public float Scale { get; init; } = 1;
	public float RotationDegrees { get; init; }
	public string Text { get; init; } = string.Empty;
	public bool BackgroundVisible { get; init; } = true;
}

// Local cosmetic preferences only. This never reads or writes the Steam pets balance.
public partial class AccessoryWardrobe : Node
{
	private const string AssetDirectory = "res://Sprites/Accessories";
	private const string PlacementSection = "placements";
	private const string ColorSection = "colors";
	private const string ScaleSection = "sizes";
	private const string RotationSection = "rotations";
	private const string TextSection = "text";
	private const string TextBackgroundKey = "background_visible";
	private const string LayerSection = "layers";
	public const string DogLayerId = "@dog";
	public const string TextAccessoryId = "@text-box";
	public const string DefaultText = "Good dog!";
	public const int MaxTextLength = 64;
	public static readonly Vector2 TextMinPosition = new(-0.75f, -0.35f);
	public static readonly Vector2 TextMaxPosition = new(1.75f, 1.25f);
	public const float MinScale = 0.5f;
	public const float MaxScale = 2f;
	public const float MinRotation = -180;
	public const float MaxRotation = 180;
	private const int HistoryLimit = 30;
	private const float AccessorySize = 0.26f;
	// Only neutral artwork explicitly prepared for tinting offers a color picker.
	private static readonly HashSet<string> RecolorableIds = new(StringComparer.OrdinalIgnoreCase)
	{
		"DragonWing.jpg", "FairyWing.jpg", "SafetyGlasses.jpg"
	};
	private readonly List<AccessoryDefinition> _catalog = new();
	private readonly List<AccessoryPlacement> _equipped = new();
	private HashSet<int> _ownedSteamItems = new();
	private int _selectedDogItemDefId;
	private readonly List<string> _layerOrder = new() { DogLayerId };
	private readonly Dictionary<string, Color> _colors = new();
	private readonly Dictionary<string, float> _scales = new();
	private readonly Dictionary<string, float> _rotations = new();
	private string _text = DefaultText;
	private bool _textBackgroundVisible = true;
	private sealed record OutfitState(AccessoryPlacement[] Equipped, string[] LayerOrder, Dictionary<string, Color> Colors,
		Dictionary<string, float> Scales, Dictionary<string, float> Rotations, string Text, bool TextBackgroundVisible);
	private readonly List<(OutfitState State, string Label)> _history = new();
	private OutfitState? _editStart;
	private string _editLabel = string.Empty;
	private bool _loading;
	private bool _dirty;

	[Export] public string StoragePath { get; set; } = "user://accessories.cfg";
	public IReadOnlyList<AccessoryDefinition> Catalog => _catalog.AsReadOnly();
	public IReadOnlyList<AccessoryPlacement> Equipped => _equipped.FindAll(item => CanEquip(item.Id)).AsReadOnly();
	public int SelectedDogItemDefId => _ownedSteamItems.Contains(_selectedDogItemDefId) ? _selectedDogItemDefId : 0;
	public string CurrentDogTexturePath => SteamCosmeticCatalog.Find(SelectedDogItemDefId)?.AssetPath ?? "res://Sprites/Doggo.png";
	// Bottom-to-top drawing order. The dog is present even with an empty outfit.
	public IReadOnlyList<string> LayerOrder => _layerOrder.AsReadOnly();
	public event Action? Changed;
	public bool CanUndo => _history.Count > 0;
	public string UndoLabel => CanUndo ? _history[^1].Label : string.Empty;

	public override void _Ready()
	{
		LoadCatalog(AssetDirectory);
		_catalog.Add(new AccessoryDefinition(TextAccessoryId, "Text Box", WoodlandTheme.Icon("text-box")!, new Vector2(1.15f, 0.483f), true) { IsText = true });
		foreach (var item in SteamCosmeticCatalog.All)
		{
			if (item.Kind != "accessory") continue;
			var texture = SteamCosmeticCatalog.CroppedTexture(item.AssetPath);
			if (texture == null) continue;
			var size = texture.GetSize() * (AccessorySize / Mathf.Max(texture.GetWidth(), texture.GetHeight()));
			_catalog.Add(new AccessoryDefinition(item.AccessoryId, item.Name, texture, size) { SteamItemDefId = item.ItemDefId });
		}
		LoadPlacements();
	}

	public override void _ExitTree()
	{
		if (_dirty)
		{
			Save();
		}
	}

	public AccessoryDefinition? Find(string id) => _catalog.Find(item => item.Id == id);
	public bool CanEquip(string id) => Find(id) is { } item && (item.SteamItemDefId == 0 || _ownedSteamItems.Contains(item.SteamItemDefId));
	public bool OwnsSteamItem(int itemDefId) => _ownedSteamItems.Contains(itemDefId);
	public void SetSteamOwnership(IEnumerable<int> itemDefIds)
	{
		var owned = new HashSet<int>(itemDefIds);
		if (_ownedSteamItems.SetEquals(owned)) return;
		_ownedSteamItems = owned;
		// Ownership comes only from the current Steam response and is never saved as a preference.
		Changed?.Invoke();
	}
	public void SelectDog(int itemDefId)
	{
		if (itemDefId != 0 && (!OwnsSteamItem(itemDefId) || SteamCosmeticCatalog.Find(itemDefId)?.Kind != "dog")) return;
		if (_selectedDogItemDefId == itemDefId) return;
		_selectedDogItemDefId = itemDefId;
		NotifyChanged(); Save();
	}
	public Color GetTint(string id) => _colors.TryGetValue(id, out var tint) ? tint : Colors.White;
	public float GetScale(string id) => _scales.TryGetValue(id, out var scale) ? scale : 1;
	public float GetRotationDegrees(string id) => _rotations.TryGetValue(id, out var rotation) ? rotation : 0;
	public AccessoryPlacement? GetPlacement(string id) => CanEquip(id) ? _equipped.Find(item => item.Id == id) : null;
	public int GetLayerIndex(string id) => _layerOrder.IndexOf(id);
	public bool CanMoveLayer(string id, int direction)
	{
		if (direction is not (1 or -1)) return false;
		var index = GetLayerIndex(id);
		return index >= 0 && index + direction >= 0 && index + direction < _layerOrder.Count;
	}
	public bool MoveLayer(string id, int direction)
	{
		if (!CanMoveLayer(id, direction)) return false;
		Remember(direction > 0 ? "move layer forward" : "move layer backward");
		var index = GetLayerIndex(id);
		(_layerOrder[index], _layerOrder[index + direction]) = (_layerOrder[index + direction], _layerOrder[index]);
		NotifyChanged();
		return true;
	}
	public string GetText() => _text;
	public bool GetTextBackgroundVisible() => _textBackgroundVisible;
	public Vector2 ClampPosition(string id, Vector2 position) => Find(id)?.IsText == true
		? position.Clamp(TextMinPosition, TextMaxPosition) : position.Clamp(Vector2.Zero, Vector2.One);
	public void SetText(string text)
	{
		// Limit grapheme clusters so emoji and combining characters remain intact.
		text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
		var characters = System.Globalization.StringInfo.ParseCombiningCharacters(text);
		if (characters.Length > MaxTextLength) text = text[..characters[MaxTextLength]];
		if (_text == text) return;
		Remember("text change"); _text = text;
		var index = _equipped.FindIndex(item => item.Id == TextAccessoryId);
		if (index >= 0) _equipped[index] = _equipped[index] with { Text = text };
		NotifyChanged();
	}

	public void SetTextBackgroundVisible(bool visible)
	{
		if (_textBackgroundVisible == visible) return;
		Remember("text background");
		_textBackgroundVisible = visible;
		var index = _equipped.FindIndex(item => item.Id == TextAccessoryId);
		if (index >= 0) _equipped[index] = _equipped[index] with { BackgroundVisible = visible };
		NotifyChanged();
	}

	public void SetTransform(string id, float scale, float rotationDegrees)
	{
		if (Find(id) == null || !float.IsFinite(scale) || !float.IsFinite(rotationDegrees)) return;
		scale = Mathf.Clamp(scale, MinScale, MaxScale);
		rotationDegrees = Mathf.Clamp(rotationDegrees, MinRotation, MaxRotation);
		if (Mathf.IsEqualApprox(scale, GetScale(id)) && Mathf.IsEqualApprox(rotationDegrees, GetRotationDegrees(id))) return;
		Remember("size / rotation");
		if (Mathf.IsEqualApprox(scale, 1)) _scales.Remove(id); else _scales[id] = scale;
		if (Mathf.IsZeroApprox(rotationDegrees)) _rotations.Remove(id); else _rotations[id] = rotationDegrees;
		var index = _equipped.FindIndex(item => item.Id == id);
		if (index >= 0) _equipped[index] = _equipped[index] with { Scale = scale, RotationDegrees = rotationDegrees };
		NotifyChanged();
	}

	// Group one drag or color-wheel gesture into one undo step. History is session-only.
	public void BeginEdit(string label)
	{
		CommitEdit();
		_editStart = CaptureState();
		_editLabel = label;
	}

	public void CommitEdit()
	{
		var start = _editStart;
		_editStart = null;
		if (start != null && !MatchesState(start))
		{
			PushHistory(start, _editLabel);
			Changed?.Invoke();
		}
	}

	public void CancelEdit()
	{
		var start = _editStart;
		_editStart = null;
		if (start != null && !MatchesState(start)) RestoreState(start);
	}

	public bool Undo()
	{
		CommitEdit();
		if (!CanUndo) return false;
		var state = _history[^1].State;
		_history.RemoveAt(_history.Count - 1);
		RestoreState(state);
		return true;
	}

	public void ClearHistory()
	{
		_editStart = null;
		_history.Clear();
		Changed?.Invoke();
	}

	private OutfitState CaptureState() => new(_equipped.ToArray(), _layerOrder.ToArray(), new(_colors), new(_scales), new(_rotations), _text, _textBackgroundVisible);
	private bool MatchesState(OutfitState state) => System.Linq.Enumerable.SequenceEqual(_equipped, state.Equipped)
		&& System.Linq.Enumerable.SequenceEqual(_layerOrder, state.LayerOrder)
		&& SameDictionary(_colors, state.Colors) && SameDictionary(_scales, state.Scales) && SameDictionary(_rotations, state.Rotations)
		&& _text == state.Text && _textBackgroundVisible == state.TextBackgroundVisible;
	private static bool SameDictionary<T>(Dictionary<string, T> left, Dictionary<string, T> right)
		=> left.Count == right.Count && System.Linq.Enumerable.All(left, pair => right.TryGetValue(pair.Key, out var value)
			&& EqualityComparer<T>.Default.Equals(pair.Value, value));
	private void Remember(string label)
	{
		if (!_loading && _editStart == null) PushHistory(CaptureState(), label);
	}
	private void PushHistory(OutfitState state, string label)
	{
		_history.Add((state, label));
		if (_history.Count > HistoryLimit) _history.RemoveAt(0);
	}
	private void RestoreState(OutfitState state)
	{
		_equipped.Clear();
		_equipped.AddRange(state.Equipped);
		_layerOrder.Clear();
		_layerOrder.AddRange(state.LayerOrder);
		_colors.Clear(); foreach (var pair in state.Colors) _colors.Add(pair.Key, pair.Value);
		_scales.Clear(); foreach (var pair in state.Scales) _scales.Add(pair.Key, pair.Value);
		_rotations.Clear(); foreach (var pair in state.Rotations) _rotations.Add(pair.Key, pair.Value);
		_text = state.Text;
		_textBackgroundVisible = state.TextBackgroundVisible;
		NotifyChanged();
	}

	public void SetTint(string id, Color tint)
	{
		if (Find(id)?.CanRecolor != true || !float.IsFinite(tint.R) || !float.IsFinite(tint.G)
			|| !float.IsFinite(tint.B) || !float.IsFinite(tint.A))
		{
			return;
		}
		tint = new Color(Mathf.Clamp(tint.R, 0, 1), Mathf.Clamp(tint.G, 0, 1), Mathf.Clamp(tint.B, 0, 1), 1);
		if (GetTint(id).IsEqualApprox(tint))
		{
			return;
		}
		Remember("color change");
		if (tint.IsEqualApprox(Colors.White))
		{
			_colors.Remove(id);
		}
		else
		{
			_colors[id] = tint;
		}
		var index = _equipped.FindIndex(item => item.Id == id);
		if (index >= 0)
		{
			_equipped[index] = _equipped[index] with { Tint = tint };
		}
		NotifyChanged();
	}

	public void Equip(string id, Vector2 normalizedPosition)
	{
		if (Find(id) == null || (!_loading && !CanEquip(id)) || !normalizedPosition.IsFinite())
		{
			return;
		}

		var placement = new AccessoryPlacement(id, ClampPosition(id, normalizedPosition))
			{ Tint = GetTint(id), Scale = GetScale(id), RotationDegrees = GetRotationDegrees(id),
				Text = Find(id)?.IsText == true ? _text : string.Empty,
				BackgroundVisible = Find(id)?.IsText != true || _textBackgroundVisible };
		var index = _equipped.FindIndex(item => item.Id == id);
		if (index >= 0)
		{
			if (_equipped[index] == placement)
			{
				return;
			}
			Remember("move accessory");
			_equipped[index] = placement;
		}
		else
		{
			Remember("place accessory");
			_equipped.Add(placement);
			_layerOrder.Add(id);
		}
		NotifyChanged();
	}

	public void Move(string id, Vector2 normalizedPosition)
	{
		if (_equipped.Exists(item => item.Id == id))
		{
			Equip(id, normalizedPosition);
		}
	}

	public void Remove(string id)
	{
		if (_equipped.Exists(item => item.Id == id))
		{
			Remember("remove accessory");
			_equipped.RemoveAll(item => item.Id == id);
			_layerOrder.Remove(id);
			NotifyChanged();
		}
	}

	public void Clear()
	{
		if (_equipped.Count == 0)
		{
			return;
		}
		Remember("clear outfit");
		_equipped.Clear();
		_layerOrder.Clear();
		_layerOrder.Add(DogLayerId);
		NotifyChanged();
	}

	public bool Save()
	{
		var config = new ConfigFile();
		config.SetValue("dog", "itemdef", _selectedDogItemDefId);
		foreach (var item in _equipped)
		{
			config.SetValue(PlacementSection, item.Id, item.Position);
		}
		foreach (var (id, tint) in _colors)
		{
			config.SetValue(ColorSection, id, tint);
		}
		foreach (var (id, scale) in _scales) config.SetValue(ScaleSection, id, scale);
		foreach (var (id, rotation) in _rotations) config.SetValue(RotationSection, id, rotation);
		if (_text != DefaultText) config.SetValue(TextSection, TextAccessoryId, _text);
		if (!_textBackgroundVisible) config.SetValue(TextSection, TextBackgroundKey, false);
		config.SetValue(LayerSection, "order", _layerOrder.ToArray());
		var error = config.Save(StoragePath);
		if (error != Error.Ok)
		{
			GD.PushWarning($"Could not save accessory preferences to {StoragePath}: {error}");
			return false;
		}
		_dirty = false;
		return true;
	}

	private void NotifyChanged()
	{
		_dirty = true;
		Changed?.Invoke();
	}

	private void LoadPlacements()
	{
		var config = new ConfigFile();
		if (config.Load(StoragePath) != Error.Ok)
		{
			return;
		}
		_loading = true;
		_selectedDogItemDefId = config.GetValue("dog", "itemdef", 0).AsInt32();
		var text = config.GetValue(TextSection, TextAccessoryId, DefaultText);
		if (text.VariantType == Variant.Type.String) SetText(text.AsString());
		var background = config.GetValue(TextSection, TextBackgroundKey, true);
		if (background.VariantType == Variant.Type.Bool) SetTextBackgroundVisible(background.AsBool());
		foreach (var definition in _catalog)
		{
			var scale = config.GetValue(ScaleSection, definition.Id, 1f);
			var rotation = config.GetValue(RotationSection, definition.Id, 0f);
			var validScale = scale.VariantType is Variant.Type.Float or Variant.Type.Int && float.IsFinite(scale.AsSingle());
			var validRotation = rotation.VariantType is Variant.Type.Float or Variant.Type.Int && float.IsFinite(rotation.AsSingle());
			SetTransform(definition.Id, validScale ? scale.AsSingle() : 1, validRotation ? rotation.AsSingle() : 0);
		}
		if (config.HasSection(ColorSection))
		{
			foreach (var id in config.GetSectionKeys(ColorSection))
			{
				var value = config.GetValue(ColorSection, id);
				if (value.VariantType == Variant.Type.Color)
				{
					SetTint(id, value.AsColor());
				}
			}
		}
		if (config.HasSection(PlacementSection))
		{
			foreach (var id in config.GetSectionKeys(PlacementSection))
			{
				var value = config.GetValue(PlacementSection, id);
				if (value.VariantType == Variant.Type.Vector2)
				{
					Equip(id, value.AsVector2());
				}
			}
		}
		LoadLayerOrder(config);
		_loading = false;
		ClearHistory();
		_dirty = false;
	}

	private void LoadLayerOrder(ConfigFile config)
	{
		var value = config.GetValue(LayerSection, "order", Array.Empty<string>());
		var saved = new List<string>();
		if (value.VariantType == Variant.Type.PackedStringArray)
		{
			saved.AddRange(value.AsStringArray());
		}
		else if (value.VariantType == Variant.Type.Array)
		{
			foreach (var item in value.AsGodotArray())
				if (item.VariantType == Variant.Type.String) saved.Add(item.AsString());
		}

		var allowed = new HashSet<string>(StringComparer.Ordinal) { DogLayerId };
		foreach (var item in _equipped) allowed.Add(item.Id);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		_layerOrder.Clear();
		foreach (var id in saved)
			if (allowed.Contains(id) && seen.Add(id)) _layerOrder.Add(id);
		// Missing/legacy dog entries default below the outfit; missing accessories
		// append in their existing equip order without disrupting valid saved layers.
		if (seen.Add(DogLayerId)) _layerOrder.Insert(0, DogLayerId);
		foreach (var item in _equipped)
			if (seen.Add(item.Id)) _layerOrder.Add(item.Id);
	}

	private void LoadCatalog(string directory)
	{
		// ResourceLoader preserves the source names in exported PCKs too.
		var entries = ResourceLoader.ListDirectory(directory);
		Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
		foreach (var entry in entries)
		{
			var path = $"{directory}/{entry}";
			if (entry.EndsWith('/'))
			{
				LoadCatalog(path.TrimEnd('/'));
				continue;
			}
			var extension = Path.GetExtension(entry).ToLowerInvariant();
			if (extension is not (".png" or ".webp" or ".svg" or ".jpg" or ".jpeg"))
			{
				continue;
			}
			var texture = ResourceLoader.Load<Texture2D>(path);
			if (texture == null)
			{
				continue;
			}
			var id = path[(AssetDirectory.Length + 1)..];
			var canRecolor = RecolorableIds.Contains(id);
			if (canRecolor && extension is (".jpg" or ".jpeg"))
			{
				texture = RemoveJpegBackground(texture);
			}
			var bounds = GetVisibleBounds(texture);
			if (bounds.Size.X == 0 || bounds.Size.Y == 0)
			{
				continue;
			}
			var cropped = new AtlasTexture { Atlas = texture, Region = bounds, FilterClip = true };
			var size = (Vector2)bounds.Size * (AccessorySize / Mathf.Max(bounds.Size.X, bounds.Size.Y));
			var name = Path.GetFileNameWithoutExtension(entry);
			name = Regex.Replace(name.Replace('_', ' ').Replace('-', ' '), "([a-z])([A-Z0-9])", "$1 $2");
			name = Regex.Replace(name, "([A-Z])([A-Z][a-z])", "$1 $2");
			_catalog.Add(new AccessoryDefinition(id, name, cropped, size, canRecolor));
		}
	}

	private static Texture2D RemoveJpegBackground(Texture2D texture)
	{
		using var image = texture.GetImage();
		if (image == null)
		{
			return texture;
		}
		image.Convert(Image.Format.Rgba8);
		for (var y = 0; y < image.GetHeight(); y++)
		{
			for (var x = 0; x < image.GetWidth(); x++)
			{
				var pixel = image.GetPixel(x, y);
				var brightness = Mathf.Max(pixel.R, Mathf.Max(pixel.G, pixel.B));
				// White/gray art on black: remove JPEG noise and soften its antialiased edge.
				var alpha = Mathf.SmoothStep(0.06f, 0.30f, brightness);
				image.SetPixel(x, y, alpha <= 0
					? new Color(1, 1, 1, 0)
					: new Color(Mathf.Min(1, pixel.R / alpha), Mathf.Min(1, pixel.G / alpha), Mathf.Min(1, pixel.B / alpha), alpha));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}

	public static Rect2I GetVisibleBounds(Texture2D texture)
	{
		using var image = texture.GetImage();
		if (image == null)
		{
			return new Rect2I();
		}
		var minX = image.GetWidth();
		var minY = image.GetHeight();
		var maxX = -1;
		var maxY = -1;
		for (var y = 0; y < image.GetHeight(); y++)
		{
			for (var x = 0; x < image.GetWidth(); x++)
			{
				if (image.GetPixel(x, y).A <= 0.03f)
				{
					continue;
				}
				minX = Mathf.Min(minX, x);
				minY = Mathf.Min(minY, y);
				maxX = Mathf.Max(maxX, x);
				maxY = Mathf.Max(maxY, y);
			}
		}
		return maxX < minX ? new Rect2I() : new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
	}
}
