using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>Category shelves and an inspector for accessories on the real desktop dog.</summary>
public partial class AccessoryEditor : VBoxContainer
{
	private static readonly Color TextColor = WoodlandTheme.Text;
	private static readonly Color MutedColor = WoodlandTheme.Muted;
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
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		SizeFlagsVertical = SizeFlags.ExpandFill;
		AddThemeConstantOverride("separation", 9);
		_wardrobe = GetNodeOrNull<AccessoryWardrobe>("/root/AccessoryWardrobe");
		_session = GetNodeOrNull<AccessoryEditingSession>("/root/AccessoryEditingSession");
		if (_wardrobe == null || _session == null)
		{
			AddChild(MakeLabel("The accessory wardrobe is unavailable.", MutedColor));
			return;
		}

		_instructions = MakeLabel("Choose an item, then click your desktop dog to place it.\nDrag equipped items to move them; use the handles to resize or rotate.", MutedColor, 13);
		_instructions.CustomMinimumSize = new Vector2(0, 40);
		_instructions.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_instructions.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		var instructionsRow = new HBoxContainer();
		instructionsRow.AddThemeConstantOverride("separation", 9);
		instructionsRow.AddChild(MakeIcon("leaf", 22));
		instructionsRow.AddChild(_instructions);
		AddChild(instructionsRow);

		_session.StateChanged += Refresh;
		_session.OutfitSaved += ShowSaveStatus;
		_session.EditStarting += PrepareDesktopEdit;

		var workspace = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		workspace.AddThemeConstantOverride("separation", 12); AddChild(workspace);
		var inspectorPanel = new PanelContainer { CustomMinimumSize = new Vector2(164, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
		inspectorPanel.AddThemeStyleboxOverride("panel", WoodlandTheme.PanelStyle()); workspace.AddChild(inspectorPanel);
		var inspectorScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
		inspectorPanel.AddChild(inspectorScroll);
		_inspector = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		_inspector.AddThemeConstantOverride("separation", 10); inspectorScroll.AddChild(_inspector);
		_inspector.AddChild(MakeLabel("Adjust item", TextColor, 15));

		var actions = new HBoxContainer();
		actions.AddThemeConstantOverride("separation", 7);
		_selectionLabel = MakeLabel("No accessory selected", MutedColor, 14);
		_selectionLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_selectionLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_inspector.AddChild(_selectionLabel);
		_cancelButton = MakeButton("Cancel", "Cancel placement (Escape).");
		_cancelButton.Pressed += CancelInteraction;
		_inspector.AddChild(_cancelButton);
		_removeButton = MakeButton("Remove", "Remove the selected accessory (Delete).");
		_removeButton.Pressed += RemoveSelected;
		_inspector.AddChild(_removeButton);
		_clearButton = MakeButton("Clear all", "Take off every accessory.");
		_clearButton.Pressed += () =>
		{
			CancelInteraction();
			_wardrobe.Clear();
			ShowSaveStatus(_wardrobe.Save());
			_session.ClearSelection();
		};
		actions.AddChild(_clearButton);
		_undoButton = MakeButton("Undo", "Undo the last outfit change (Ctrl + Z).");
		_undoButton.Name = "UndoOutfit";
		_undoButton.Pressed += UndoOutfit;
		actions.AddChild(_undoButton);
		_inspector.AddChild(actions);
		_layerControls = new VBoxContainer { Name = "AccessoryLayerControls", Visible = false };
		_layerControls.AddThemeConstantOverride("separation", 5);
		_layerLabel = MakeLabel("Layers", MutedColor, 12);
		_layerLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		_layerControls.AddChild(_layerLabel);
		var layerButtons = new HBoxContainer();
		_layerUpButton = MakeButton("↑ Up", "Move the selected item forward one layer, including past the dog.");
		_layerUpButton.Name = "MoveAccessoryLayerUp";
		_layerUpButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_layerUpButton.Pressed += () => MoveSelectedLayer(1);
		_layerDownButton = MakeButton("↓ Down", "Move the selected item backward one layer, including behind the dog.");
		_layerDownButton.Name = "MoveAccessoryLayerDown";
		_layerDownButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_layerDownButton.Pressed += () => MoveSelectedLayer(-1);
		layerButtons.AddChild(_layerUpButton); layerButtons.AddChild(_layerDownButton);
		_layerControls.AddChild(layerButtons); _inspector.AddChild(_layerControls);

		_transformControls = new VBoxContainer { Name = "AccessoryTransformControls", Visible = false };
		var transformRow = new HBoxContainer();
		var transformSliders = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		transformSliders.AddThemeConstantOverride("separation", 10);
		transformSliders.AddChild(CreateTransformRow("Size", 50, 200, 1, out _sizeSlider, out _sizeValueLabel));
		transformSliders.AddChild(CreateTransformRow("Rotation", -180, 180, 1, out _rotationSlider, out _rotationValueLabel));
		transformRow.AddChild(transformSliders);
		var resetTransform = MakeButton("Reset fit", "Restore this accessory to 100% size and no rotation.");
		resetTransform.Name = "ResetAccessoryTransform";
		resetTransform.SizeFlagsVertical = SizeFlags.ShrinkCenter;
		resetTransform.Pressed += () =>
		{
			if (_session.SelectedId is string id) { PrepareInspectorEdit(); PrepareDesktopEdit(); _wardrobe.SetTransform(id, 1, 0); ShowSaveStatus(_wardrobe.Save()); }
		};
		_transformControls.AddChild(transformRow);
		_transformControls.AddChild(resetTransform);
		_inspector.AddChild(_transformControls);
		_textControls = new HBoxContainer { Name = "TextAccessoryControls", Visible = false };
		var textStack = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _textControls.AddChild(textStack);
		textStack.AddChild(MakeLabel("Text", TextColor, 13));
		_textInput = new LineEdit { Name = "AccessoryTextInput", PlaceholderText = "What should your dog say?",
			SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Up to 64 characters. Changes save automatically." };
		_textInput.TextChanged += text =>
		{
			if (_syncingText || _wardrobe == null) return;
			PrepareInspectorEdit();
			FinishTransform(); SavePendingColor();
			if (!_textEditing) { _wardrobe.BeginEdit("text change"); _textEditing = true; }
			_wardrobe.SetText(text); SyncTextInput(); ShowSaveStatus(_wardrobe.Save());
		};
		_textInput.FocusExited += SavePendingText;
		_textInput.TextSubmitted += _ => { SavePendingText(); _textInput.ReleaseFocus(); };
		textStack.AddChild(_textInput);
		_textBackgroundCheck = new CheckBox
		{
			Name = "TextBackgroundToggle",
			Text = "Text background",
			TooltipText = "Show a bubble behind the text. Turn it off to show only the letters."
		};
		_textBackgroundCheck.AddThemeFontSizeOverride("font_size", 12);
		_textBackgroundCheck.Toggled += visible =>
		{
			if (_syncingText || _wardrobe == null) return;
			PrepareInspectorEdit(); PrepareDesktopEdit();
			_wardrobe.SetTextBackgroundVisible(visible);
			ShowSaveStatus(_wardrobe.Save());
		};
		textStack.AddChild(_textBackgroundCheck); _inspector.AddChild(_textControls);

		_colorControls = new HBoxContainer { Name = "AccessoryColorControls", Visible = false };
		_colorControls.AddThemeConstantOverride("separation", 8);
		var colorStack = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _colorControls.AddChild(colorStack);
		_colorLabel = MakeLabel("Color", TextColor, 13); colorStack.AddChild(_colorLabel);
		_colorPicker = new ColorPickerButton
		{
			Name = "AccessoryColorPicker",
			Color = Colors.White,
			EditAlpha = false,
			CustomMinimumSize = new Vector2(72, 30),
			TooltipText = "Open the color wheel. Changes apply to this accessory immediately."
		};
		WoodlandTheme.StyleButton(_colorPicker, "SecondaryButton");
		_colorPicker.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		colorStack.AddChild(_colorPicker);
		_resetColorButton = MakeButton("Reset white", "Restore the accessory's original white color.");
		_resetColorButton.Name = "ResetAccessoryColor";
		_resetColorButton.Pressed += () =>
		{
			OnAccessoryColorChanged(Colors.White);
			SavePendingColor();
		};
		colorStack.AddChild(_resetColorButton);
		_inspector.AddChild(_colorControls);
		var picker = _colorPicker.GetPicker();
		picker.PickerShape = ColorPicker.PickerShapeType.HsvWheel;
		picker.EditAlpha = false;
		picker.DeferredMode = false;
		picker.EditIntensity = false;
		picker.PresetsVisible = false;
		picker.CanAddSwatches = false;
		var swatches = new HBoxContainer { Name = "QuickColors", Alignment = BoxContainer.AlignmentMode.Center };
		foreach (var (label, color) in new[] { ("White", Colors.White), ("Red", Color.FromHtml("#e35650")),
			("Orange", Color.FromHtml("#ff9b3f")), ("Yellow", Color.FromHtml("#f5d75c")),
			("Green", Color.FromHtml("#62af76")), ("Blue", Color.FromHtml("#5b9bda")),
			("Purple", Color.FromHtml("#a176d6")), ("Pink", Color.FromHtml("#eb91ba")) })
		{
			var swatch = new Button { CustomMinimumSize = new Vector2(26, 26), TooltipText = label };
			var skin = new StyleBoxFlat { BgColor = color, BorderColor = WoodlandTheme.Border };
			skin.SetCornerRadiusAll(5); skin.SetBorderWidthAll(1);
			foreach (var state in new[] { "normal", "hover", "pressed" }) swatch.AddThemeStyleboxOverride(state, skin);
			swatch.Pressed += () => { _colorPicker.Color = color; OnAccessoryColorChanged(color); };
			swatches.AddChild(swatch);
		}
		picker.AddChild(swatches);
		var colorActions = new HBoxContainer();
		_advancedColorButton = MakeButton("Advanced", "Show numeric color controls.");
		_advancedColorButton.ToggleMode = true;
		_advancedColorButton.Toggled += SetAdvancedColors;
		colorActions.AddChild(_advancedColorButton);
		var popupReset = MakeButton("Reset", "Restore the accessory's white color.");
		popupReset.Pressed += () => { _colorPicker.Color = Colors.White; OnAccessoryColorChanged(Colors.White); };
		colorActions.AddChild(popupReset);
		var done = MakeButton("Done", "Keep this color and close the picker.");
		done.Pressed += () => _colorPicker.GetPopup().Hide();
		colorActions.AddChild(done);
		picker.AddChild(colorActions);
		SetAdvancedColors(false);
		WoodlandTheme.ApplyPopup(_colorPicker.GetPopup());
		_colorPicker.GetPopup().AboutToPopup += () =>
		{
			_advancedColorButton.SetPressedNoSignal(false);
			SetAdvancedColors(false);
			WoodlandTheme.ApplyPopup(_colorPicker.GetPopup());
		};
		_colorPicker.ColorChanged += OnAccessoryColorChanged;
		_colorPicker.PopupClosed += SavePendingColor;

		var catalogColumn = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
		workspace.AddChild(catalogColumn);
		var catalogHeading = new HBoxContainer();
		catalogHeading.AddThemeConstantOverride("separation", 7);
		catalogHeading.AddChild(MakeIcon("leaf", 18));
		catalogHeading.AddChild(MakeLabel($"Accessories · {_wardrobe.Catalog.Count}", TextColor, 15));
		catalogHeading.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
		catalogColumn.AddChild(catalogHeading);
		_saveStatusLabel = MakeLabel("Outfit saves automatically", MutedColor, 12);
		_saveStatusLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		_saveStatusLabel.HorizontalAlignment = HorizontalAlignment.Left;
		_saveStatusLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

		_catalogScroll = new ScrollContainer
		{
			CustomMinimumSize = new Vector2(0, 160),
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
		};
		_catalogScroll.AddThemeStyleboxOverride("panel", WoodlandTheme.CatalogStyle());
		var shelves = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		shelves.AddThemeConstantOverride("separation", 10); _catalogScroll.AddChild(shelves); catalogColumn.AddChild(_catalogScroll);
		foreach (var category in AccessoryCategories.OrderedNames)
		{
			var items = _wardrobe.Catalog.Where(item => AccessoryCategories.For(item) == category).ToArray();
			var shelf = new AccessoryCategoryRow(); shelf.Configure(category, items.Length); shelves.AddChild(shelf);
			foreach (var item in items) shelf.AddCard(CreateCard(item));
			if (items.Length == 0) shelf.Expanded = false;
			_categoryRows.Add(category, shelf);
		}
		catalogColumn.AddChild(_saveStatusLabel);
		VisibilityChanged += OnVisibilityChanged;
		_wardrobe.Changed += Refresh;
		Refresh();
	}

	public override void _ExitTree()
	{
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

	private Control CreateTransformRow(string label, double min, double max, double step, out Slider slider, out Label valueLabel)
	{
		var row = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddThemeConstantOverride("separation", 6);
		var title = MakeLabel(label, TextColor, 12); title.HorizontalAlignment = HorizontalAlignment.Center; row.AddChild(title);
		valueLabel = MakeLabel(string.Empty, MutedColor, 12); valueLabel.HorizontalAlignment = HorizontalAlignment.Center; row.AddChild(valueLabel);
		slider = new VSlider { MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
			CustomMinimumSize = new Vector2(28, 190) };
		slider.DragStarted += () => { PrepareInspectorEdit(); SavePendingText(); SavePendingColor(); _transformDragging = true; _wardrobe?.BeginEdit("size / rotation"); };
		slider.DragEnded += _ => FinishTransform();
		slider.ValueChanged += _ => ApplyTransform(); row.AddChild(slider);
		return row;
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
		_saveStatusLabel.Text = success ? "Outfit saved" : "Could not save outfit";
		_saveStatusLabel.TooltipText = success ? "This outfit will return when you restart." : "Your outfit is applied for this session. Try moving an accessory to save again.";
		_saveStatusLabel.AddThemeColorOverride("font_color", success ? MutedColor : WoodlandTheme.Accent);
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
		var card = MakeButton(string.Empty, $"Choose {accessory.Name}. Click the desktop dog to place it, or drag an equipped item directly.", "CardButton");
		card.ToggleMode = true;
		card.CustomMinimumSize = new Vector2(122, 112);
		card.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
		var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
		margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		margin.AddThemeConstantOverride("margin_left", 7);
		margin.AddThemeConstantOverride("margin_right", 7);
		margin.AddThemeConstantOverride("margin_top", 7);
		margin.AddThemeConstantOverride("margin_bottom", 6);
		card.AddChild(margin);
		var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
		content.AddThemeConstantOverride("separation", 3);
		margin.AddChild(content);
		var image = new TextureRect
		{
			Texture = accessory.Texture,
			Modulate = _wardrobe!.GetTint(accessory.Id),
			TextureFilter = CanvasItem.TextureFilterEnum.Linear,
			MouseFilter = MouseFilterEnum.Ignore,
			CustomMinimumSize = new Vector2(0, 51),
			SizeFlagsVertical = SizeFlags.ExpandFill,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
		};
		content.AddChild(image);
		_cardTextures.Add(accessory.Id, image);
		var name = MakeLabel(accessory.Name, TextColor, 12);
		name.HorizontalAlignment = HorizontalAlignment.Center;
		name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
		content.AddChild(name);
		var status = MakeLabel("Click to place", MutedColor, 11);
		status.HorizontalAlignment = HorizontalAlignment.Center;
		content.AddChild(status);
		card.Pressed += () => _session?.ChooseAccessory(accessory.Id);
		_cards.Add(accessory.Id, card);
		_cardStatuses.Add(accessory.Id, status);
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
		_colorLabel!.Text = textSelected ? "Text color" : "Color";
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
			_cardStatuses[accessory.Id].Text = selected && _session.IsPlacing ? accessory.IsText ? "Place near dog" : "Click the dog" : equippedIds.Contains(accessory.Id) ? "Equipped" : "Click to place";
			_cardStatuses[accessory.Id].AddThemeColorOverride("font_color", equippedIds.Contains(accessory.Id) ? WoodlandTheme.Moss : MutedColor);
		}
		_selectionLabel!.Text = selectedName == null ? "No accessory selected" : selectedName;
		_selectionLabel.TooltipText = _selectionLabel.Text;
		_selectionLabel.AddThemeColorOverride("font_color", selectedName == null ? MutedColor : TextColor);
		_removeButton!.Disabled = _session.SelectedId == null || !equippedIds.Contains(_session.SelectedId);
		_layerControls!.Visible = !_removeButton.Disabled;
		if (_session.SelectedId is string layerId && equippedIds.Contains(layerId))
		{
			_layerUpButton!.Disabled = !_wardrobe.CanMoveLayer(layerId, 1);
			_layerDownButton!.Disabled = !_wardrobe.CanMoveLayer(layerId, -1);
			_layerLabel!.Text = _wardrobe.GetLayerIndex(layerId) < _wardrobe.GetLayerIndex(AccessoryWardrobe.DogLayerId)
				? "Layers · Behind dog" : "Layers · In front of dog";
		}
		_clearButton!.Disabled = equippedIds.Count == 0;
		_removeButton.Visible = !_removeButton.Disabled;
		_undoButton!.Disabled = !_wardrobe.CanUndo;
		_undoButton.TooltipText = _wardrobe.CanUndo ? $"Undo {_wardrobe.UndoLabel} (Ctrl + Z)." : "No outfit changes to undo.";
		_transformControls!.Visible = _session.SelectedId != null;
		if (_session.SelectedId is string transformId)
		{
			_syncingTransform = true;
			_sizeSlider!.Value = _wardrobe.GetScale(transformId) * 100;
			_rotationSlider!.Value = _wardrobe.GetRotationDegrees(transformId);
			_sizeValueLabel!.Text = $"{Mathf.RoundToInt(_wardrobe.GetScale(transformId) * 100)}%";
			_rotationValueLabel!.Text = $"{Mathf.RoundToInt(_wardrobe.GetRotationDegrees(transformId))}°";
			_syncingTransform = false;
		}
		_cancelButton!.Visible = _session.IsPlacing || _session.IsDragging;
		_instructions!.Text = _session.IsPlacing
			? textSelected ? "Click near your desktop dog to place the text.\nEdit its message, color and background in the side panel."
			: $"Click your desktop dog to place {selectedName}.\nPress Escape or Cancel to choose something else."
			: _session.SelectedId != null ? $"Drag {selectedName} to move it; use its handles to resize or rotate.\nRight-click it or use the layer arrows to change what appears in front."
			: "Choose an item, then click your desktop dog to place it.\nDrag the dog to move it; right-click the dog or an item for layer options.";
	}
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

	private static Label MakeLabel(string text, Color color, int size = 14)
	{
		var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
		label.AddThemeColorOverride("font_color", color);
		label.AddThemeFontSizeOverride("font_size", size);
		return label;
	}

	private static TextureRect MakeIcon(string name, int size)
	{
		return new TextureRect
		{
			Texture = WoodlandTheme.Icon(name),
			CustomMinimumSize = new Vector2(size, size),
			SizeFlagsVertical = SizeFlags.ShrinkCenter,
			MouseFilter = MouseFilterEnum.Ignore,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
		};
	}

	private static Button MakeButton(string text, string tooltip, string variation = "SecondaryButton")
	{
		var button = new Button { Text = text, TooltipText = tooltip, CustomMinimumSize = new Vector2(0, 30) };
		WoodlandTheme.StyleButton(button, variation);
		button.AddThemeFontSizeOverride("font_size", 13);
		if (text is "Remove" or "Reset white")
		{
			button.Icon = WoodlandTheme.Icon(text == "Remove" ? "remove" : "reset");
			button.AddThemeConstantOverride("icon_max_width", 14);
		}
		return button;
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
		wardrobe.Changed += QueueRedraw;
		QueueRedraw();
	}

	public override void _ExitTree()
	{
		FinishDrag();
		if (_wardrobe != null)
		{
			_wardrobe.Changed -= QueueRedraw;
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
