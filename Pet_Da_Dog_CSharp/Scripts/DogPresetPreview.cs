using Godot;
using System;
using System.Collections.Generic;

/// <summary>A read-only drawing of a saved outfit, independent of current ownership and equipment.</summary>
public partial class DogPresetPreview : Control
{
	private const string StarterDogTexturePath = "res://Sprites/Doggo.png";
	private const float ContentPadding = 8;
	private sealed record Layer(string Id, Texture2D Texture, bool IsText, Vector2 Center, Vector2 Size,
		float RotationDegrees, Color Tint, string Text, bool BackgroundVisible);
	private readonly List<Layer> _layers = new();
	private readonly List<string> _layerIds = new();
	private Rect2 _dogBounds;
	private Rect2 _contentBounds;
	private DesktopAppearance? _textAppearance;
	private Vector2 _textReferenceSize = Vector2.One;

	// Geometry hooks for the headless UI smoke: all coordinates are in the
	// visible dog's source-pixel space, before fitting the complete outfit.
	internal Rect2 DogBounds => _dogBounds;
	internal Rect2 ContentBounds => _contentBounds;
	internal IReadOnlyList<string> DrawLayerIds => _layerIds.AsReadOnly();
	internal Rect2 FittedContentBounds => FitContentBounds(_contentBounds, Size);

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		FocusMode = FocusModeEnum.None;
		TextureFilter = CanvasItem.TextureFilterEnum.Linear;
		QueueRedraw();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationResized) QueueRedraw();
	}

	public void Configure(AccessoryWardrobe wardrobe, DogPreset preset)
	{
		ArgumentNullException.ThrowIfNull(wardrobe);
		ArgumentNullException.ThrowIfNull(preset);
		MouseFilter = MouseFilterEnum.Ignore;
		FocusMode = FocusModeEnum.None;
		_layers.Clear();
		_layerIds.Clear();
		_dogBounds = _contentBounds = new Rect2();
		_textAppearance = DesktopAppearance.Default;
		_textReferenceSize = _textAppearance.TextCanvasSize;

		var dog = SteamCosmeticCatalog.Find(preset.DogItemDefId);
		var path = dog?.Kind == "dog" ? dog.AssetPath : StarterDogTexturePath;
		// Use the desktop art's visible crop, not the UI catalog icon. The catalog
		// shares its cached textures/crops between cards without creating viewports.
		var texture = SteamCosmeticCatalog.CroppedTexture(path)
			?? SteamCosmeticCatalog.CroppedTexture(StarterDogTexturePath);
		if (texture == null || texture.GetWidth() <= 0 || texture.GetHeight() <= 0)
		{
			QueueRedraw();
			return;
		}
		var dogSize = texture.GetSize();
		_dogBounds = _contentBounds = new Rect2(Vector2.Zero, dogSize);
		var available = new Dictionary<string, Layer>(StringComparer.Ordinal)
		{
			[AccessoryWardrobe.DogLayerId] = new(AccessoryWardrobe.DogLayerId, texture, false,
				dogSize * 0.5f, dogSize, 0, Colors.White, string.Empty, false),
		};
		var accessoryIds = new List<string>();
		foreach (var placement in preset.Accessories)
		{
			// Find returns artwork even for a currently unowned item. Previewing the
			// immutable snapshot never equips it or changes Steam ownership.
			var definition = wardrobe.Find(placement.Id);
			if (definition == null || available.ContainsKey(placement.Id) || !placement.Position.IsFinite()
				|| !float.IsFinite(placement.Scale) || placement.Scale <= 0 || !float.IsFinite(placement.RotationDegrees)) continue;
			var center = dogSize * placement.Position;
			// Match DesktopPet/AccessoryPreview: accessory dimensions are normalized
			// to the visible dog's height, preserving proportions across dog breeds.
			var size = definition.Size * dogSize.Y * placement.Scale;
			if (!center.IsFinite() || !size.IsFinite() || size.X <= 0 || size.Y <= 0) continue;
			var bounds = AccessoryGeometry.Bounds(center, size, placement.RotationDegrees);
			if (!bounds.Position.IsFinite() || !bounds.Size.IsFinite()) continue;
			var tint = placement.Tint;
			if (!float.IsFinite(tint.R) || !float.IsFinite(tint.G) || !float.IsFinite(tint.B) || !float.IsFinite(tint.A)) tint = Colors.White;
			available.Add(placement.Id, new Layer(placement.Id, definition.Texture, definition.IsText, center, size,
				placement.RotationDegrees, tint, placement.Text, placement.BackgroundVisible));
			accessoryIds.Add(placement.Id);
			_contentBounds = _contentBounds.Merge(bounds);
		}

		var seen = new HashSet<string>(StringComparer.Ordinal);
		// Well-formed presets specify every layer. Missing legacy layer entries
		// receive a deterministic fallback without changing the saved snapshot.
		if (!ContainsDog(preset.LayerOrder)) AddLayer(AccessoryWardrobe.DogLayerId);
		foreach (var id in preset.LayerOrder) AddLayer(id);
		foreach (var id in accessoryIds) AddLayer(id);
		QueueRedraw();

		void AddLayer(string id)
		{
			if (!available.TryGetValue(id, out var layer) || !seen.Add(id)) return;
			_layers.Add(layer);
			_layerIds.Add(id);
		}
	}

	private static bool ContainsDog(IReadOnlyList<string> order)
	{
		foreach (var id in order) if (id == AccessoryWardrobe.DogLayerId) return true;
		return false;
	}

	internal static Rect2 FitContentBounds(Rect2 content, Vector2 controlSize)
	{
		if (!controlSize.IsFinite() || controlSize.X <= 0 || controlSize.Y <= 0
			|| !content.Size.IsFinite() || content.Size.X <= 0 || content.Size.Y <= 0) return new Rect2();
		var padding = Mathf.Min(ContentPadding, Mathf.Min(controlSize.X, controlSize.Y) * 0.25f);
		var available = controlSize - Vector2.One * (padding * 2);
		var scale = Mathf.Min(available.X / content.Size.X, available.Y / content.Size.Y);
		var fittedSize = content.Size * scale;
		return new Rect2((controlSize - fittedSize) * 0.5f, fittedSize);
	}

	public override void _Draw()
	{
		if (_layers.Count == 0) return;
		var fitted = FittedContentBounds;
		if (fitted.Size.X <= 0 || fitted.Size.Y <= 0) return;
		var scale = fitted.Size.X / _contentBounds.Size.X;
		var origin = fitted.Position - _contentBounds.Position * scale;
		foreach (var layer in _layers)
		{
			var drawScale = layer.IsText ? layer.Size / _textReferenceSize * scale : Vector2.One * scale;
			DrawSetTransform(origin + layer.Center * scale, Mathf.DegToRad(layer.RotationDegrees), drawScale);
			if (layer.IsText)
				PetTextAccessory.DrawTextBox(this, layer.Text, layer.Tint, layer.BackgroundVisible, _textAppearance);
			else
				DrawTextureRect(layer.Texture, new Rect2(-layer.Size * 0.5f, layer.Size), false, layer.Tint);
		}
		DrawSetTransform(Vector2.Zero, 0, Vector2.One);
	}
}
