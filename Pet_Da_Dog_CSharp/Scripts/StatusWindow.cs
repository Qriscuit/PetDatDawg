using System;
using System.Globalization;
using Godot;

public partial class StatusWindow : Window
{
	private static readonly Vector2I BaseWindowSize = new(640, 780);
	private static readonly Vector2I BaseMinimumWindowSize = new(560, 600);
	private const string LayoutPath = "user://menu_layout.cfg";
	private enum StatusTab { Dogs, Items, Shop, Settings }
	private PetSettings? _settings;
	private AccessoryEditingSession? _editingSession;
	private PatrolRoute? _patrolRoute;
	private bool _syncingPatrol, _returnFromPatrolEdit;
	private Button? _editPatrolButton, _clearPatrolButton;
	private CheckBox? _usePatrolCheck;
	private Label? _patrolSummary, _patrolSaveStatus;
	private bool _refreshingControls, _placed;
	private float _appliedUiScale = -1;
	private StatusTab _activeTab = StatusTab.Items;
	private Label? _petsValueLabel, _pendingLabel, _backendStatusLabel, _steamStatusLabel, _connectionSummary;
	private Button? _dogsTabButton, _itemsTabButton, _shopTabButton, _settingsTabButton;
	private Control? _dogsPage, _accessoriesPage, _shopPage, _settingsPage, _welcomePanel;
	private AccessoryEditor? _accessoryEditor;
	private CheckBox? _alwaysOnTopCheck, _dogClickThroughCheck;
	private HSlider? _dogTransparencySlider, _dogScaleSlider, _uiScaleSlider;
	private Label? _dogTransparencyValueLabel, _dogScaleValueLabel, _uiScaleValueLabel;

	public override void _Ready()
	{
		_editingSession = GetNodeOrNull<AccessoryEditingSession>("/root/AccessoryEditingSession");
		_patrolRoute = GetNodeOrNull<PatrolRoute>("/root/PatrolRoute");
		Title = "Pet Da Dog";
		Size = BaseWindowSize; MinSize = BaseMinimumWindowSize;
		AlwaysOnTop = false; Borderless = false; Transparent = false;
		TransparentBg = false; Unresizable = false; Visible = false;
		CloseRequested += () => { _editingSession?.SetActive(false); SaveLayout(); Hide(); };
		VisibilityChanged += SyncEditingState;
		Theme = WoodlandTheme.Build(); BuildUi(); ApplyUiScale();
		if (_patrolRoute != null)
		{
			_patrolRoute.Changed += RefreshPatrolControls;
			_patrolRoute.EditingChanged += OnPatrolEditingChanged;
		}
		RefreshPatrolControls();
	}
	public override void _Process(double delta) => SyncEditingState();
	private void SyncEditingState() => _editingSession?.SetActive(Visible && Mode != ModeEnum.Minimized
		&& _activeTab == StatusTab.Items && _patrolRoute?.IsEditing != true);
	public override void _ExitTree()
	{
		_editingSession?.SetActive(false);
		SaveLayout();
		if (_settings != null) _settings.SettingsChanged -= OnSettingsChanged;
		_returnFromPatrolEdit = false;
		if (_patrolRoute != null)
		{
			_patrolRoute.Changed -= RefreshPatrolControls;
			_patrolRoute.EditingChanged -= OnPatrolEditingChanged;
		}
	}
	public void Configure(PetSettings? settings)
	{
		if (_settings != null) _settings.SettingsChanged -= OnSettingsChanged;
		_settings = settings;
		if (_settings != null) _settings.SettingsChanged += OnSettingsChanged;
		RefreshControls(); ApplyUiScale();
	}
	public void ShowStatusWindow()
	{
		if (Mode == ModeEnum.Minimized) Mode = ModeEnum.Windowed;
		RefreshControls(); ApplyUiScale();
		if (!_placed) { LoadLayout(); _placed = true; }
		KeepWindowReachable(); Show(); GrabFocus();
		SyncEditingState();
	}
	public void UpdateStatus(long? confirmedPets, int pendingGrantCount, string backendStatus, string steamStatus,
		bool steamReady = false, bool backendReady = false)
	{
		var retrying = backendStatus.Contains("retry", StringComparison.OrdinalIgnoreCase)
			|| backendStatus.Contains("failed", StringComparison.OrdinalIgnoreCase);
		_petsValueLabel!.Text = confirmedPets.HasValue
			? $"Pets: {confirmedPets.Value.ToString("N0", CultureInfo.InvariantCulture)}"
			: steamReady ? "Pets: Waiting to sync" : "Pets: Steam unavailable";
		_pendingLabel!.Visible = pendingGrantCount > 0;
		_pendingLabel.Text = $"{pendingGrantCount} {(pendingGrantCount == 1 ? "click" : "clicks")} waiting to sync";
		_pendingLabel.TooltipText = "Waiting clicks retry while this app is open. They are not yet confirmed pets and are cleared when you quit.";
		_backendStatusLabel!.Text = $"Service: {NormalizeStatus(backendStatus)}";
		_steamStatusLabel!.Text = NormalizeStatus(steamStatus);
		_connectionSummary!.Text = !steamReady ? steamStatus.StartsWith("Steam is disabled", StringComparison.OrdinalIgnoreCase)
			? "Steam connection is disabled for this run." : "Steam is unavailable. Open Steam, then restart Pet Da Dog."
			: retrying ? "Pet syncing is interrupted. The app will retry while it is open."
			: !backendReady ? "Connecting to the pet service…" : "Connected to Steam and the pet service.";
	}
	private void BuildUi()
	{
		var background = new PanelContainer();
		background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		background.AddThemeStyleboxOverride("panel", WoodlandTheme.WindowStyle()); AddChild(background);
		var margin = new MarginContainer();
		foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 20);
		background.AddChild(margin);
		var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		root.AddThemeConstantOverride("separation", 10); margin.AddChild(root);
		root.AddChild(CreateTopBar());
		_welcomePanel = CreateWelcome(); root.AddChild(_welcomePanel); root.AddChild(CreateTabPages());
	}
	private Control CreateTopBar()
	{
		var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", WoodlandTheme.PanelStyle());
		var header = new VBoxContainer(); panel.AddChild(header);
		var tabs = new HBoxContainer(); tabs.AddThemeConstantOverride("separation", 8);
		_dogsTabButton = CreateTabButton("Dogs", StatusTab.Dogs);
		_itemsTabButton = CreateTabButton("Items", StatusTab.Items);
		_shopTabButton = CreateTabButton("Shop", StatusTab.Shop);
		_settingsTabButton = CreateTabButton("Settings", StatusTab.Settings);
		tabs.AddChild(_dogsTabButton); tabs.AddChild(_itemsTabButton); tabs.AddChild(_shopTabButton); tabs.AddChild(_settingsTabButton);
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = "Pet Da Dog", ThemeTypeVariation = "WoodlandHeading", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
		row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		var pets = new VBoxContainer();
		_petsValueLabel = CreateLabel("Pets: Waiting to sync", WoodlandTheme.Text, 13);
		_petsValueLabel.HorizontalAlignment = HorizontalAlignment.Right;
		_pendingLabel = CreateLabel(string.Empty, WoodlandTheme.Muted, 12);
		_pendingLabel.HorizontalAlignment = HorizontalAlignment.Right; _pendingLabel.Visible = false;
		pets.AddChild(_petsValueLabel); pets.AddChild(_pendingLabel); row.AddChild(pets); header.AddChild(row); header.AddChild(tabs);
		return panel;
	}
	private Control CreateWelcome()
	{
		var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", WoodlandTheme.PanelStyle());
		var row = new HBoxContainer(); panel.AddChild(row);
		row.AddChild(CreateStatusLabel("Welcome! Right-click your desktop dog to customize it.\nYou can also choose Open Pet Da Dog from its tray icon. Closing this menu keeps your dog on the desktop."));
		var dismiss = new Button { Text = "Got it", SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
		WoodlandTheme.StyleButton(dismiss, "SecondaryButton");
		dismiss.Pressed += () => { _settings?.DismissWelcome(); panel.Hide(); }; row.AddChild(dismiss); return panel;
	}
	private Control CreateTabPages()
	{
		var pages = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		_accessoryEditor = new AccessoryEditor();
		var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		var style = WoodlandTheme.PanelStyle(); style.SetContentMarginAll(4);
		panel.AddThemeStyleboxOverride("panel", style); panel.AddChild(_accessoryEditor);
		_accessoriesPage = panel; _dogsPage = CreateDogs(); _shopPage = CreateShop(); _settingsPage = CreateSettingsTab();
		pages.AddChild(_dogsPage); pages.AddChild(_accessoriesPage); pages.AddChild(_shopPage); pages.AddChild(_settingsPage);
		SetActiveTab(_activeTab); return pages;
	}
	private Control CreateDogs()
	{
		var (panel, content) = CreatePanel(); panel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
		AddSectionTitle(content, "Your desktop dog");
		var texture = ResourceLoader.Load<Texture2D>("res://Sprites/Doggo.png");
		if (texture != null)
		{
			var bounds = AccessoryWardrobe.GetVisibleBounds(texture);
			content.AddChild(new TextureRect { Texture = new AtlasTexture { Atlas = texture, Region = bounds },
				CustomMinimumSize = new Vector2(0, 220), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = Control.SizeFlags.ExpandFill });
		}
		content.AddChild(CreateStatusLabel("Your current dog is ready for the desktop.\nVisit Items to change its look, or Settings to adjust its visibility and size."));
		var customize = new Button { Text = "Customize this dog", SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
		WoodlandTheme.StyleButton(customize, "ActionButton"); customize.Pressed += () => SetActiveTab(StatusTab.Items);
		content.AddChild(customize); return panel;
	}
	private static Control CreateShop()
	{
		var (panel, content) = CreatePanel(); panel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
		content.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
		AddSectionTitle(content, "Shop is coming soon");
		content.AddChild(CreateStatusLabel("There are no items for purchase yet.\nYou can use the bundled accessories in Items to create your dog's look.")); return panel;
	}
	private Control CreateSettingsTab()
	{
		var scroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		content.AddThemeConstantOverride("separation", 12); scroll.AddChild(content);
		content.AddChild(CreatePatrolPanel()); content.AddChild(CreateSettingsPanel()); content.AddChild(CreateConnectionPanel());
		var quit = new Button { Name = "QuitPetDaDog", Text = "Quit Pet Da Dog", TooltipText = "Closes the app and removes the desktop dog. Waiting clicks are not kept after quitting.",
			SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
		WoodlandTheme.StyleButton(quit, "SecondaryButton"); quit.Pressed += () => GetTree().Quit(); content.AddChild(quit); return scroll;
	}
	private Control CreatePatrolPanel()
	{
		var (panel, content) = CreatePanel(); panel.Name = "PatrolRouteSettings";
		AddSectionTitle(content, "Patrol route");
		content.AddChild(CreateStatusLabel("Choose stops on your desktop. Your dog visits them in order, then loops back to the first stop."));
		_patrolSummary = CreateStatusLabel("No patrol route set.");
		_patrolSummary.Name = "PatrolRouteSummary"; content.AddChild(_patrolSummary);
		var actions = new HBoxContainer(); actions.AddThemeConstantOverride("separation", 8);
		_editPatrolButton = new Button { Name = "EditPatrolRoute", Text = "Set patrol route", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		WoodlandTheme.StyleButton(_editPatrolButton, "ActionButton");
		_editPatrolButton.Pressed += BeginPatrolEdit; actions.AddChild(_editPatrolButton);
		_clearPatrolButton = new Button { Name = "ClearPatrolRoute", Text = "Clear route", TooltipText = "Remove the saved route and return to the usual walk." };
		WoodlandTheme.StyleButton(_clearPatrolButton, "SecondaryButton");
		_clearPatrolButton.Pressed += () => _patrolRoute?.ClearRoute(); actions.AddChild(_clearPatrolButton);
		content.AddChild(actions);
		_usePatrolCheck = new CheckBox { Name = "UsePatrolRoute", Text = "Use patrol route",
			TooltipText = "Turn off to return to the usual walk without removing your route." };
		_usePatrolCheck.Toggled += enabled => { if (!_syncingPatrol) _patrolRoute?.SetEnabled(enabled); };
		content.AddChild(_usePatrolCheck);
		content.AddChild(CreateStatusLabel("Turn this off to return to your dog's usual walk."));
		_patrolSaveStatus = CreateStatusLabel(string.Empty);
		_patrolSaveStatus.Name = "PatrolRouteSaveStatus";
		_patrolSaveStatus.AddThemeColorOverride("font_color", WoodlandTheme.Accent);
		content.AddChild(_patrolSaveStatus);
		return panel;
	}
	private void BeginPatrolEdit()
	{
		if (_patrolRoute == null || _patrolRoute.IsEditing) return;
		_editingSession?.SetActive(false);
		_returnFromPatrolEdit = true;
		SaveLayout(); Hide();
		_patrolRoute.BeginEdit();
	}
	private void OnPatrolEditingChanged(bool editing)
	{
		RefreshPatrolControls(); SyncEditingState();
		if (editing || !_returnFromPatrolEdit) return;
		_returnFromPatrolEdit = false;
		// Let the desktop editor restore its window before this menu takes focus again.
		CallDeferred(nameof(ReturnFromPatrolEdit));
	}
	private void ReturnFromPatrolEdit()
	{
		if (!IsInsideTree() || _patrolRoute?.IsEditing == true) return;
		SetActiveTab(StatusTab.Settings);
		ShowStatusWindow();
	}
	private void RefreshPatrolControls()
	{
		if (_editPatrolButton == null) return;
		var count = _patrolRoute?.Points.Count ?? 0;
		var editing = _patrolRoute?.IsEditing == true;
		_editPatrolButton.Text = count > 0 ? "Edit route" : "Set patrol route";
		_editPatrolButton.Disabled = _patrolRoute == null || editing;
		_clearPatrolButton!.Disabled = _patrolRoute == null || count == 0 || editing;
		_usePatrolCheck!.Disabled = _patrolRoute == null || count < 2 || editing;
		_syncingPatrol = true;
		_usePatrolCheck.SetPressedNoSignal(_patrolRoute?.Enabled == true);
		_syncingPatrol = false;
		_patrolSummary!.Text = count == 0 ? "No patrol route set. Your dog uses its usual walk."
			: PatrolRouteToolbar.DescribeRoute(count) + (_patrolRoute?.Enabled == true ? "\nPatrol is on." : "\nPatrol is off; your dog uses its usual walk.");
		_patrolSaveStatus!.Visible = _patrolRoute?.LastSaveSucceeded == false;
		_patrolSaveStatus.Text = "Could not save patrol settings. This change applies for this session; try again to save it.";
	}
	private Control CreateConnectionPanel()
	{
		var (panel, content) = CreatePanel(); AddSectionTitle(content, "Connection");
		_connectionSummary = CreateStatusLabel("Connecting…"); content.AddChild(_connectionSummary);
		var toggle = new Button { Text = "Show technical details", ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
		WoodlandTheme.StyleButton(toggle, "SecondaryButton"); content.AddChild(toggle);
		var details = new VBoxContainer { Visible = false, Name = "ConnectionDetails" };
		_backendStatusLabel = CreateStatusLabel("Service: Starting"); _steamStatusLabel = CreateStatusLabel("Steam: Starting");
		details.AddChild(_backendStatusLabel); details.AddChild(_steamStatusLabel); content.AddChild(details);
		toggle.Toggled += expanded => { details.Visible = expanded; toggle.Text = expanded ? "Hide technical details" : "Show technical details"; };
		return panel;
	}
	private Control CreateSettingsPanel()
	{
		var (panel, content) = CreatePanel(); AddSectionTitle(content, "Your desktop dog");
		_alwaysOnTopCheck = new CheckBox { Text = "Keep dog above other windows" };
		_alwaysOnTopCheck.Toggled += value => { if (!_refreshingControls) _settings?.SetAlwaysOnTop(value); }; content.AddChild(_alwaysOnTopCheck);
		_dogClickThroughCheck = new CheckBox { Text = "Let clicks pass through the dog" };
		_dogClickThroughCheck.Toggled += value => { if (!_refreshingControls) _settings?.SetDogClickThrough(value); }; content.AddChild(_dogClickThroughCheck);
		content.AddChild(CreateStatusLabel("When enabled, you cannot pet or right-click the dog.\nPress Alt + backtick to toggle this, or reopen the menu from the tray icon."));
		content.AddChild(CreateSliderBlock("Dog visibility", PetSettings.MinDogTransparency, PetSettings.MaxDogTransparency,
			out _dogTransparencySlider, out _dogTransparencyValueLabel, value => _settings?.SetDogTransparency(value)));
		content.AddChild(CreateStatusLabel("100% is fully visible; 0% hides the dog."));
		content.AddChild(CreateSliderBlock("Dog size", PetSettings.MinDogScale, PetSettings.MaxDogScale,
			out _dogScaleSlider, out _dogScaleValueLabel, value => _settings?.SetDogScale(value)));
		content.AddChild(CreateSliderBlock("Menu size", PetSettings.MinUiScale, PetSettings.MaxUiScale,
			out _uiScaleSlider, out _uiScaleValueLabel, value => _settings?.SetUiScale(value))); return panel;
	}
	private Control CreateSliderBlock(string text, double min, double max, out HSlider slider, out Label valueLabel, Action<float> apply)
	{
		var block = new VBoxContainer(); var row = new HBoxContainer(); block.AddChild(row);
		row.AddChild(CreateLabel(text, WoodlandTheme.Text));
		valueLabel = CreateLabel(string.Empty, WoodlandTheme.Muted); valueLabel.HorizontalAlignment = HorizontalAlignment.Right; row.AddChild(valueLabel);
		slider = new HSlider { MinValue = min, MaxValue = max, Step = 0.01, CustomMinimumSize = new Vector2(120, 28), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		slider.ValueChanged += value => { if (!_refreshingControls) apply((float)value); }; block.AddChild(slider); return block;
	}
	private static (PanelContainer, VBoxContainer) CreatePanel()
	{
		var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		panel.AddThemeStyleboxOverride("panel", WoodlandTheme.PanelStyle(true));
		var margin = new MarginContainer();
		foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 12);
		panel.AddChild(margin);
		var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		content.AddThemeConstantOverride("separation", 8); margin.AddChild(content); return (panel, content);
	}
	private static Label CreateLabel(string text, Color color, int size = 14)
	{
		var label = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		label.AddThemeColorOverride("font_color", color); label.AddThemeFontSizeOverride("font_size", size); return label;
	}
	private static Label CreateStatusLabel(string text)
	{
		var label = CreateLabel(text, WoodlandTheme.Muted, 13); label.AutowrapMode = TextServer.AutowrapMode.WordSmart; return label;
	}
	private static void AddSectionTitle(VBoxContainer parent, string text) => parent.AddChild(new Label { Text = text, ThemeTypeVariation = "WoodlandHeading" });
	private Button CreateTabButton(string text, StatusTab tab)
	{
		var button = new Button { Text = text, ToggleMode = true, CustomMinimumSize = new Vector2(80, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		WoodlandTheme.StyleButton(button, "TabButton"); button.Pressed += () => SetActiveTab(tab); return button;
	}
	private void SetActiveTab(StatusTab tab)
	{
		if (tab != StatusTab.Items) _editingSession?.SetActive(false);
		_activeTab = tab; _dogsTabButton?.SetPressedNoSignal(tab == StatusTab.Dogs); _itemsTabButton?.SetPressedNoSignal(tab == StatusTab.Items);
		_shopTabButton?.SetPressedNoSignal(tab == StatusTab.Shop); _settingsTabButton?.SetPressedNoSignal(tab == StatusTab.Settings);
		if (_accessoriesPage != null) _accessoriesPage.Visible = tab == StatusTab.Items;
		if (_dogsPage != null) _dogsPage.Visible = tab == StatusTab.Dogs;
		if (_shopPage != null) _shopPage.Visible = tab == StatusTab.Shop;
		if (_settingsPage != null) _settingsPage.Visible = tab == StatusTab.Settings;
		SyncEditingState();
	}
	private void RefreshControls()
	{
		RefreshPatrolControls();
		if (_settings == null) return; _refreshingControls = true;
		_alwaysOnTopCheck!.SetPressedNoSignal(_settings.AlwaysOnTop); _dogClickThroughCheck!.SetPressedNoSignal(_settings.DogClickThrough);
		SetValue(_dogTransparencySlider, _dogTransparencyValueLabel, _settings.DogTransparency);
		SetValue(_dogScaleSlider, _dogScaleValueLabel, _settings.DogScale); SetValue(_uiScaleSlider, _uiScaleValueLabel, _settings.UiScale);
		_welcomePanel!.Visible = !_settings.HasSeenWelcome; _refreshingControls = false;
	}
	private static void SetValue(HSlider? slider, Label? label, float value)
	{
		if (slider != null) slider.Value = value; if (label != null) label.Text = $"{Mathf.RoundToInt(value * 100)}%";
	}
	private void ApplyUiScale()
	{
		var scale = _settings?.UiScale ?? PetSettings.DefaultUiScale;
		if (Mathf.IsEqualApprox(scale, _appliedUiScale)) return;
		_appliedUiScale = scale; Theme = WoodlandTheme.Build(scale);
		var size = new Vector2I(Mathf.RoundToInt(BaseWindowSize.X * scale), Mathf.RoundToInt(BaseWindowSize.Y * scale));
		var minimum = new Vector2I(Mathf.RoundToInt(BaseMinimumWindowSize.X * scale), Mathf.RoundToInt(BaseMinimumWindowSize.Y * scale));
		if (!Engine.IsEmbeddedInEditor())
		{
			var usable = DisplayServer.ScreenGetUsableRect(CurrentScreen);
			size = new Vector2I(Mathf.Min(size.X, usable.Size.X - 48), Mathf.Min(size.Y, usable.Size.Y - 80));
		}
		MinSize = new Vector2I(Mathf.Min(minimum.X, size.X), Mathf.Min(minimum.Y, size.Y)); Size = size;
	}
	private void OnSettingsChanged() { RefreshControls(); ApplyUiScale(); }
	private void LoadLayout()
	{
		var config = new ConfigFile();
		if (config.Load(LayoutPath) == Error.Ok)
		{
			var position = config.GetValue("menu", "position"); var size = config.GetValue("menu", "size");
			if (position.VariantType == Variant.Type.Vector2I) Position = position.AsVector2I();
			if (size.VariantType == Variant.Type.Vector2I && config.GetValue("menu", "layout_version", 0).AsInt32() >= 2
				&& Mathf.IsEqualApprox(config.GetValue("menu", "scale", 1f).AsSingle(), _appliedUiScale))
			{
				var stored = size.AsVector2I(); if (stored.X > 0 && stored.Y > 0) Size = stored.Max(MinSize);
			}
			return;
		}
		PlaceWindowAboveDogPath();
	}
	private void SaveLayout()
	{
		if (!_placed || Mode != ModeEnum.Windowed) return;
		var config = new ConfigFile(); config.SetValue("menu", "position", Position); config.SetValue("menu", "size", Size);
		config.SetValue("menu", "scale", _appliedUiScale); config.SetValue("menu", "layout_version", 2); config.Save(LayoutPath);
	}
	private void KeepWindowReachable()
	{
		if (Engine.IsEmbeddedInEditor()) return;
		for (var screen = 0; screen < DisplayServer.GetScreenCount(); screen++)
		{
			var usable = DisplayServer.ScreenGetUsableRect(screen);
			if (!usable.HasPoint(Position + new Vector2I(32, 16))) continue;
			var maximum = new Vector2I(Mathf.Max(1, usable.Size.X - 24), Mathf.Max(1, usable.Size.Y - 56));
			MinSize = new Vector2I(Mathf.RoundToInt(BaseMinimumWindowSize.X * _appliedUiScale), Mathf.RoundToInt(BaseMinimumWindowSize.Y * _appliedUiScale)).Min(maximum);
			Size = Size.Min(maximum).Max(MinSize);
			Position = new Vector2I(Mathf.Clamp(Position.X, usable.Position.X + 12, Mathf.Max(usable.Position.X + 12, usable.End.X - Size.X - 12)),
				Mathf.Clamp(Position.Y, usable.Position.Y + 40, Mathf.Max(usable.Position.Y + 40, usable.End.Y - Size.Y - 16))); return;
		}
		PlaceWindowAboveDogPath();
	}
	private void PlaceWindowAboveDogPath()
	{
		if (Engine.IsEmbeddedInEditor()) return;
		var usable = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary);
		Position = new Vector2I(Mathf.Max(usable.Position.X + 24, usable.End.X - Size.X - 24), usable.Position.Y + 24);
	}
	private static string NormalizeStatus(string text) => string.IsNullOrWhiteSpace(text) ? "Unavailable" : text;
}
