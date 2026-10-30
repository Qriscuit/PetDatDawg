using System;
using System.Globalization;
using System.Linq;
using Godot;

[Tool]
public partial class StatusWindow : Window
{
	private Vector2I _baseMinimumWindowSize;
	private float _baseContentScaleFactor = 1;
	private int _initialTab = 1;
	/// <summary>The page shown at startup and in the Godot editor preview.</summary>
	[Export(PropertyHint.Enum, "Dogs,Items,Shop,Settings")]
	public int InitialTab
	{
		get => _initialTab;
		set { _initialTab = Mathf.Clamp(value, 0, 3); if (Engine.IsEditorHint()) PreviewTab(); }
	}
	[ExportGroup("Dynamic captions")]
	[Export] public string ShowDetailsText { get; set; } = "Show technical details";
	[Export] public string HideDetailsText { get; set; } = "Hide technical details";
	[Export] public string SetRouteText { get; set; } = "Set patrol route";
	[Export] public string EditRouteText { get; set; } = "Edit route";
	[ExportGroup("Pet status text")]
	[Export] public string PetsTotalFormat { get; set; } = "Pets: {count}";
	[Export] public string PetsAwaitingText { get; set; } = "Pets: Waiting to sync";
	[Export] public string PetsUnavailableText { get; set; } = "Pets: Steam unavailable";
	[Export] public string ServiceStatusFormat { get; set; } = "Service: {status}";
	[Export] public string SteamStatusFormat { get; set; } = "{status}";
	[Export] public string UnavailableStatusText { get; set; } = "Unavailable";
	[ExportGroup("Connection summary text")]
	[Export(PropertyHint.MultilineText)] public string SteamDisabledText { get; set; } = "Steam connection is disabled for this run.";
	[Export(PropertyHint.MultilineText)] public string SteamUnavailableText { get; set; } = "Waiting for Steam. Open Steam and sign in; Pet that Dog will reconnect automatically.";
	[Export(PropertyHint.MultilineText)] public string SyncInterruptedText { get; set; } = "Pet syncing is interrupted. The app will retry while it is open.";
	[Export(PropertyHint.MultilineText)] public string ConnectingText { get; set; } = "Connecting to the pet service…";
	[Export(PropertyHint.MultilineText)] public string ConnectedText { get; set; } = "Connected to Steam and the pet service.";
	[ExportGroup("Patrol summary text")]
	[Export(PropertyHint.MultilineText)] public string NoPatrolRouteText { get; set; } = "No patrol route set. Your dog uses its usual walk.";
	[Export] public string PatrolRouteFormat { get; set; } = "{count} stops · {loop}";
	[Export] public string PatrolEnabledText { get; set; } = "Patrol is on.";
	[Export] public string PatrolDisabledText { get; set; } = "Patrol is off; your dog uses its usual walk.";
	[Export] public string StationaryPatrolText { get; set; } = "Your dog is stationary. Turn off Keep dog stationary to resume walking.";
	[Export(PropertyHint.MultilineText)] public string PatrolSummaryFormat { get; set; } = "{route}\n{state}";
	private const string LayoutPath = "user://menu_layout.cfg";
	private enum StatusTab { Dogs, Items, Shop, Settings }
	private PetSettings? _settings;
	private NativeWindowBridge? _nativeWindowBridge;
	private AccessoryEditingSession? _editingSession;
	private PatrolRoute? _patrolRoute;
	private bool _syncingPatrol, _returnFromPatrolEdit;
	private Button? _editPatrolButton, _clearPatrolButton;
	private CheckBox? _usePatrolCheck;
	private OptionButton? _patrolTraversalMode;
	private Label? _patrolSummary, _patrolSaveStatus;
	private bool _refreshingControls, _placed;
	private float _appliedUiScale = -1;
	private MenuFontScale? _menuFonts;
	private StatusTab _activeTab = StatusTab.Items;
	private Label? _petsValueLabel, _backendStatusLabel, _steamStatusLabel, _connectionSummary;
	private Button? _dogsTabButton, _itemsTabButton, _shopTabButton, _settingsTabButton;
	private Control? _dogsPage, _accessoriesPage, _shopPage, _settingsPage, _welcomePanel;
	private AccessoryEditor? _accessoryEditor;
	private CheckBox? _alwaysOnTopCheck, _dogClickThroughCheck, _stationaryCheck, _bounceEnabledCheck, _flipHorizontallyCheck;
	private CheckBox? _showPetCounterCheck, _openMenuOnRightClickCheck;
	private OptionButton? _petCounterPosition;
	private Button? _recordMenuHotkeyButton, _resetMenuHotkeyButton;
	private Label? _menuHotkeyHelp, _menuHotkeyStatus;
	private string _menuHotkeyIdleHelp = string.Empty;
	private bool _recordingMenuHotkey;
	private HSlider? _dogTransparencySlider, _dogScaleSlider, _uiScaleSlider, _fontScaleSlider;
	private Label? _dogTransparencyValueLabel, _dogScaleValueLabel, _uiScaleValueLabel, _fontScaleValueLabel;
	public float MenuFontScale => _settings?.FontScale ?? PetSettings.DefaultFontScale;

	public override void _Ready()
	{
		if (Engine.IsEditorHint())
		{
			PreviewTab(); SetProcess(false); return;
		}
		Visible = false;
		_baseMinimumWindowSize = MinSize;
		_baseContentScaleFactor = ContentScaleFactor;
		BindScene(); ConnectControls();
		_nativeWindowBridge = GetNodeOrNull<NativeWindowBridge>("/root/NativeWindowBridge");
		BindInventoryControls();
		BindPresetControls();
		_menuFonts = global::MenuFontScale.Attach(this);
		_editingSession = GetNodeOrNull<AccessoryEditingSession>("/root/AccessoryEditingSession");
		_patrolRoute = GetNodeOrNull<PatrolRoute>("/root/PatrolRoute");
		CloseRequested += () => { CancelMenuHotkeyCapture(); _editingSession?.SetActive(false); SaveLayout(); Hide(); };
		VisibilityChanged += SyncEditingState;
		SetActiveTab((StatusTab)_initialTab);
		ApplyUiScale();
		if (_patrolRoute != null)
		{
			_patrolRoute.Changed += RefreshPatrolControls;
			_patrolRoute.EditingChanged += OnPatrolEditingChanged;
		}
		RefreshPatrolControls();
	}
	public override void _Process(double delta)
	{
		if (Engine.IsEditorHint()) return;
		SyncEditingState();
		UpdateInventoryControls();
		if (_recordingMenuHotkey && (!Visible || Mode == ModeEnum.Minimized || !HasFocus() || _activeTab != StatusTab.Settings))
			CancelMenuHotkeyCapture();
		RefreshMenuHotkeyStatus();
	}
	private void PreviewTab()
	{
		if (!IsInsideTree()) return;
		foreach (var (name, index) in new[] { ("Dogs", 0), ("Items", 1), ("Shop", 2), ("Settings", 3) })
		{
			var page = GetNodeOrNull<Control>($"%{name}Page");
			if (page != null) page.Visible = _initialTab == index;
			GetNodeOrNull<Button>($"%{name}Tab")?.SetPressedNoSignal(_initialTab == index);
		}
	}
	private void BindScene()
	{
		_petsValueLabel = GetNode<Label>("%PetsValue");
		_backendStatusLabel = GetNode<Label>("%BackendStatus");
		_steamStatusLabel = GetNode<Label>("%SteamStatus");
		_connectionSummary = GetNode<Label>("%ConnectionSummary");
		_dogsTabButton = GetNode<Button>("%DogsTab");
		_itemsTabButton = GetNode<Button>("%ItemsTab");
		_shopTabButton = GetNode<Button>("%ShopTab");
		_settingsTabButton = GetNode<Button>("%SettingsTab");
		_dogsPage = GetNode<Control>("%DogsPage");
		_accessoriesPage = GetNode<Control>("%ItemsPage");
		_shopPage = GetNode<Control>("%ShopPage");
		_settingsPage = GetNode<Control>("%SettingsPage");
		_welcomePanel = GetNode<Control>("%WelcomePanel");
		_accessoryEditor = GetNode<AccessoryEditor>("%AccessoryEditor");
		_alwaysOnTopCheck = GetNode<CheckBox>("%AlwaysOnTop");
		_dogClickThroughCheck = GetNode<CheckBox>("%DogClickThrough");
		_stationaryCheck = GetNode<CheckBox>("%Stationary");
		_bounceEnabledCheck = GetNode<CheckBox>("%BounceEnabled");
		_flipHorizontallyCheck = GetNode<CheckBox>("%FlipHorizontally");
		_showPetCounterCheck = GetNode<CheckBox>("%ShowPetCounter");
		_petCounterPosition = GetNode<OptionButton>("%PetCounterPosition");
		_openMenuOnRightClickCheck = GetNode<CheckBox>("%OpenMenuOnRightClick");
		_recordMenuHotkeyButton = GetNode<Button>("%RecordMenuHotkey");
		_resetMenuHotkeyButton = GetNode<Button>("%ResetMenuHotkey");
		_menuHotkeyHelp = GetNode<Label>("%MenuHotkeyHelp");
		_menuHotkeyStatus = GetNode<Label>("%MenuHotkeyStatus");
		_menuHotkeyIdleHelp = _menuHotkeyHelp.Text;
		_dogTransparencySlider = GetNode<HSlider>("%DogTransparency");
		_dogScaleSlider = GetNode<HSlider>("%DogScale");
		_uiScaleSlider = GetNode<HSlider>("%UiScale");
		_fontScaleSlider = GetNode<HSlider>("%FontScale");
		_dogTransparencyValueLabel = GetNode<Label>("%DogTransparencyValue");
		_dogScaleValueLabel = GetNode<Label>("%DogScaleValue");
		_uiScaleValueLabel = GetNode<Label>("%UiScaleValue");
		_fontScaleValueLabel = GetNode<Label>("%FontScaleValue");
		_editPatrolButton = GetNode<Button>("%EditPatrolRoute");
		_clearPatrolButton = GetNode<Button>("%ClearPatrolRoute");
		_usePatrolCheck = GetNode<CheckBox>("%UsePatrolRoute");
		_patrolTraversalMode = GetNode<OptionButton>("%PatrolTraversalMode");
		_patrolSummary = GetNode<Label>("%PatrolRouteSummary");
		_patrolSaveStatus = GetNode<Label>("%PatrolRouteSaveStatus");
	}
	private void ConnectControls()
	{
		_dogsTabButton!.Pressed += () => SetActiveTab(StatusTab.Dogs);
		_itemsTabButton!.Pressed += () => SetActiveTab(StatusTab.Items);
		_shopTabButton!.Pressed += () => SetActiveTab(StatusTab.Shop);
		_settingsTabButton!.Pressed += () => SetActiveTab(StatusTab.Settings);
		GetNode<Button>("%CustomizeDog").Pressed += () => SetActiveTab(StatusTab.Items);
		GetNode<Button>("%DismissWelcome").Pressed += () => { _settings?.DismissWelcome(); _welcomePanel!.Hide(); };
		GetNode<Button>("%QuitPetThatDog").Pressed += () => GetTree().Quit();
		var detailsToggle = GetNode<Button>("%ConnectionDetailsToggle");
		var details = GetNode<Control>("%ConnectionDetails");
		detailsToggle.Toggled += expanded =>
		{
			details.Visible = expanded;
			detailsToggle.Text = expanded ? HideDetailsText : ShowDetailsText;
		};
		detailsToggle.Text = detailsToggle.ButtonPressed ? HideDetailsText : ShowDetailsText;
		_alwaysOnTopCheck!.Toggled += value => { if (!_refreshingControls) _settings?.SetAlwaysOnTop(value); };
		_dogClickThroughCheck!.Toggled += value => { if (!_refreshingControls) _settings?.SetDogClickThrough(value); };
		_stationaryCheck!.Toggled += value => { if (!_refreshingControls) _settings?.SetStationary(value); };
		_bounceEnabledCheck!.Toggled += value => { if (!_refreshingControls) _settings?.SetBounceEnabled(value); };
		_flipHorizontallyCheck!.Toggled += value => { if (!_refreshingControls) _settings?.SetFlipHorizontally(value); };
		_showPetCounterCheck!.Toggled += value => { if (!_refreshingControls) _settings?.SetShowPetCounter(value); };
		_petCounterPosition!.ItemSelected += index =>
		{
			if (!_refreshingControls) _settings?.SetPetCounterPosition((PetCounterPlacement)_petCounterPosition.GetItemId((int)index));
		};
		_openMenuOnRightClickCheck!.Toggled += value => { if (!_refreshingControls) _settings?.SetOpenMenuOnRightClick(value); };
		_recordMenuHotkeyButton!.Pressed += () =>
		{
			if (_recordingMenuHotkey) CancelMenuHotkeyCapture();
			else BeginMenuHotkeyCapture();
		};
		_resetMenuHotkeyButton!.Pressed += () =>
		{
			CancelMenuHotkeyCapture();
			_settings?.SetMenuHotkey(PetSettings.DefaultMenuHotkeyVirtualKey, PetSettings.DefaultMenuHotkeyModifiers);
		};
		_dogTransparencySlider!.ValueChanged += value => { if (!_refreshingControls) _settings?.SetDogTransparency((float)value); };
		_dogScaleSlider!.ValueChanged += value => { if (!_refreshingControls) _settings?.SetDogScale((float)value); };
		_uiScaleSlider!.ValueChanged += value => { if (!_refreshingControls) _settings?.SetUiScale((float)value); };
		_fontScaleSlider!.ValueChanged += value => { if (!_refreshingControls) _settings?.SetFontScale((float)value); };
		_editPatrolButton!.Pressed += BeginPatrolEdit;
		_clearPatrolButton!.Pressed += () => _patrolRoute?.ClearRoute();
		_usePatrolCheck!.Toggled += enabled => { if (!_syncingPatrol) _patrolRoute?.SetEnabled(enabled); };
		_patrolTraversalMode!.ItemSelected += index =>
		{
			if (!_syncingPatrol) _patrolRoute?.SetTraversalMode((PatrolTraversalMode)_patrolTraversalMode.GetItemId((int)index));
		};
	}
	private void SyncEditingState() => _editingSession?.SetActive(Visible && Mode != ModeEnum.Minimized
		&& _activeTab == StatusTab.Items && _patrolRoute?.IsEditing != true);
	public override void _ExitTree()
	{
		if (Engine.IsEditorHint()) return;
		CancelMenuHotkeyCapture();
		_editingSession?.SetActive(false);
		SaveLayout();
		ReleasePresetControls();
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
		if (Engine.IsEditorHint()) return;
		if (_settings != null) _settings.SettingsChanged -= OnSettingsChanged;
		_settings = settings;
		if (_settings != null) _settings.SettingsChanged += OnSettingsChanged;
		RefreshControls(); ApplyUiScale();
	}
	public void ShowStatusWindow()
	{
		if (Engine.IsEditorHint()) return;
		if (Mode == ModeEnum.Minimized) Mode = ModeEnum.Windowed;
		RefreshControls(); ApplyUiScale();
		if (!_placed) { LoadLayout(); _placed = true; }
		KeepWindowReachable(); Show(); GrabFocus();
		SyncEditingState();
	}
	public void UpdateStatus(long? confirmedPets, string backendStatus, string steamStatus,
		bool steamReady = false, bool backendReady = false, bool inputReady = true, string? inputStatus = null)
	{
		if (Engine.IsEditorHint() || _petsValueLabel == null) return;
		var retrying = backendStatus.Contains("retry", StringComparison.OrdinalIgnoreCase)
			|| backendStatus.Contains("failed", StringComparison.OrdinalIgnoreCase);
		_petsValueLabel!.Text = confirmedPets.HasValue
			? PetsTotalFormat.Replace("{count}", confirmedPets.Value.ToString("N0", CultureInfo.InvariantCulture))
			: steamReady ? PetsAwaitingText : PetsUnavailableText;
		_backendStatusLabel!.Text = ServiceStatusFormat.Replace("{status}", NormalizeStatus(backendStatus));
		_steamStatusLabel!.Text = SteamStatusFormat.Replace("{status}", NormalizeStatus(steamStatus));
		if (!string.IsNullOrWhiteSpace(inputStatus)) _steamStatusLabel.Text += "\nDesktop input: " + inputStatus;
		_connectionSummary!.Text = !steamReady ? steamStatus.StartsWith("Steam is disabled", StringComparison.OrdinalIgnoreCase)
			? SteamDisabledText : SteamUnavailableText
			: !inputReady ? "Desktop input is unavailable. Check technical details before earning Pets."
			: retrying ? SyncInterruptedText : !backendReady ? ConnectingText : ConnectedText;
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
		_editPatrolButton.Text = count > 0 ? EditRouteText : SetRouteText;
		_editPatrolButton.Disabled = _patrolRoute == null || editing;
		_clearPatrolButton!.Disabled = _patrolRoute == null || count == 0 || editing;
		_usePatrolCheck!.Disabled = _patrolRoute == null || count < 2 || editing;
		_patrolTraversalMode!.Disabled = _patrolRoute == null || editing;
		_syncingPatrol = true;
		_usePatrolCheck.SetPressedNoSignal(_patrolRoute?.Enabled == true);
		_patrolTraversalMode.Select(_patrolTraversalMode.GetItemIndex((int)(_patrolRoute?.TraversalMode ?? PatrolTraversalMode.Loop)));
		_syncingPatrol = false;
		var stops = PatrolRouteToolbar.DescribeStops(count, _patrolRoute?.TraversalMode ?? PatrolTraversalMode.Loop);
		var routeDescription = PatrolRouteFormat.Replace("{count}", count.ToString(CultureInfo.InvariantCulture)).Replace("{loop}", stops);
		var stationary = _settings?.Stationary == true;
		_patrolSummary!.Text = count == 0 ? stationary ? StationaryPatrolText : NoPatrolRouteText
			: PatrolSummaryFormat.Replace("{route}", routeDescription)
				.Replace("{state}", stationary ? StationaryPatrolText : _patrolRoute?.Enabled == true ? PatrolEnabledText : PatrolDisabledText);
		_patrolSaveStatus!.Visible = _patrolRoute?.LastSaveSucceeded == false;
	}
	private void SetActiveTab(StatusTab tab)
	{
		if (tab != StatusTab.Settings) CancelMenuHotkeyCapture();
		if (tab != StatusTab.Items) _editingSession?.SetActive(false);
		_activeTab = tab; _dogsTabButton?.SetPressedNoSignal(tab == StatusTab.Dogs); _itemsTabButton?.SetPressedNoSignal(tab == StatusTab.Items);
		_shopTabButton?.SetPressedNoSignal(tab == StatusTab.Shop); _settingsTabButton?.SetPressedNoSignal(tab == StatusTab.Settings);
		if (_accessoriesPage != null) _accessoriesPage.Visible = tab == StatusTab.Items;
		if (_dogsPage != null) _dogsPage.Visible = tab == StatusTab.Dogs;
		if (_shopPage != null) _shopPage.Visible = tab == StatusTab.Shop;
		if (_settingsPage != null) _settingsPage.Visible = tab == StatusTab.Settings;
		SyncEditingState();
		if (tab == StatusTab.Shop && Visible) SynchronizeOpenShop();
	}
	private void RefreshControls()
	{
		RefreshPatrolControls();
		if (_settings == null) return; _refreshingControls = true;
		_alwaysOnTopCheck!.SetPressedNoSignal(_settings.AlwaysOnTop); _dogClickThroughCheck!.SetPressedNoSignal(_settings.DogClickThrough);
		_stationaryCheck!.SetPressedNoSignal(_settings.Stationary); _bounceEnabledCheck!.SetPressedNoSignal(_settings.BounceEnabled);
		_flipHorizontallyCheck!.SetPressedNoSignal(_settings.FlipHorizontally);
		_showPetCounterCheck!.SetPressedNoSignal(_settings.ShowPetCounter);
		_petCounterPosition!.Select(_petCounterPosition.GetItemIndex((int)_settings.PetCounterPosition));
		_petCounterPosition.Disabled = !_settings.ShowPetCounter;
		_openMenuOnRightClickCheck!.SetPressedNoSignal(_settings.OpenMenuOnRightClick);
		RefreshMenuShortcutControls();
		SetValue(_dogTransparencySlider, _dogTransparencyValueLabel, _settings.DogTransparency);
		SetValue(_dogScaleSlider, _dogScaleValueLabel, _settings.DogScale); SetValue(_uiScaleSlider, _uiScaleValueLabel, _settings.UiScale);
		SetValue(_fontScaleSlider, _fontScaleValueLabel, _settings.FontScale);
		_welcomePanel!.Visible = !_settings.HasSeenWelcome; _refreshingControls = false;
	}

	private void BeginMenuHotkeyCapture()
	{
		if (_settings == null || _recordingMenuHotkey) return;
		_recordingMenuHotkey = true;
		_nativeWindowBridge?.SetMenuHotkeyCaptureActive(true);
		_menuHotkeyHelp!.Text = "Hold Ctrl, Alt or Shift and press another key. Escape cancels.";
		RefreshMenuShortcutControls();
	}

	private void CancelMenuHotkeyCapture()
	{
		if (!_recordingMenuHotkey) return;
		_recordingMenuHotkey = false;
		_nativeWindowBridge?.SetMenuHotkeyCaptureActive(false);
		_menuHotkeyHelp!.Text = _menuHotkeyIdleHelp;
		_recordMenuHotkeyButton?.ReleaseFocus();
		RefreshMenuShortcutControls();
	}

	public override void _Input(InputEvent @event)
	{
		if (!_recordingMenuHotkey || @event is not InputEventKey key) return;
		GetViewport().SetInputAsHandled();
		if (!key.Pressed || key.Echo) return;
		if (key.Keycode == Key.Escape || key.PhysicalKeycode == Key.Escape)
		{
			CancelMenuHotkeyCapture();
			return;
		}
		if (!MenuShortcut.TryFromGodotKey(key, out var virtualKey, out var modifiers))
		{
			_menuHotkeyHelp!.Text = "Choose Ctrl, Alt or Shift with another key. If that shortcut is unavailable, try a different key. Escape cancels.";
			return;
		}
		_settings?.SetMenuHotkey(virtualKey, modifiers);
		CancelMenuHotkeyCapture();
	}

	private void RefreshMenuShortcutControls()
	{
		if (_recordMenuHotkeyButton == null) return;
		_recordMenuHotkeyButton.Disabled = _settings == null;
		_recordMenuHotkeyButton.Text = _recordingMenuHotkey ? "Press shortcut…"
			: MenuShortcut.Format(_settings?.MenuHotkeyVirtualKey ?? PetSettings.DefaultMenuHotkeyVirtualKey,
				_settings?.MenuHotkeyModifiers ?? PetSettings.DefaultMenuHotkeyModifiers);
		_recordMenuHotkeyButton.TooltipText = _recordMenuHotkeyButton.Text
			+ "\nClick to choose a new menu shortcut. Click again or press Escape to cancel.";
		_resetMenuHotkeyButton!.Disabled = _settings == null || (!_recordingMenuHotkey
			&& _settings.MenuHotkeyVirtualKey == PetSettings.DefaultMenuHotkeyVirtualKey
			&& _settings.MenuHotkeyModifiers == PetSettings.DefaultMenuHotkeyModifiers);
		RefreshMenuHotkeyStatus();
	}

	private void RefreshMenuHotkeyStatus()
	{
		if (_menuHotkeyStatus == null) return;
		var status = _recordingMenuHotkey ? "Waiting for your new shortcut…" : _nativeWindowBridge?.MenuHotkeyStatus;
		if (string.IsNullOrWhiteSpace(status)) status = "Choose a shortcut to open this menu.";
		if (_menuHotkeyStatus.Text != status) _menuHotkeyStatus.Text = status;
	}
	private static void SetValue(HSlider? slider, Label? label, float value)
	{
		if (slider != null) slider.Value = value; if (label != null) label.Text = $"{Mathf.RoundToInt(value * 100)}%";
	}
	private void ApplyUiScale()
	{
		_menuFonts?.Apply(MenuFontScale);
		_presetsWindow?.ApplyMenuFontScale(MenuFontScale);
		var scale = _settings?.UiScale ?? PetSettings.DefaultUiScale;
		if (Mathf.IsEqualApprox(scale, _appliedUiScale)) return;
		_appliedUiScale = scale;
		// ContentScaleFactor changes the UI canvas, independently of the player's window geometry.
		ContentScaleFactor = _baseContentScaleFactor * scale;
	}
	private void OnSettingsChanged() { RefreshControls(); ApplyUiScale(); }
	private void LoadLayout()
	{
		var config = new ConfigFile();
		if (config.Load(LayoutPath) == Error.Ok)
		{
			var position = config.GetValue("menu", "position"); var size = config.GetValue("menu", "size");
			if (position.VariantType == Variant.Type.Vector2I) Position = position.AsVector2I();
			if (size.VariantType == Variant.Type.Vector2I && config.GetValue("menu", "layout_version", 0).AsInt32() >= 2)
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
		config.SetValue("menu", "layout_version", 3); config.Save(LayoutPath);
	}
	private void KeepWindowReachable()
	{
		if (Engine.IsEmbeddedInEditor()) return;
		for (var screen = 0; screen < DisplayServer.GetScreenCount(); screen++)
		{
			var usable = DisplayServer.ScreenGetUsableRect(screen);
			if (!usable.HasPoint(Position + new Vector2I(32, 16))) continue;
			var maximum = new Vector2I(Mathf.Max(1, usable.Size.X - 24), Mathf.Max(1, usable.Size.Y - 56));
			MinSize = _baseMinimumWindowSize.Min(maximum);
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
	private string NormalizeStatus(string text) => string.IsNullOrWhiteSpace(text) ? UnavailableStatusText : text;
}
