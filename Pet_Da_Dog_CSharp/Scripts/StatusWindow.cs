using System;
using System.Globalization;
using Godot;

public partial class StatusWindow : Window
{
	private static readonly Vector2I BaseWindowSize = new(560, 540);
	private static readonly Vector2I BaseMinimumWindowSize = new(500, 460);

	private static readonly Color BackgroundColor = Color.FromHtml("#f4f6f8");
	private static readonly Color PanelColor = Color.FromHtml("#ffffff");
	private static readonly Color BorderColor = Color.FromHtml("#cfd7e3");
	private static readonly Color TextColor = Color.FromHtml("#152033");
	private static readonly Color MutedTextColor = Color.FromHtml("#657386");
	private static readonly Color AccentColor = Color.FromHtml("#2f6fdd");
	private const string AccessoriesTabTitle = "Accessories";
	private const string DiscoveriesTabTitle = "Discoveries";
	private const string SettingsTabTitle = "Settings";
	private const int PlaceholderTileCount = 16;

	private enum StatusTab
	{
		Accessories,
		Discoveries,
		Settings
	}

	private PetSettings? _settings;
	private bool _refreshingControls;
	private StatusTab _activeTab = StatusTab.Accessories;

	private Label? _petsValueLabel;
	private Label? _pendingLabel;
	private Label? _backendStatusLabel;
	private Label? _steamStatusLabel;
	private Button? _accessoriesTabButton;
	private Button? _discoveriesTabButton;
	private Button? _settingsTabButton;
	private Control? _accessoriesPage;
	private Control? _discoveriesPage;
	private Control? _settingsPage;
	private CheckBox? _alwaysOnTopCheck;
	private CheckBox? _dogClickThroughCheck;
	private HSlider? _dogTransparencySlider;
	private SpinBox? _dogTransparencySpin;
	private HSlider? _dogScaleSlider;
	private SpinBox? _dogScaleSpin;
	private HSlider? _uiScaleSlider;
	private SpinBox? _uiScaleSpin;
	private Label? _dogTransparencyValueLabel;
	private Label? _dogScaleValueLabel;
	private Label? _uiScaleValueLabel;

	public override void _Ready()
	{
		Title = "Pet Da Dog Status";
		Size = BaseWindowSize;
		MinSize = BaseMinimumWindowSize;
		InitialPosition = WindowInitialPosition.CenterMainWindowScreen;
		AlwaysOnTop = false;
		Borderless = false;
		Transparent = false;
		TransparentBg = false;
		Unresizable = false;
		Visible = false;

		CloseRequested += Hide;

		BuildUi();
		RefreshControls();
		ApplyUiScale();
	}

	public override void _ExitTree()
	{
		if (_settings != null)
		{
			_settings.SettingsChanged -= OnSettingsChanged;
		}
	}

	public void Configure(PetSettings? settings)
	{
		if (_settings != null)
		{
			_settings.SettingsChanged -= OnSettingsChanged;
		}

		_settings = settings;

		if (_settings != null)
		{
			_settings.SettingsChanged += OnSettingsChanged;
		}

		RefreshControls();
		ApplyUiScale();
	}

	public void ShowStatusWindow()
	{
		RefreshControls();
		ApplyUiScale();

		PlaceWindowAboveDogPath();
		Show();
		GrabFocus();
	}

	public void UpdateStatus(long? confirmedPets, int pendingGrantCount, string backendStatus, string steamStatus)
	{
		if (_petsValueLabel != null)
		{
			_petsValueLabel.Text = confirmedPets.HasValue
				? $"Pets: {confirmedPets.Value.ToString("N0", CultureInfo.InvariantCulture)}"
				: "Pets: Syncing...";
		}

		if (_pendingLabel != null)
		{
			_pendingLabel.Visible = pendingGrantCount > 0;
			_pendingLabel.Text = pendingGrantCount > 0
				? $"Pending: {pendingGrantCount}"
				: string.Empty;
		}

		if (_backendStatusLabel != null)
		{
			_backendStatusLabel.Text = $"Backend: {NormalizeStatus(backendStatus)}";
		}

		if (_steamStatusLabel != null)
		{
			_steamStatusLabel.Text = $"Steam: {NormalizeStatus(steamStatus)}";
		}
	}

	private void BuildUi()
	{
		var background = new PanelContainer();
		background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		background.AddThemeStyleboxOverride("panel", CreateStyleBox(BackgroundColor, BackgroundColor, 0, 0));
		AddChild(background);

		var margin = new MarginContainer();
		margin.AddThemeConstantOverride("margin_left", 18);
		margin.AddThemeConstantOverride("margin_top", 18);
		margin.AddThemeConstantOverride("margin_right", 18);
		margin.AddThemeConstantOverride("margin_bottom", 18);
		background.AddChild(margin);

		var root = new VBoxContainer();
		root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		root.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
		root.AddThemeConstantOverride("separation", 12);
		margin.AddChild(root);

		root.AddChild(CreateTopBar());
		root.AddChild(CreateTabPages());
	}

	private Control CreateTopBar()
	{
		var row = new HBoxContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0, 44)
		};
		row.AddThemeConstantOverride("separation", 12);

		var tabs = new HBoxContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
		};
		tabs.AddThemeConstantOverride("separation", 8);

		_accessoriesTabButton = CreateTabButton(AccessoriesTabTitle, StatusTab.Accessories);
		_discoveriesTabButton = CreateTabButton(DiscoveriesTabTitle, StatusTab.Discoveries);
		_settingsTabButton = CreateTabButton(SettingsTabTitle, StatusTab.Settings);

		tabs.AddChild(_accessoriesTabButton);
		tabs.AddChild(_discoveriesTabButton);
		tabs.AddChild(_settingsTabButton);
		row.AddChild(tabs);

		var pets = new VBoxContainer
		{
			SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
			CustomMinimumSize = new Vector2(148, 0)
		};
		pets.AddThemeConstantOverride("separation", 1);

		_petsValueLabel = CreateLabel("Pets: Syncing...", TextColor, 16);
		_petsValueLabel.HorizontalAlignment = HorizontalAlignment.Right;
		pets.AddChild(_petsValueLabel);

		_pendingLabel = CreateLabel(string.Empty, MutedTextColor, 12);
		_pendingLabel.HorizontalAlignment = HorizontalAlignment.Right;
		_pendingLabel.Visible = false;
		pets.AddChild(_pendingLabel);

		row.AddChild(pets);

		return row;
	}

	private Control CreateTabPages()
	{
		var pages = new VBoxContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill
		};

		_accessoriesPage = CreateTilePage("Accessory");
		_discoveriesPage = CreateTilePage("Discovery");
		_settingsPage = CreateSettingsTab();

		pages.AddChild(_accessoriesPage);
		pages.AddChild(_discoveriesPage);
		pages.AddChild(_settingsPage);

		SetActiveTab(_activeTab);
		return pages;
	}

	private Control CreateTilePage(string tilePrefix)
	{
		var scroll = CreateScrollPage(out var content);
		var grid = new GridContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Columns = 4
		};
		grid.AddThemeConstantOverride("h_separation", 10);
		grid.AddThemeConstantOverride("v_separation", 10);

		for (var index = 1; index <= PlaceholderTileCount; index++)
		{
			grid.AddChild(CreateTileButton($"{tilePrefix} {index:00}"));
		}

		content.AddChild(grid);

		return scroll;
	}

	private Control CreateSettingsTab()
	{
		var scroll = CreateScrollPage(out var content);
		content.AddChild(CreateConnectionPanel());
		content.AddChild(CreateSettingsPanel());
		return scroll;
	}

	private static ScrollContainer CreateScrollPage(out VBoxContainer content)
	{
		var scroll = new ScrollContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
		};

		content = new VBoxContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill
		};
		content.AddThemeConstantOverride("separation", 12);
		scroll.AddChild(content);

		return scroll;
	}

	private static Button CreateTileButton(string text)
	{
		var button = new Button
		{
			Text = text,
			CustomMinimumSize = new Vector2(96, 96),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		StyleButton(button);
		return button;
	}

	private Button CreateTabButton(string text, StatusTab tab)
	{
		var button = new Button
		{
			Text = text,
			ToggleMode = true,
			CustomMinimumSize = new Vector2(118, 36)
		};
		StyleButton(button);
		button.Pressed += () => SetActiveTab(tab);
		return button;
	}

	private void SetActiveTab(StatusTab tab)
	{
		_activeTab = tab;
		var accessoriesSelected = tab == StatusTab.Accessories;
		var discoveriesSelected = tab == StatusTab.Discoveries;
		var settingsSelected = tab == StatusTab.Settings;

		if (_accessoriesTabButton != null)
		{
			_accessoriesTabButton.ButtonPressed = accessoriesSelected;
		}

		if (_discoveriesTabButton != null)
		{
			_discoveriesTabButton.ButtonPressed = discoveriesSelected;
		}

		if (_settingsTabButton != null)
		{
			_settingsTabButton.ButtonPressed = settingsSelected;
		}

		if (_accessoriesPage != null)
		{
			_accessoriesPage.Visible = accessoriesSelected;
		}

		if (_discoveriesPage != null)
		{
			_discoveriesPage.Visible = discoveriesSelected;
		}

		if (_settingsPage != null)
		{
			_settingsPage.Visible = settingsSelected;
		}
	}

	private Control CreateConnectionPanel()
	{
		var (panel, content) = CreatePanel();
		AddSectionTitle(content, "Connection");

		_backendStatusLabel = CreateStatusLabel("Backend: Starting");
		_steamStatusLabel = CreateStatusLabel("Steam: Starting");
		content.AddChild(_backendStatusLabel);
		content.AddChild(_steamStatusLabel);

		return panel;
	}

	private Control CreateSettingsPanel()
	{
		var (panel, content) = CreatePanel();
		AddSectionTitle(content, "Dog Settings");

		_alwaysOnTopCheck = CreateCheckBox("Always On Top");
		_alwaysOnTopCheck.Toggled += value =>
		{
			if (!_refreshingControls)
			{
				_settings?.SetAlwaysOnTop(value);
			}
		};
		content.AddChild(_alwaysOnTopCheck);

		_dogClickThroughCheck = CreateCheckBox("Doggo Click Through (Alt + `)");
		_dogClickThroughCheck.Toggled += value =>
		{
			if (!_refreshingControls)
			{
				_settings?.SetDogClickThrough(value);
			}
		};
		content.AddChild(_dogClickThroughCheck);

		content.AddChild(CreateSliderBlock(
			"Doggo Transparency",
			PetSettings.MinDogTransparency,
			PetSettings.MaxDogTransparency,
			0.01,
			out _dogTransparencySlider,
			out _dogTransparencySpin,
			out _dogTransparencyValueLabel,
			value => _settings?.SetDogTransparency(value)));

		content.AddChild(CreateSliderBlock(
			"Dog Scale",
			PetSettings.MinDogScale,
			PetSettings.MaxDogScale,
			0.01,
			out _dogScaleSlider,
			out _dogScaleSpin,
			out _dogScaleValueLabel,
			value => _settings?.SetDogScale(value)));

		content.AddChild(CreateSliderBlock(
			"UI Scale",
			PetSettings.MinUiScale,
			PetSettings.MaxUiScale,
			0.01,
			out _uiScaleSlider,
			out _uiScaleSpin,
			out _uiScaleValueLabel,
			value => _settings?.SetUiScale(value)));

		return panel;
	}

	private static (PanelContainer Panel, VBoxContainer Content) CreatePanel()
	{
		var panel = new PanelContainer();
		panel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		panel.AddThemeStyleboxOverride("panel", CreateStyleBox(PanelColor, BorderColor, 1, 8));

		var margin = new MarginContainer();
		margin.AddThemeConstantOverride("margin_left", 14);
		margin.AddThemeConstantOverride("margin_top", 12);
		margin.AddThemeConstantOverride("margin_right", 14);
		margin.AddThemeConstantOverride("margin_bottom", 12);
		panel.AddChild(margin);

		var content = new VBoxContainer();
		content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		content.AddThemeConstantOverride("separation", 8);
		margin.AddChild(content);

		return (panel, content);
	}

	private static Control CreateValueRow(string labelText, out Label valueLabel, string initialValue)
	{
		var row = new HBoxContainer();
		row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		row.AddThemeConstantOverride("separation", 12);

		var label = CreateLabel(labelText, MutedTextColor);
		label.CustomMinimumSize = new Vector2(150, 0);
		row.AddChild(label);

		valueLabel = CreateLabel(initialValue, TextColor, 18);
		valueLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		row.AddChild(valueLabel);

		return row;
	}

	private static Label CreateStatusLabel(string text)
	{
		var label = CreateLabel(text, MutedTextColor);
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		return label;
	}

	private static Label CreateLabel(string text, Color color, int fontSize = 14)
	{
		var label = new Label
		{
			Text = text,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		label.AddThemeColorOverride("font_color", color);
		label.AddThemeFontSizeOverride("font_size", fontSize);
		return label;
	}

	private static void AddSectionTitle(VBoxContainer root, string text)
	{
		root.AddChild(CreateLabel(text, TextColor, 16));
	}

	private static CheckBox CreateCheckBox(string text)
	{
		var checkBox = new CheckBox
		{
			Text = text,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		checkBox.AddThemeColorOverride("font_color", TextColor);
		checkBox.AddThemeColorOverride("font_hover_color", TextColor);
		checkBox.AddThemeColorOverride("font_pressed_color", TextColor);
		checkBox.AddThemeColorOverride("font_focus_color", TextColor);
		checkBox.AddThemeColorOverride("font_disabled_color", MutedTextColor);
		checkBox.AddThemeFontSizeOverride("font_size", 14);
		return checkBox;
	}

	private static void StyleButton(Button button)
	{
		button.AddThemeColorOverride("font_color", TextColor);
		button.AddThemeColorOverride("font_hover_color", TextColor);
		button.AddThemeColorOverride("font_pressed_color", TextColor);
		button.AddThemeColorOverride("font_focus_color", TextColor);
		button.AddThemeColorOverride("font_disabled_color", MutedTextColor);
		button.AddThemeFontSizeOverride("font_size", 14);
		button.AddThemeStyleboxOverride("normal", CreateStyleBox(PanelColor, BorderColor, 1, 6));
		button.AddThemeStyleboxOverride("hover", CreateStyleBox(Color.FromHtml("#eef4ff"), AccentColor, 1, 6));
		button.AddThemeStyleboxOverride("pressed", CreateStyleBox(Color.FromHtml("#dceaff"), AccentColor, 1, 6));
		button.AddThemeStyleboxOverride("focus", CreateStyleBox(Color.FromHtml("#ffffff"), AccentColor, 1, 6));
	}

	private Control CreateSliderBlock(
		string labelText,
		double min,
		double max,
		double step,
		out HSlider slider,
		out SpinBox spinBox,
		out Label valueLabel,
		Action<float> applyValue)
	{
		var block = new VBoxContainer();
		block.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		block.AddThemeConstantOverride("separation", 5);

		var heading = new HBoxContainer();
		heading.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		heading.AddThemeConstantOverride("separation", 10);
		block.AddChild(heading);

		heading.AddChild(CreateLabel(labelText, TextColor));

		valueLabel = CreateLabel(string.Empty, MutedTextColor);
		valueLabel.HorizontalAlignment = HorizontalAlignment.Right;
		valueLabel.CustomMinimumSize = new Vector2(70, 0);
		heading.AddChild(valueLabel);

		var controls = new HBoxContainer();
		controls.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		controls.AddThemeConstantOverride("separation", 12);
		block.AddChild(controls);

		slider = new HSlider
		{
			MinValue = min,
			MaxValue = max,
			Step = step,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(240, 28)
		};
		slider.ValueChanged += value =>
		{
			if (!_refreshingControls)
			{
				applyValue((float)value);
			}
		};
		controls.AddChild(slider);

		spinBox = new SpinBox
		{
			MinValue = min,
			MaxValue = max,
			Step = step,
			CustomMinimumSize = new Vector2(92, 30)
		};
		StyleSpinBox(spinBox);
		spinBox.ValueChanged += value =>
		{
			if (!_refreshingControls)
			{
				applyValue((float)value);
			}
		};
		controls.AddChild(spinBox);

		return block;
	}

	private static void StyleSpinBox(SpinBox spinBox)
	{
		spinBox.AddThemeColorOverride("font_color", TextColor);
		spinBox.AddThemeFontSizeOverride("font_size", 14);

		var lineEdit = spinBox.GetLineEdit();
		lineEdit.AddThemeColorOverride("font_color", TextColor);
		lineEdit.AddThemeColorOverride("caret_color", AccentColor);
		lineEdit.AddThemeStyleboxOverride("normal", CreateStyleBox(Color.FromHtml("#ffffff"), BorderColor, 1, 5));
		lineEdit.AddThemeStyleboxOverride("focus", CreateStyleBox(Color.FromHtml("#ffffff"), AccentColor, 1, 5));
	}

	private void RefreshControls()
	{
		if (_settings == null)
		{
			return;
		}

		_refreshingControls = true;

		if (_alwaysOnTopCheck != null)
		{
			_alwaysOnTopCheck.ButtonPressed = _settings.AlwaysOnTop;
		}

		if (_dogClickThroughCheck != null)
		{
			_dogClickThroughCheck.ButtonPressed = _settings.DogClickThrough;
		}

		SetPairValue(_dogTransparencySlider, _dogTransparencySpin, _dogTransparencyValueLabel, _settings.DogTransparency);
		SetPairValue(_dogScaleSlider, _dogScaleSpin, _dogScaleValueLabel, _settings.DogScale);
		SetPairValue(_uiScaleSlider, _uiScaleSpin, _uiScaleValueLabel, _settings.UiScale);

		_refreshingControls = false;
	}

	private void ApplyUiScale()
	{
		var uiScale = _settings?.UiScale ?? PetSettings.DefaultUiScale;
		Theme = new Theme
		{
			DefaultBaseScale = uiScale,
			DefaultFontSize = Mathf.RoundToInt(14 * uiScale)
		};

		var desiredSize = new Vector2I(
			Mathf.RoundToInt(BaseWindowSize.X * uiScale),
			Mathf.RoundToInt(BaseWindowSize.Y * uiScale));
		var desiredMinimumSize = new Vector2I(
			Mathf.RoundToInt(BaseMinimumWindowSize.X * uiScale),
			Mathf.RoundToInt(BaseMinimumWindowSize.Y * uiScale));

		if (!Engine.IsEmbeddedInEditor())
		{
			var usableRect = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary);
			var maxSize = new Vector2I(
				Mathf.Max(BaseMinimumWindowSize.X, usableRect.Size.X - 48),
				Mathf.Max(BaseMinimumWindowSize.Y, usableRect.Size.Y - 220)
			);

			desiredSize = new Vector2I(
				Mathf.Min(desiredSize.X, maxSize.X),
				Mathf.Min(desiredSize.Y, maxSize.Y)
			);
		}

		Size = desiredSize;
		MinSize = new Vector2I(
			Mathf.Min(desiredMinimumSize.X, Size.X),
			Mathf.Min(desiredMinimumSize.Y, Size.Y)
		);
	}

	private void PlaceWindowAboveDogPath()
	{
		if (Engine.IsEmbeddedInEditor())
		{
			return;
		}

		var usableRect = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary);
		const int margin = 24;
		var x = Mathf.Max(usableRect.Position.X + margin, usableRect.End.X - Size.X - margin);
		var y = usableRect.Position.Y + margin;
		Position = new Vector2I(x, y);
	}

	private void OnSettingsChanged()
	{
		RefreshControls();
		ApplyUiScale();
	}

	private static void SetPairValue(HSlider? slider, SpinBox? spinBox, Label? valueLabel, float value)
	{
		if (slider != null)
		{
			slider.Value = value;
		}

		if (spinBox != null)
		{
			spinBox.Value = value;
		}

		if (valueLabel != null)
		{
			valueLabel.Text = value.ToString("0.00", CultureInfo.InvariantCulture);
		}
	}

	private static StyleBoxFlat CreateStyleBox(Color background, Color border, int borderWidth, int cornerRadius)
	{
		var style = new StyleBoxFlat
		{
			BgColor = background,
			BorderColor = border
		};
		style.SetBorderWidthAll(borderWidth);
		style.SetCornerRadiusAll(cornerRadius);
		return style;
	}

	private static string NormalizeStatus(string status)
	{
		return string.IsNullOrWhiteSpace(status) ? "Unavailable" : status;
	}
}
