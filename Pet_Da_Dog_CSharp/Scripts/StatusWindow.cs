using System;
using System.Globalization;
using System.Linq;
using Godot;

[Tool]
public partial class StatusWindow : Window
{
	private Vector2I _baseWindowSize, _baseMinimumWindowSize;
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
	[Export] public string PendingClickFormat { get; set; } = "{count} click waiting to sync";
	[Export] public string PendingClicksFormat { get; set; } = "{count} clicks waiting to sync";
	[Export] public string ServiceStatusFormat { get; set; } = "Service: {status}";
	[Export] public string SteamStatusFormat { get; set; } = "{status}";
	[Export] public string UnavailableStatusText { get; set; } = "Unavailable";
	[ExportGroup("Connection summary text")]
	[Export(PropertyHint.MultilineText)] public string SteamDisabledText { get; set; } = "Steam connection is disabled for this run.";
	[Export(PropertyHint.MultilineText)] public string SteamUnavailableText { get; set; } = "Steam is unavailable. Open Steam, then restart Pet Da Dog.";
	[Export(PropertyHint.MultilineText)] public string SyncInterruptedText { get; set; } = "Pet syncing is interrupted. The app will retry while it is open.";
	[Export(PropertyHint.MultilineText)] public string ConnectingText { get; set; } = "Connecting to the pet service…";
	[Export(PropertyHint.MultilineText)] public string ConnectedText { get; set; } = "Connected to Steam and the pet service.";
	[ExportGroup("Patrol summary text")]
	[Export(PropertyHint.MultilineText)] public string NoPatrolRouteText { get; set; } = "No patrol route set. Your dog uses its usual walk.";
	[Export] public string PatrolRouteFormat { get; set; } = "{count} stops · {loop}";
	[Export] public string PatrolEnabledText { get; set; } = "Patrol is on.";
	[Export] public string PatrolDisabledText { get; set; } = "Patrol is off; your dog uses its usual walk.";
	[Export(PropertyHint.MultilineText)] public string PatrolSummaryFormat { get; set; } = "{route}\n{state}";
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
		if (Engine.IsEditorHint())
		{
			PreviewTab(); SetProcess(false); return;
		}
		Visible = false;
		_baseWindowSize = Size; _baseMinimumWindowSize = MinSize;
		_baseContentScaleFactor = ContentScaleFactor;
		BindScene(); ConnectControls();
		BindInventoryControls();
		_editingSession = GetNodeOrNull<AccessoryEditingSession>("/root/AccessoryEditingSession");
		_patrolRoute = GetNodeOrNull<PatrolRoute>("/root/PatrolRoute");
		CloseRequested += () => { _editingSession?.SetActive(false); SaveLayout(); Hide(); };
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
		if (!Engine.IsEditorHint()) SyncEditingState();
		if (!Engine.IsEditorHint()) UpdateInventoryControls();
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
		_pendingLabel = GetNode<Label>("%PendingPets");
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
		_dogTransparencySlider = GetNode<HSlider>("%DogTransparency");
		_dogScaleSlider = GetNode<HSlider>("%DogScale");
		_uiScaleSlider = GetNode<HSlider>("%UiScale");
		_dogTransparencyValueLabel = GetNode<Label>("%DogTransparencyValue");
		_dogScaleValueLabel = GetNode<Label>("%DogScaleValue");
		_uiScaleValueLabel = GetNode<Label>("%UiScaleValue");
		_editPatrolButton = GetNode<Button>("%EditPatrolRoute");
		_clearPatrolButton = GetNode<Button>("%ClearPatrolRoute");
		_usePatrolCheck = GetNode<CheckBox>("%UsePatrolRoute");
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
		GetNode<Button>("%QuitPetDaDog").Pressed += () => GetTree().Quit();
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
		_dogTransparencySlider!.ValueChanged += value => { if (!_refreshingControls) _settings?.SetDogTransparency((float)value); };
		_dogScaleSlider!.ValueChanged += value => { if (!_refreshingControls) _settings?.SetDogScale((float)value); };
		_uiScaleSlider!.ValueChanged += value => { if (!_refreshingControls) _settings?.SetUiScale((float)value); };
		_editPatrolButton!.Pressed += BeginPatrolEdit;
		_clearPatrolButton!.Pressed += () => _patrolRoute?.ClearRoute();
		_usePatrolCheck!.Toggled += enabled => { if (!_syncingPatrol) _patrolRoute?.SetEnabled(enabled); };
	}
	private void SyncEditingState() => _editingSession?.SetActive(Visible && Mode != ModeEnum.Minimized
		&& _activeTab == StatusTab.Items && _patrolRoute?.IsEditing != true);
	public override void _ExitTree()
	{
		if (Engine.IsEditorHint()) return;
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
	public void UpdateStatus(long? confirmedPets, int pendingGrantCount, string backendStatus, string steamStatus,
		bool steamReady = false, bool backendReady = false)
	{
		if (Engine.IsEditorHint() || _petsValueLabel == null) return;
		var retrying = backendStatus.Contains("retry", StringComparison.OrdinalIgnoreCase)
			|| backendStatus.Contains("failed", StringComparison.OrdinalIgnoreCase);
		_petsValueLabel!.Text = confirmedPets.HasValue
			? PetsTotalFormat.Replace("{count}", confirmedPets.Value.ToString("N0", CultureInfo.InvariantCulture))
			: steamReady ? PetsAwaitingText : PetsUnavailableText;
		_pendingLabel!.Visible = pendingGrantCount > 0;
		_pendingLabel.Text = (pendingGrantCount == 1 ? PendingClickFormat : PendingClicksFormat).Replace("{count}", pendingGrantCount.ToString(CultureInfo.InvariantCulture));
		_backendStatusLabel!.Text = ServiceStatusFormat.Replace("{status}", NormalizeStatus(backendStatus));
		_steamStatusLabel!.Text = SteamStatusFormat.Replace("{status}", NormalizeStatus(steamStatus));
		_connectionSummary!.Text = !steamReady ? steamStatus.StartsWith("Steam is disabled", StringComparison.OrdinalIgnoreCase)
			? SteamDisabledText : SteamUnavailableText
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
		_syncingPatrol = true;
		_usePatrolCheck.SetPressedNoSignal(_patrolRoute?.Enabled == true);
		_syncingPatrol = false;
		var stops = count <= 10 ? string.Join(" → ", Enumerable.Range(1, count)) : $"1 → 2 → 3 → … → {count}";
		var routeDescription = PatrolRouteFormat.Replace("{count}", count.ToString(CultureInfo.InvariantCulture)).Replace("{loop}", stops + " → 1");
		_patrolSummary!.Text = count == 0 ? NoPatrolRouteText : PatrolSummaryFormat.Replace("{route}", routeDescription)
			.Replace("{state}", _patrolRoute?.Enabled == true ? PatrolEnabledText : PatrolDisabledText);
		_patrolSaveStatus!.Visible = _patrolRoute?.LastSaveSucceeded == false;
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
		_appliedUiScale = scale;
		ContentScaleFactor = _baseContentScaleFactor * scale;
		var size = new Vector2I(Mathf.RoundToInt(_baseWindowSize.X * scale), Mathf.RoundToInt(_baseWindowSize.Y * scale));
		var minimum = new Vector2I(Mathf.RoundToInt(_baseMinimumWindowSize.X * scale), Mathf.RoundToInt(_baseMinimumWindowSize.Y * scale));
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
			MinSize = new Vector2I(Mathf.RoundToInt(_baseMinimumWindowSize.X * _appliedUiScale), Mathf.RoundToInt(_baseMinimumWindowSize.Y * _appliedUiScale)).Min(maximum);
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
