using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>Category shelves and an inspector for accessories on the real desktop dog.</summary>
[Tool]
public partial class AccessoryEditor : VBoxContainer
{
	[Export] public PackedScene? CardScene { get; set; }
	[Export] public PackedScene? CategoryRowScene { get; set; }
	[Export] public PackedScene? ColorControlsScene { get; set; }
	[Export] public ColorPicker.PickerShapeType ColorWheelShape { get; set; } = ColorPicker.PickerShapeType.HsvWheel;
	[ExportGroup("State captions")]
	[Export] public string WardrobeUnavailableText { get; set; } = "The accessory wardrobe is unavailable.";
	[Export] public string NoSelectionText { get; set; } = "No accessory selected";
	[Export] public string ItemColorCaption { get; set; } = "Color";
	[Export] public string TextColorCaption { get; set; } = "Text color";
	[Export] public string LayerBehindDogText { get; set; } = "Layers · Behind dog";
	[Export] public string LayerInFrontOfDogText { get; set; } = "Layers · In front of dog";
	[ExportGroup("Instructions")]
	[Export(PropertyHint.MultilineText)] public string IdleInstructions { get; set; } = "Choose an item, then click your desktop dog to place it.\nDrag the dog to move it; right-click the dog or an item for layer options.";
	/// <summary>Use {name} for the selected accessory's display name.</summary>
	[Export(PropertyHint.MultilineText)] public string SelectedInstructionsFormat { get; set; } = "Drag {name} to move it; use its handles to resize or rotate.\nRight-click it or use the layer arrows to change what appears in front.";
	/// <summary>Use {name} for the accessory being placed.</summary>
	[Export(PropertyHint.MultilineText)] public string ItemPlacementInstructionsFormat { get; set; } = "Click your desktop dog to place {name}.\nPress Escape or Cancel to choose something else.";
	[Export(PropertyHint.MultilineText)] public string TextPlacementInstructions { get; set; } = "Click near your desktop dog to place the text.\nEdit its message, color and background in the side panel.";
	[ExportGroup("Save status")]
	[Export] public string OutfitSavedText { get; set; } = "Outfit saved";
	[Export] public string OutfitSaveFailedText { get; set; } = "Could not save outfit";
	[Export(PropertyHint.MultilineText)] public string OutfitSavedTooltip { get; set; } = "This outfit will return when you restart.";
	[Export(PropertyHint.MultilineText)] public string OutfitSaveFailedTooltip { get; set; } = "Your outfit is applied for this session. Try moving an accessory to save again.";
	/// <summary>Use {count} for the number of available accessories.</summary>
	[ExportGroup("Dynamic formats")]
	[Export] public string CatalogHeadingFormat { get; set; } = "Accessories · {count}";
	/// <summary>Use {value} for the selected item's size percentage.</summary>
	[Export] public string SizeValueFormat { get; set; } = "{value}%";
	/// <summary>Use {value} for the selected item's angle in degrees.</summary>
	[Export] public string RotationValueFormat { get; set; } = "{value}°";
	/// <summary>Use {label} for the outfit change being undone.</summary>
	[Export] public string UndoTooltipFormat { get; set; } = "Undo {label} (Ctrl + Z).";
	[Export] public string EmptyUndoTooltip { get; set; } = "No outfit changes to undo.";
	/// <summary>Use {name} for the current selection label, including the no-selection caption.</summary>
	[Export] public string SelectionTooltipFormat { get; set; } = "{name}";
	private readonly Dictionary<string, Button> _cards = new();
	private readonly Dictionary<string, Label> _cardStatuses = new();
	private readonly Dictionary<string, TextureRect> _cardTextures = new();
	private AccessoryWardrobe? _wardrobe;
	private AccessoryEditingSession? _session;
	private readonly Dictionary<string, AccessoryCategoryRow> _categoryRows = new();
	private Label? _instructions;
	private Label? _selectionLabel;
	private Label? _saveStatusLabel;
	private Button? _removeButton;
	private Button? _cancelButton;
	private Button? _clearButton;
	private Button? _undoButton;
	private VBoxContainer? _layerControls;
	private Button? _layerUpButton, _layerDownButton;
	private Label? _layerLabel;
	private VBoxContainer? _transformControls;
	private Slider? _sizeSlider, _rotationSlider;
	private Label? _sizeValueLabel, _rotationValueLabel;
	private bool _syncingTransform, _transformDragging;
	private Button? _advancedColorButton;
	private ScrollContainer? _catalogScroll;
	private VBoxContainer? _inspector;
	private HBoxContainer? _colorControls;
	private ColorPickerButton? _colorPicker;
	private Button? _resetColorButton;
	private Label? _colorLabel;
	private string? _colorAccessoryId;
	private bool _syncingColorPicker;
	private bool _colorSavePending;
	private HBoxContainer? _textControls;
	private LineEdit? _textInput;
	private CheckBox? _textBackgroundCheck;
	private bool _syncingText, _textEditing;

	public override void _Ready()
	{
		BindSceneNodes();
		// Authored sample cards remain visible in the editor. No autoloads, native
		// windows, cosmetic transactions or saves are touched during editor preview.
		if (Engine.IsEditorHint()) { SetProcess(false); return; }
		_wardrobe = GetNodeOrNull<AccessoryWardrobe>("/root/AccessoryWardrobe");
		_session = GetNodeOrNull<AccessoryEditingSession>("/root/AccessoryEditingSession");
		if (_wardrobe == null || _session == null)
		{
			_instructions!.Text = WardrobeUnavailableText;
			SetProcess(false); return;
		}
		BindBehavior();
		PopulateCatalog();
		_session.StateChanged += Refresh;
		_session.OutfitSaved += ShowSaveStatus;
		_session.EditStarting += PrepareDesktopEdit;
		VisibilityChanged += OnVisibilityChanged;
		_wardrobe.Changed += Refresh;
		Refresh();
	}

	private void BindSceneNodes()
	{
		_instructions = GetNode<Label>("%AccessoryInstructions");
		_selectionLabel = GetNode<Label>("%AccessorySelectionLabel");
		_saveStatusLabel = GetNode<Label>("%OutfitSaveStatus");
		_removeButton = GetNode<Button>("%RemoveAccessory");
		_cancelButton = GetNode<Button>("%CancelAccessory");
		_clearButton = GetNode<Button>("%ClearOutfit");
		_undoButton = GetNode<Button>("%UndoOutfit");
		_layerControls = GetNode<VBoxContainer>("%AccessoryLayerControls");
		_layerUpButton = GetNode<Button>("%MoveAccessoryLayerUp");
		_layerDownButton = GetNode<Button>("%MoveAccessoryLayerDown");
		_layerLabel = GetNode<Label>("%AccessoryLayerLabel");
		_transformControls = GetNode<VBoxContainer>("%AccessoryTransformControls");
		_sizeSlider = GetNode<Slider>("%AccessorySizeSlider");
		_rotationSlider = GetNode<Slider>("%AccessoryRotationSlider");
		_sizeValueLabel = GetNode<Label>("%AccessorySizeValue");
		_rotationValueLabel = GetNode<Label>("%AccessoryRotationValue");
		_textControls = GetNode<HBoxContainer>("%TextAccessoryControls");
		_textInput = GetNode<LineEdit>("%AccessoryTextInput");
		_textBackgroundCheck = GetNode<CheckBox>("%TextBackgroundToggle");
		_colorControls = GetNode<HBoxContainer>("%AccessoryColorControls");
		_colorPicker = GetNode<ColorPickerButton>("%AccessoryColorPicker");
		_resetColorButton = GetNode<Button>("%ResetAccessoryColor");
		_colorLabel = GetNode<Label>("%AccessoryColorLabel");
		_catalogScroll = GetNode<ScrollContainer>("%AccessoryCatalogScroll");
		_inspector = GetNode<VBoxContainer>("%AccessoryInspector");
	}

	private void BindBehavior()
	{
		_cancelButton!.Pressed += CancelInteraction;
		_removeButton!.Pressed += RemoveSelected;
		_clearButton!.Pressed += () =>
		{
			CancelInteraction(); _wardrobe!.Clear(); ShowSaveStatus(_wardrobe.Save()); _session!.ClearSelection();
		};
		_undoButton!.Pressed += UndoOutfit;
		_layerUpButton!.Pressed += () => MoveSelectedLayer(1);
		_layerDownButton!.Pressed += () => MoveSelectedLayer(-1);
		foreach (var slider in new[] { _sizeSlider!, _rotationSlider! })
		{
			slider.DragStarted += () => { PrepareInspectorEdit(); SavePendingText(); SavePendingColor(); _transformDragging = true; _wardrobe?.BeginEdit("size / rotation"); };
			slider.DragEnded += _ => FinishTransform();
			slider.ValueChanged += _ => ApplyTransform();
		}
		GetNode<Button>("%ResetAccessoryTransform").Pressed += () =>
		{
			if (_session!.SelectedId is string id) { PrepareInspectorEdit(); PrepareDesktopEdit(); _wardrobe!.SetTransform(id, 1, 0); ShowSaveStatus(_wardrobe.Save()); }
		};
		_textInput!.TextChanged += text =>
		{
			if (_syncingText || _wardrobe == null) return;
			PrepareInspectorEdit(); FinishTransform(); SavePendingColor();
			if (!_textEditing) { _wardrobe.BeginEdit("text change"); _textEditing = true; }
			_wardrobe.SetText(text); SyncTextInput(); ShowSaveStatus(_wardrobe.Save());
		};
		_textInput.FocusExited += SavePendingText;
		_textInput.TextSubmitted += _ => { SavePendingText(); _textInput.ReleaseFocus(); };
		_textBackgroundCheck!.Toggled += visible =>
		{
			if (_syncingText || _wardrobe == null) return;
			PrepareInspectorEdit(); PrepareDesktopEdit(); _wardrobe.SetTextBackgroundVisible(visible); ShowSaveStatus(_wardrobe.Save());
		};
		_resetColorButton!.Pressed += () => { OnAccessoryColorChanged(Colors.White); SavePendingColor(); };
		ConfigureColorPicker();
	}

	private void ConfigureColorPicker()
	{
		var picker = _colorPicker!.GetPicker();
		picker.PickerShape = ColorWheelShape;
		picker.EditAlpha = false; picker.DeferredMode = false; picker.EditIntensity = false;
		picker.PresetsVisible = false; picker.CanAddSwatches = false;
		if (ColorControlsScene == null) throw new InvalidOperationException("Assign the color controls scene in the AccessoryEditor inspector.");
		var controls = ColorControlsScene.Instantiate<AccessoryColorControls>();
		picker.AddChild(controls);
		_advancedColorButton = controls.AdvancedButton;
		controls.ColorSelected += color => { _colorPicker.Color = color; OnAccessoryColorChanged(color); };
		controls.AdvancedToggled += SetAdvancedColors;
		controls.ResetRequested += () => { _colorPicker.Color = Colors.White; OnAccessoryColorChanged(Colors.White); };
		controls.DoneRequested += () => _colorPicker.GetPopup().Hide();
		SetAdvancedColors(false);
		WoodlandTheme.ApplyPopup(_colorPicker.GetPopup());
		_colorPicker.GetPopup().AboutToPopup += () =>
		{
			_advancedColorButton.SetPressedNoSignal(false); SetAdvancedColors(false);
			WoodlandTheme.ApplyPopup(_colorPicker.GetPopup());
		};
		_colorPicker.ColorChanged += OnAccessoryColorChanged;
		_colorPicker.PopupClosed += SavePendingColor;
	}

	private void PopulateCatalog()
	{
		if (_wardrobe == null) return;
		var shelves = GetNode<VBoxContainer>("%AccessoryShelves");
		foreach (var row in shelves.GetChildren().OfType<AccessoryCategoryRow>()) _categoryRows[row.Category] = row;
		foreach (var category in AccessoryCategories.OrderedNames)
		{
			var items = _wardrobe.Catalog.Where(item => AccessoryCategories.For(item) == category).ToArray();
			if (!_categoryRows.TryGetValue(category, out var row))
			{
				if (CategoryRowScene == null) throw new InvalidOperationException("Assign the category row scene in the AccessoryEditor inspector.");
				row = CategoryRowScene.Instantiate<AccessoryCategoryRow>();
				row.Category = category; shelves.AddChild(row); _categoryRows.Add(category, row);
			}
			row.Configure(category, items.Length);
			foreach (var item in items) row.AddCard(CreateCard(item));
		}
		GetNode<Label>("%AccessoryCatalogHeading").Text = FormatCopy(CatalogHeadingFormat, "count", _wardrobe.Catalog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	public override void _ExitTree()
	{
		if (Engine.IsEditorHint()) return;
		SavePendingText();
		FinishTransform();
		SavePendingColor();
		if (_session != null)
		{
			_session.StateChanged -= Refresh;
			_session.OutfitSaved -= ShowSaveStatus;
			_session.EditStarting -= PrepareDesktopEdit;
		}
		if (_wardrobe != null)
		{
			_wardrobe.Changed -= Refresh;
		}
	}

	public void CancelInteraction()
	{
		SavePendingText();
		FinishTransform();
		SavePendingColor();
		_colorPicker?.GetPopup().Hide();
		_session?.CancelInteraction();
	}
	private void PrepareDesktopEdit()
	{
		// The desktop overlay does not take keyboard focus from the inspector.
		// End each inspector transaction before starting a gesture on the dog.
		SavePendingText(); FinishTransform(); SavePendingColor();
		_colorPicker?.GetPopup().Hide();
	}
	private void PrepareInspectorEdit() => _session?.CompleteGesture();
	private void MoveSelectedLayer(int direction)
	{
		if (_session?.Active != true || _session.SelectedId is not string id || _wardrobe == null) return;
		PrepareInspectorEdit(); PrepareDesktopEdit();
		if (_wardrobe.MoveLayer(id, direction)) ShowSaveStatus(_wardrobe.Save());
	}

	private void ApplyTransform()
	{
		if (_syncingTransform || _session?.SelectedId == null || _wardrobe == null) return;
		var id = _session.SelectedId;
		var scale = (float)_sizeSlider!.Value / 100;
		var rotation = (float)_rotationSlider!.Value;
		PrepareInspectorEdit();
		SavePendingText(); SavePendingColor();
		_wardrobe.SetTransform(id, scale, rotation);
		if (!_transformDragging) ShowSaveStatus(_wardrobe.Save());
	}
	private void FinishTransform()
	{
		if (!_transformDragging || _wardrobe == null) return;
		_transformDragging = false; _wardrobe.CommitEdit(); ShowSaveStatus(_wardrobe.Save());
	}
	private void SetAdvancedColors(bool advanced)
	{
		var picker = _colorPicker!.GetPicker();
		picker.ColorModesVisible = advanced; picker.SlidersVisible = advanced; picker.HexVisible = advanced;
		picker.SamplerVisible = advanced;
		_colorPicker.GetPopup().Size = Vector2I.Zero;
	}
	private void UndoOutfit()
	{
		CancelInteraction();
		if (_wardrobe?.Undo() == true) { _session?.ClearSelection(); ShowSaveStatus(_wardrobe.Save()); }
	}
	public override void _Process(double delta)
	{
		if (Engine.IsEditorHint()) return;
		if (_transformDragging && (!Input.IsMouseButtonPressed(MouseButton.Left) || !GetWindow().Visible || !GetWindow().HasFocus())) FinishTransform();
	}

	private void OnAccessoryColorChanged(Color color)
	{
		if (_syncingColorPicker || _wardrobe == null || _colorAccessoryId == null ||
			_wardrobe.Find(_colorAccessoryId)?.CanRecolor != true)
		{
			return;
		}
		PrepareInspectorEdit();
		FinishTransform();
		SavePendingText();
		// Preview every wheel movement, then save once when the picker closes.
		if (!_colorSavePending) _wardrobe.BeginEdit("color change");
		_colorSavePending = true;
		_wardrobe.SetTint(_colorAccessoryId, color);
	}

	private void SavePendingColor()
	{
		if (!_colorSavePending || _wardrobe == null)
		{
			return;
		}
		_colorSavePending = false;
		_wardrobe.CommitEdit();
		var success = _wardrobe.Save();
		ShowSaveStatus(success);
	}

	private void ShowSaveStatus(bool success)
	{
		if (_saveStatusLabel == null)
		{
			return;
		}
		_saveStatusLabel.Text = success ? OutfitSavedText : OutfitSaveFailedText;
		_saveStatusLabel.TooltipText = success ? OutfitSavedTooltip : OutfitSaveFailedTooltip;
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey key && HandleShortcut(key)) GetViewport().SetInputAsHandled();
	}
	private bool HandleShortcut(InputEventKey key)
	{
		if (_session?.Active != true || !IsVisibleInTree() || !GetWindow().Visible || !key.Pressed || key.Echo) return false;
		if (key.CtrlPressed && key.Keycode == Key.Z)
		{
			UndoOutfit(); return true;
		}
		else if (key.Keycode == Key.Escape)
		{
			CancelInteraction(); return true;
		}
		else if (key.Keycode == Key.Delete && _session?.SelectedId != null)
		{
			RemoveSelected();
			return true;
		}
		return false;
	}

	private Button CreateCard(AccessoryDefinition accessory)
	{
		if (CardScene == null) throw new InvalidOperationException("Assign the accessory card scene in the AccessoryEditor inspector.");
		var card = CardScene.Instantiate<AccessoryCard>();
		card.Configure(accessory, _wardrobe!.GetTint(accessory.Id));
		card.Pressed += () => _session?.ChooseAccessory(accessory.Id);
		_cards.Add(accessory.Id, card);
		_cardTextures.Add(accessory.Id, card.Preview);
		_cardStatuses.Add(accessory.Id, card.Status);
		return card;
	}

	private void RemoveSelected()
	{
		var selected = _session?.SelectedId;
		if (selected == null || _wardrobe == null)
		{
			return;
		}
		CancelInteraction();
		_wardrobe.Remove(selected);
		ShowSaveStatus(_wardrobe.Save());
		_session!.ClearSelection();
	}

	private void OnVisibilityChanged()
	{
		if (!IsVisibleInTree())
		{
			_session?.SetActive(false);
		}
	}

	private void Refresh()
	{
		if (_wardrobe == null || _session == null)
		{
			return;
		}
		var textSelected = _wardrobe.Find(_session.SelectedId ?? string.Empty)?.IsText == true;
		_textControls!.Visible = textSelected;
		if (textSelected) SyncTextInput();
		var colorAccessory = _session.SelectedId == null ? null : _wardrobe.Find(_session.SelectedId);
		var colorAccessoryId = colorAccessory is { CanRecolor: true } ? colorAccessory.Id : null;
		if (_colorAccessoryId != colorAccessoryId)
		{
			SavePendingColor();
			_colorPicker?.GetPopup().Hide();
			_colorAccessoryId = colorAccessoryId;
		}
		_colorControls!.Visible = colorAccessoryId != null;
		_colorLabel!.Text = textSelected ? TextColorCaption : ItemColorCaption;
		if (colorAccessoryId != null)
		{
			var color = _wardrobe.GetTint(colorAccessoryId);
			if (_colorPicker!.Color != color)
			{
				_syncingColorPicker = true;
				_colorPicker.Color = color;
				_syncingColorPicker = false;
			}
			_resetColorButton!.Disabled = color == Colors.White;
		}
		var equippedIds = new HashSet<string>();
		foreach (var placement in _wardrobe.Equipped)
		{
			equippedIds.Add(placement.Id);
		}
		string? selectedName = null;
		foreach (var accessory in _wardrobe.Catalog)
		{
			var selected = accessory.Id == _session.SelectedId;
			if (selected)
			{
				selectedName = accessory.Name;
			}
			_cards[accessory.Id].SetPressedNoSignal(selected);
			_cardTextures[accessory.Id].Modulate = _wardrobe.GetTint(accessory.Id);
			((AccessoryCard)_cards[accessory.Id]).RefreshStatus(selected && _session.IsPlacing, equippedIds.Contains(accessory.Id), accessory.IsText, _wardrobe.CanEquip(accessory.Id));
		}
		_selectionLabel!.Text = selectedName ?? NoSelectionText;
		_selectionLabel.TooltipText = FormatCopy(SelectionTooltipFormat, "name", _selectionLabel.Text);
		_removeButton!.Disabled = _session.SelectedId == null || !equippedIds.Contains(_session.SelectedId);
		_layerControls!.Visible = !_removeButton.Disabled;
		if (_session.SelectedId is string layerId && equippedIds.Contains(layerId))
		{
			_layerUpButton!.Disabled = !_wardrobe.CanMoveLayer(layerId, 1);
			_layerDownButton!.Disabled = !_wardrobe.CanMoveLayer(layerId, -1);
			_layerLabel!.Text = _wardrobe.GetLayerIndex(layerId) < _wardrobe.GetLayerIndex(AccessoryWardrobe.DogLayerId)
				? LayerBehindDogText : LayerInFrontOfDogText;
		}
		_clearButton!.Disabled = equippedIds.Count == 0;
		_removeButton.Visible = !_removeButton.Disabled;
		_undoButton!.Disabled = !_wardrobe.CanUndo;
		_undoButton.TooltipText = _wardrobe.CanUndo ? FormatCopy(UndoTooltipFormat, "label", _wardrobe.UndoLabel) : EmptyUndoTooltip;
		_transformControls!.Visible = _session.SelectedId != null;
		if (_session.SelectedId is string transformId)
		{
			_syncingTransform = true;
			_sizeSlider!.Value = _wardrobe.GetScale(transformId) * 100;
			_rotationSlider!.Value = _wardrobe.GetRotationDegrees(transformId);
			_sizeValueLabel!.Text = FormatCopy(SizeValueFormat, "value", Mathf.RoundToInt(_wardrobe.GetScale(transformId) * 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
			_rotationValueLabel!.Text = FormatCopy(RotationValueFormat, "value", Mathf.RoundToInt(_wardrobe.GetRotationDegrees(transformId)).ToString(System.Globalization.CultureInfo.InvariantCulture));
			_syncingTransform = false;
		}
		_cancelButton!.Visible = _session.IsPlacing || _session.IsDragging;
		var instructions = _session.IsPlacing
			? textSelected ? TextPlacementInstructions : ItemPlacementInstructionsFormat
			: _session.SelectedId != null ? SelectedInstructionsFormat : IdleInstructions;
		_instructions!.Text = FormatCopy(instructions, "name", selectedName ?? string.Empty);
	}
	private static string FormatCopy(string format, string token, string value) => (format ?? string.Empty).Replace("{" + token + "}", value);

	private void SyncTextInput()
	{
		if (_textInput == null || _wardrobe == null) return;
		var caret = _textInput.CaretColumn;
		_syncingText = true;
		if (_textInput.Text != _wardrobe.GetText()) { _textInput.Text = _wardrobe.GetText(); _textInput.CaretColumn = caret; }
		_textBackgroundCheck?.SetPressedNoSignal(_wardrobe.GetTextBackgroundVisible());
		_syncingText = false;
	}

	private void SavePendingText()
	{
		if (!_textEditing || _wardrobe == null) return;
		_textEditing = false; _wardrobe.CommitEdit(); ShowSaveStatus(_wardrobe.Save());
	}


}

/// <summary>All preview input stays in its cosmetic viewport; it never enqueues a pet grant.</summary>
public partial class AccessoryPreview : Control
{
	private const string DogTexturePath = "res://Sprites/Doggo.png";
	private static readonly Color Accent = WoodlandTheme.Accent;
	private AccessoryWardrobe? _wardrobe;
	private Texture2D? _dogTexture;
	private Rect2I _dogVisibleBounds;
	private readonly Dictionary<string, Image> _hitImages = new();
	private string? _placementId;
	private string? _draggedId;
	private Vector2 _dragOffset;
	private Vector2 _pointer;
	private bool _pointerInside;
	private StyleBox? _panelStyle;

	public string? SelectedId { get; private set; }
	public bool IsPlacing => _placementId != null;
	public bool IsDragging => _draggedId != null;
	public event Action? StateChanged;
	public event Action<bool>? OutfitSaved;
	public event Action? EditStarting;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Stop;
		FocusMode = FocusModeEnum.All;
		MouseDefaultCursorShape = CursorShape.Arrow;
		TextureFilter = CanvasItem.TextureFilterEnum.Linear;
		_panelStyle = WoodlandTheme.PanelStyle(true);
		_dogTexture = ResourceLoader.Load<Texture2D>(DogTexturePath);
		if (_dogTexture != null)
		{
			_dogVisibleBounds = AccessoryWardrobe.GetVisibleBounds(_dogTexture);
		}
		MouseEntered += () => { _pointerInside = true; QueueRedraw(); };
		MouseExited += () => { _pointerInside = false; QueueRedraw(); };
		Resized += QueueRedraw;
	}

	public void Configure(AccessoryWardrobe wardrobe)
	{
		_wardrobe = wardrobe;
		wardrobe.Changed += RefreshDogTexture;
		RefreshDogTexture();
	}
	private void RefreshDogTexture()
	{
		var texture = ResourceLoader.Load<Texture2D>(_wardrobe?.CurrentDogTexturePath ?? DogTexturePath);
		if (texture != _dogTexture && texture != null)
		{
			_dogTexture = texture;
			_dogVisibleBounds = AccessoryWardrobe.GetVisibleBounds(texture);
		}
		QueueRedraw();
	}

	public override void _ExitTree()
	{
		FinishDrag();
		if (_wardrobe != null)
		{
			_wardrobe.Changed -= RefreshDogTexture;
		}
		foreach (var image in _hitImages.Values)
		{
			image.Dispose();
		}
	}

	public override void _Process(double delta)
	{
		// A release outside the window, or a lost focus, must not leave an outfit unsaved.
		if (_draggedId != null && (!Input.IsMouseButtonPressed(MouseButton.Left) || !GetWindow().Visible || !GetWindow().HasFocus()))
		{
			FinishDrag();
		}
	}

	public void ChooseAccessory(string id)
	{
		if (_wardrobe?.Find(id) == null) return;
		FinishDrag();
		SelectedId = id;
		_placementId = _wardrobe?.GetPlacement(id) == null ? id : null;
		MouseDefaultCursorShape = IsPlacing ? CursorShape.Cross : CursorShape.Arrow;
		StateChanged?.Invoke();
		QueueRedraw();
	}

	public void ClearSelection()
	{
		FinishDrag();
		SelectedId = null;
		_placementId = null;
		MouseDefaultCursorShape = CursorShape.Arrow;
		StateChanged?.Invoke();
		QueueRedraw();
	}

	public void CancelInteraction()
	{
		if (_wardrobe != null && _draggedId != null)
		{
			_draggedId = null;
			_wardrobe.CancelEdit();
			OutfitSaved?.Invoke(_wardrobe.Save());
		}
		_placementId = null;
		if (SelectedId != null && _wardrobe?.GetPlacement(SelectedId) == null) SelectedId = null;
		MouseDefaultCursorShape = CursorShape.Arrow;
		StateChanged?.Invoke();
		QueueRedraw();
	}

	public override void _GuiInput(InputEvent @event)
	{
		if (_wardrobe == null || _dogTexture == null)
		{
			return;
		}
		if (@event is InputEventMouseMotion motion)
		{
			_pointer = motion.Position;
			if (_draggedId != null)
			{
				_wardrobe.Move(_draggedId, NormalizePoint(motion.Position - _dragOffset));
				AcceptEvent();
			}
			else if (_placementId == null)
			{
				MouseDefaultCursorShape = HitAccessory(motion.Position) != null ? CursorShape.Drag : CursorShape.Arrow;
			}
			QueueRedraw();
			return;
		}
		if (@event is not InputEventMouseButton button)
		{
			return;
		}
		if (button.ButtonIndex == MouseButton.Right && button.Pressed)
		{
			EditStarting?.Invoke();
			CancelInteraction();
			AcceptEvent();
			return;
		}
		if (button.ButtonIndex != MouseButton.Left)
		{
			return;
		}
		AcceptEvent();
		if (!button.Pressed)
		{
			FinishDrag();
			return;
		}
		EditStarting?.Invoke();
		GrabFocus();
		_pointer = button.Position;
		if (_placementId != null)
		{
			if (!PlacementArea(_placementId).HasPoint(button.Position))
			{
				return;
			}
			_draggedId = _placementId;
			_dragOffset = Vector2.Zero;
			_placementId = null;
			_wardrobe.BeginEdit("place accessory");
			_wardrobe.Equip(_draggedId, NormalizePoint(button.Position));
			MouseDefaultCursorShape = CursorShape.Drag;
			StateChanged?.Invoke();
			return;
		}
		var hit = HitAccessory(button.Position);
		SelectedId = hit?.Id;
		if (hit != null)
		{
			_wardrobe.BeginEdit("move accessory");
			_draggedId = hit.Id;
			var rect = DogRect();
			_dragOffset = button.Position - (rect.Position + hit.Position * rect.Size);
			MouseDefaultCursorShape = CursorShape.Drag;
		}
		StateChanged?.Invoke();
		QueueRedraw();
	}

	public override void _Draw()
	{
		if (_panelStyle != null)
		{
			DrawStyleBox(_panelStyle, new Rect2(Vector2.Zero, Size));
		}
		if (_dogTexture == null || _wardrobe == null)
		{
			return;
		}
		var dogRect = DogRect();
		DrawTextureRectRegion(_dogTexture, dogRect, new Rect2(_dogVisibleBounds.Position, _dogVisibleBounds.Size));
		foreach (var placement in _wardrobe.Equipped)
		{
			var definition = FindDefinition(placement.Id);
			if (definition == null)
			{
				continue;
			}
			DrawAccessory(definition, placement.Position, placement.Tint, placement.Scale, placement.RotationDegrees,
				placement.Id == SelectedId && _placementId == null);
		}
		if (_placementId != null && _pointerInside && PlacementArea(_placementId).HasPoint(_pointer))
		{
			var definition = FindDefinition(_placementId);
			if (definition != null)
			{
				var tint = _wardrobe.GetTint(definition.Id);
				tint.A = 0.65f;
				DrawAccessory(definition, NormalizePoint(_pointer), tint, _wardrobe.GetScale(definition.Id),
					_wardrobe.GetRotationDegrees(definition.Id), true);
				DrawCircle(_pointer, 3, Accent);
			}
		}
	}

	private void DrawAccessory(AccessoryDefinition definition, Vector2 position, Color tint, float scale, float rotation, bool selected)
	{
		var dogRect = DogRect();
		var center = dogRect.Position + dogRect.Size * position;
		var size = definition.Size * dogRect.Size.Y * scale;
		DrawSetTransform(center, Mathf.DegToRad(rotation), definition.IsText ? size / PetTextAccessory.ReferenceSize : Vector2.One);
		var localSize = definition.IsText ? PetTextAccessory.ReferenceSize : size;
		var localRect = new Rect2(-localSize * 0.5f, localSize);
		if (definition.IsText) PetTextAccessory.DrawTextBox(this, _wardrobe!.GetText(), tint, _wardrobe.GetTextBackgroundVisible());
		else DrawTextureRect(definition.Texture, localRect, false, tint);
		if (selected) DrawRect(localRect.Grow(4), Accent, false, 1.5f);
		DrawSetTransform(Vector2.Zero, 0, Vector2.One);
	}

	private void FinishDrag()
	{
		if (_draggedId == null)
		{
			return;
		}
		_draggedId = null;
		if (_wardrobe != null)
		{
			_wardrobe.CommitEdit();
			OutfitSaved?.Invoke(_wardrobe.Save());
		}
		StateChanged?.Invoke();
		QueueRedraw();
	}

	private Rect2 DogRect()
	{
		var sourceSize = new Vector2(Math.Max(1, _dogVisibleBounds.Size.X), Math.Max(1, _dogVisibleBounds.Size.Y));
		var overhang = Vector2.Zero;
		if (_wardrobe != null)
		{
			foreach (var definition in _wardrobe.Catalog)
			{
				if (definition.IsText) continue;
				var accessorySize = definition.Size * _wardrobe.GetScale(definition.Id);
				overhang = overhang.Max(AccessoryGeometry.Bounds(Vector2.Zero, accessorySize, _wardrobe.GetRotationDegrees(definition.Id)).Size);
			}
		}
		// Leave room for an accessory centered on any edge, even in a tall resized window.
		var paddedSource = new Rect2(-sourceSize.Y * overhang * 0.5f, sourceSize + sourceSize.Y * overhang);
		if (_wardrobe != null && (SelectedId == AccessoryWardrobe.TextAccessoryId || _wardrobe.GetPlacement(AccessoryWardrobe.TextAccessoryId) != null))
		{
			var id = AccessoryWardrobe.TextAccessoryId;
			var textSize = _wardrobe.Find(id)!.Size * sourceSize.Y * _wardrobe.GetScale(id);
			foreach (var anchor in new[] { AccessoryWardrobe.TextMinPosition, AccessoryWardrobe.TextMaxPosition,
				new Vector2(AccessoryWardrobe.TextMinPosition.X, AccessoryWardrobe.TextMaxPosition.Y), new Vector2(AccessoryWardrobe.TextMaxPosition.X, AccessoryWardrobe.TextMinPosition.Y) })
				paddedSource = paddedSource.Merge(AccessoryGeometry.Bounds(anchor * sourceSize, textSize, _wardrobe.GetRotationDegrees(id)));
		}
		var available = new Vector2(Math.Max(1, Size.X - 24), Math.Max(1, Size.Y - 24));
		var scale = Math.Min(available.X / paddedSource.Size.X, available.Y / paddedSource.Size.Y);
		var size = sourceSize * scale;
		return new Rect2((Size - paddedSource.Size * scale) * 0.5f - paddedSource.Position * scale, size);
	}
	private Rect2 PlacementArea(string id)
	{
		var dog = DogRect();
		return _wardrobe?.Find(id)?.IsText == true ? new Rect2(dog.Position + dog.Size * AccessoryWardrobe.TextMinPosition,
			dog.Size * (AccessoryWardrobe.TextMaxPosition - AccessoryWardrobe.TextMinPosition)) : dog;
	}

	private Vector2 NormalizePoint(Vector2 point)
	{
		var rect = DogRect();
		var normalized = (point - rect.Position) / rect.Size;
		return _wardrobe?.ClampPosition(_draggedId ?? _placementId ?? SelectedId ?? string.Empty, normalized) ?? normalized.Clamp(Vector2.Zero, Vector2.One);
	}

	private Rect2 AccessoryRect(AccessoryDefinition definition, Vector2 normalizedPosition)
	{
		var dogRect = DogRect();
		var size = definition.Size * dogRect.Size.Y * (_wardrobe?.GetScale(definition.Id) ?? 1);
		return AccessoryGeometry.Bounds(dogRect.Position + normalizedPosition * dogRect.Size, size,
			_wardrobe?.GetRotationDegrees(definition.Id) ?? 0);
	}

	private AccessoryPlacement? HitAccessory(Vector2 position)
	{
		if (_wardrobe == null)
		{
			return null;
		}
		for (var index = _wardrobe.Equipped.Count - 1; index >= 0; index--)
		{
			var placement = _wardrobe.Equipped[index];
			var definition = FindDefinition(placement.Id);
			if (definition == null)
			{
				continue;
			}
			var rect = AccessoryRect(definition, placement.Position);
			if (!rect.HasPoint(position))
			{
				continue;
			}
			if (!_hitImages.TryGetValue(placement.Id, out var image))
			{
				image = definition.Texture.GetImage();
				_hitImages[placement.Id] = image;
			}
			var dogRect = DogRect();
			var center = dogRect.Position + placement.Position * dogRect.Size;
			var size = definition.Size * dogRect.Size.Y * placement.Scale;
			var uv = (position - center).Rotated(-Mathf.DegToRad(placement.RotationDegrees)) / size + Vector2.One * 0.5f;
			if (uv.X < 0 || uv.Y < 0 || uv.X >= 1 || uv.Y >= 1) continue;
			if (definition.IsText) return placement;
			var x = Mathf.Clamp((int)(uv.X * image.GetWidth()), 0, image.GetWidth() - 1);
			var y = Mathf.Clamp((int)(uv.Y * image.GetHeight()), 0, image.GetHeight() - 1);
			if (image.GetPixel(x, y).A > 0.1f)
			{
				return placement;
			}
		}
		return null;
	}

	private AccessoryDefinition? FindDefinition(string id)
	{
		if (_wardrobe != null)
		{
			foreach (var definition in _wardrobe.Catalog)
			{
				if (definition.Id == id)
				{
					return definition;
				}
			}
		}
		return null;
	}

	private AccessoryPlacement? FindPlacement(string id)
	{
		if (_wardrobe != null)
		{
			foreach (var placement in _wardrobe.Equipped)
			{
				if (placement.Id == id)
				{
					return placement;
				}
			}
		}
		return null;
	}
}
