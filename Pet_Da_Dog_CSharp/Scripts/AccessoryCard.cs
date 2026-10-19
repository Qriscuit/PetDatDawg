using System;
using Godot;

/// <summary>Editable catalog card template. Its exported sample data is safe in the editor.</summary>
[Tool]
public partial class AccessoryCard : Button
{
	private string _displayName = "Accessory", _previewStatus = "Click to place";
	private Texture2D? _previewTexture;
	private Color _previewTint = Colors.White;
	private const float CatalogDragThreshold = 8;
	private bool _trackingPointer, _dragRecognized, _suppressClick;
	private Vector2 _pressScreenPosition;
	private string _chooseTooltip = string.Empty;
	public event Action? CatalogChosen;
	public event Func<bool>? CatalogDragStarted;
	[ExportGroup("Editor sample")]
	[Export] public string DisplayName { get => _displayName; set { _displayName = value; RefreshIfReady(); } }
	[Export] public Texture2D? PreviewTexture { get => _previewTexture; set { _previewTexture = value; RefreshIfReady(); } }
	[Export] public string PreviewStatus { get => _previewStatus; set { _previewStatus = value; RefreshIfReady(); } }
	[Export] public Color PreviewTint { get => _previewTint; set { _previewTint = value; RefreshIfReady(); } }
	[ExportGroup("State captions")]
	[Export] public string AvailableStatusText { get; set; } = "Click or drag to place";
	[Export] public string AdditionalCopyStatusText { get; set; } = "Click or drag another";
	[Export] public string EquippedStatusText { get; set; } = "Equipped";
	[Export] public string LockedStatusText { get; set; } = "Unlock from a box";
	[Export] public string ItemPlacementStatusText { get; set; } = "Click the dog";
	[Export] public string TextPlacementStatusText { get; set; } = "Place near dog";
	/// <summary>Named tokens: {name}, {id}, and {category}.</summary>
	[Export(PropertyHint.MultilineText)] public string ChooseTooltipFormat { get; set; } = "Click {name}, then click your desktop dog to place it, or drag it from this card onto the dog. Each placement uses one owned copy.";

	public TextureRect Preview => GetNode<TextureRect>("%CardTexture");
	public Label Status => GetNode<Label>("%CardStatus");

	public override void _Ready()
	{
		RefreshSample();
		if (!Engine.IsEditorHint()) Pressed += OnPressed;
	}
	private void OnPressed()
	{
		// A native window may deliver the button's release after the desktop drop.
		// Keep suppression until the next press so it cannot arm another copy.
		if (!_suppressClick) CatalogChosen?.Invoke();
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (Engine.IsEditorHint() || Disabled) return;
		if (@event is InputEventKey { Pressed: true, Echo: false }) _suppressClick = false;
		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
		{
			if (button.Pressed)
			{
				_trackingPointer = true;
				_dragRecognized = _suppressClick = false;
				_pressScreenPosition = DisplayServer.MouseGetPosition();
			}
			else
			{
				RecognizeCatalogDrag(DisplayServer.MouseGetPosition());
				_trackingPointer = false;
			}
		}
		else if (@event is InputEventMouseMotion) RecognizeCatalogDrag(DisplayServer.MouseGetPosition());
	}

	public override void _Process(double delta)
	{
		if (Engine.IsEditorHint() || !_trackingPointer) return;
		if (Disabled || !IsVisibleInTree()) { _trackingPointer = false; return; }
		PollCatalogPointer(DisplayServer.MouseGetPosition(), NativeWindowBridge.IsLeftMouseButtonDown());
	}

	private void PollCatalogPointer(Vector2 screenPosition, bool leftDown)
	{
		// The card and dog live in different native windows. Poll the real pointer
		// rather than relying on motion/release events remaining in this window.
		// Sample the final position before ending tracking: a quick cross-window
		// drag can move and release between frames without another GUI event here.
		RecognizeCatalogDrag(screenPosition);
		if (!leftDown) _trackingPointer = false;
	}

	private void RecognizeCatalogDrag(Vector2 screenPosition)
	{
		if (!_trackingPointer || _dragRecognized ||
			screenPosition.DistanceSquaredTo(_pressScreenPosition) < CatalogDragThreshold * CatalogDragThreshold) return;
		_dragRecognized = _suppressClick = true;
		CatalogDragStarted?.Invoke();
	}
	private void RefreshIfReady() { if (IsNodeReady()) RefreshSample(); }

	public void Configure(AccessoryDefinition accessory, Color tint)
	{
		DisplayName = accessory.Name; PreviewTexture = accessory.Texture; PreviewTint = tint;
		_chooseTooltip = (ChooseTooltipFormat ?? string.Empty).Replace("{name}", accessory.Name)
			.Replace("{id}", accessory.Id).Replace("{category}", AccessoryCategories.For(accessory));
		TooltipText = _chooseTooltip;
		RefreshSample();
	}

	public void RefreshStatus(bool placing, bool equipped, bool isText, bool owned = true, bool canAddCopy = false)
	{
		Disabled = !owned;
		Status.Text = !owned ? LockedStatusText : placing ? isText ? TextPlacementStatusText : ItemPlacementStatusText
			: equipped ? canAddCopy ? AdditionalCopyStatusText : EquippedStatusText : AvailableStatusText;
		TooltipText = _chooseTooltip + (owned && equipped && !canAddCopy
			? "\nAll owned copies are placed. Click to select an existing copy." : string.Empty);
	}

	private void RefreshSample()
	{
		Preview.Texture = PreviewTexture;
		Preview.Modulate = PreviewTint;
		GetNode<Label>("%CardName").Text = DisplayName;
		Status.Text = PreviewStatus;
	}
}
