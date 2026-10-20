#if CHECKOUT_SANDBOX
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Steamworks;

// Diagnostic scene compiled only into the isolated CHECKOUT_SANDBOX snapshot.
// The real DesktopPet scene owns its normal transparent window and Steam client.
public partial class CheckoutSandbox : Node
{
	private const uint ExpectedAppId = 4817200;
	private const int PackId = 1200;
	private const bool PreservesLaunchGameId = __PRESERVE_LAUNCH_GAME_ID__;
	private static readonly ulong ValidatedTestSteamId = ulong.Parse("__VALIDATED_TEST_STEAM_ID__", System.Globalization.CultureInfo.InvariantCulture);
	private const string ExpectedPricedSchemaSha256 = "__EXPECTED_SCHEMA_SHA256__";
	private readonly CancellationTokenSource _shutdown = new();
	private DesktopPet _pet = null!;
	private SteamIntegration _steam = null!;
	private BackendPetClient _backend = null!;
	private Window _diagnostic = null!;
	private Label _identity = null!;
	private Label _geometry = null!;
	private Label _price = null!;
	private Label _wallet = null!;
	private Label _result = null!;
	private Button _loadPrices = null!;
	private Button _checkout = null!;
	private Button _refresh = null!;
	private RichTextLabel _events = null!;
	private string _logPath = "";
	private string _sessionPath = "";
	private Session? _session;
	private long? _lastBones;
	private bool _verifiedSdk;
	private bool? _lastOverlayState;
	private bool _action;
	private double _pollElapsed;
	private bool _offlineSmoke;
	private double _offlineElapsed;

	private sealed record Session(ulong SteamId, long BaselineGoldenBones, DateTimeOffset StartedAtUtc, bool CheckoutStarted = false, long? LastConfirmedGoldenBones = null);
	private sealed record Approval(uint AppId, int PackId, ulong SteamId, string SchemaSha256, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc);

	public override void _Ready()
	{
		if (!OS.IsDebugBuild()) { GD.PushError("Checkout sandbox requires a debug export."); GetTree().Quit(1); return; }
		_logPath = ProjectSettings.GlobalizePath("user://checkout-sandbox.log");
		_sessionPath = ProjectSettings.GlobalizePath("user://checkout-session.json");
		Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
		if (File.Exists(_sessionPath))
		{
			try { _session = JsonSerializer.Deserialize<Session>(File.ReadAllText(_sessionPath)); if (_session?.SteamId != ValidatedTestSteamId) _session = null; }
			catch (Exception) { _session = null; }
		}
		GetNode<PetSettings>("/root/PetSettings").DismissWelcome();
		_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>();
		AddChild(_pet);
		_steam = ReadPetField<SteamIntegration>("_steam");
		_backend = ReadPetField<BackendPetClient>("_backend");
		BuildDiagnosticWindow();
		_steam.InventoryChanged += OnInventoryChanged;
		_offlineSmoke = System.Environment.GetEnvironmentVariable("PDD_CHECKOUT_SANDBOX_OFFLINE") == "1";
		Log($"START debug={OS.IsDebugBuild()} inputDisabled=true normalCashFlagUnchanged=true preserveLaunchGameId={PreservesLaunchGameId} profile={OS.GetUserDataDir()}");
		if (_session != null) Log($"RESTART baselineBones={_session.BaselineGoldenBones} checkoutStarted={_session.CheckoutStarted} lastConfirmed={_session.LastConfirmedGoldenBones}");
	}

	public override void _Process(double delta)
	{
		if (_diagnostic == null) return;
		var initialized = _steam.IsInitialized;
		var appId = initialized ? SteamUtils.GetAppID().m_AppId : 0;
		var correctApp = initialized && appId == ExpectedAppId && _steam.SteamId == ValidatedTestSteamId;
		var overlay = initialized && SteamUtils.IsOverlayEnabled();
		if (_lastOverlayState != overlay)
		{
			Log($"OVERLAY_STATE enabled={overlay} initialized={initialized} appId={appId}");
			_lastOverlayState = overlay;
		}
		if (correctApp && !_verifiedSdk) { _verifiedSdk = true; Log($"SDK_VERIFIED appId={appId} steamId={_steam.SteamId} overlayEnabled={overlay}"); }
		_identity.Text = initialized ? $"Steam AppID: {appId} {(correctApp ? "✓ verified" : "WRONG APP — checkout blocked")}\nSteam account: {_steam.SteamId} · Overlay enabled: {overlay}" : _steam.Status;
		_geometry.Text = $"Actual dog window: {GetWindow().Size.X} × {GetWindow().Size.Y} at {GetWindow().Position}\nTransparent: {GetWindow().Transparent} · NoFocus: {GetWindow().Unfocusable}\nDiagnostic window: {_diagnostic.Size.X} × {_diagnostic.Size.Y}";
		_wallet.Text = $"Worker-confirmed Golden Bones: {_backend.ConfirmedGoldenBones?.ToString() ?? "—"}\nNormal Shop cash capability: {_backend.ShopCapabilities.CashPurchasesEnabled} (must stay false)";
		var unlocked = IsUnlocked();
		_loadPrices.Disabled = !correctApp || _action;
		_refresh.Disabled = !_backend.IsAuthenticated || _action;
		_checkout.Disabled = !correctApp || !overlay || !unlocked || !_backend.IsAuthenticated || _backend.ShopCapabilities.CashPurchasesEnabled || !_steam.CurrencyPrices.ContainsKey(PackId) || _action || _steam.IsCheckoutInFlight;
		_checkout.Text = !correctApp ? "Checkout disabled — Steam identity not verified"
			: !overlay ? "Checkout disabled — Steam overlay API reports unavailable"
			: !unlocked ? "Checkout locked — awaiting verified test schema"
			: !_backend.IsAuthenticated ? "Checkout disabled — waiting for authentication"
			: _backend.ShopCapabilities.CashPurchasesEnabled ? "Checkout disabled — public cash must remain off"
			: !_steam.CurrencyPrices.ContainsKey(PackId) ? "Checkout disabled — load the Steam price first"
			: _action || _steam.IsCheckoutInFlight ? "Checkout disabled — another action is in progress"
			: "Start Steam sandbox checkout · 100 Golden Bones";
		if (_backend.ConfirmedGoldenBones is long bones && bones != _lastBones)
		{
			_lastBones = bones;
			Log($"INVENTORY_CONFIRMED goldenBones={bones}");
			if (_session != null)
			{
				var difference = bones - _session.BaselineGoldenBones;
				_session = _session with { LastConfirmedGoldenBones = bones }; SaveSession();
				_result.Text = $"Confirmed inventory change since checkout baseline: {difference:+0;-0;0} Golden Bones.\nBaseline: {_session.BaselineGoldenBones}; current: {bones}.";
				Log($"BASELINE_COMPARISON baseline={_session.BaselineGoldenBones} current={bones} difference={difference} checkoutStarted={_session.CheckoutStarted}");
			}
		}
		_pollElapsed += delta;
		if (_session != null && _backend.IsAuthenticated && !_backend.IsOperationInFlight && !_action && _pollElapsed >= 5)
		{
			_pollElapsed = 0;
			_ = RefreshInventoryAsync(false);
		}
		if (_offlineSmoke)
		{
			_offlineElapsed += delta;
			if (_offlineElapsed >= 2) { Log("CHECKOUT_SANDBOX_OFFLINE_PASS: actual dog and diagnostic window loaded; checkout blocked; no Steam initialization."); GetTree().Quit(); }
		}
	}

	private void BuildDiagnosticWindow()
	{
		GetViewport().GuiEmbedSubwindows = false;
		_diagnostic = new Window { Title = "Pet Da Dog · Steam checkout sandbox", Size = new Vector2I(690, 650), MinSize = new Vector2I(620, 560), Transparent = false, Unfocusable = false };
		AddChild(_diagnostic);
		_diagnostic.CloseRequested += () => GetTree().Quit();
		_diagnostic.FocusEntered += () => _steam.NotifyApplicationFocusReturned();
		var background = new ColorRect { Color = new Color("202c25"), MouseFilter = Control.MouseFilterEnum.Ignore };
		_diagnostic.AddChild(background); background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		var margin = new MarginContainer(); _diagnostic.AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 18);
		var column = new VBoxContainer(); column.AddThemeConstantOverride("separation", 10); margin.AddChild(column);
		AddText(column, "Steam checkout verification · 100 Golden Bones", 21);
		AddText(column, "Only for the confirmed developer account on this unreleased app.\nCancel one checkout first; then complete one checkout. No inputs earn Pets in this build.", 14);
		_identity = AddText(column, "Connecting to Steam…", 15);
		_geometry = AddText(column, "", 13);
		_price = AddText(column, "Steam-localized price: not loaded. No estimated price is used.", 15);
		_wallet = AddText(column, "", 15);
		_loadPrices = AddButton(column, "Load price from Steam", () => _ = LoadPricesAsync());
		_checkout = AddButton(column, "Checkout locked — awaiting verified test schema", () => _ = CheckoutAsync());
		_refresh = AddButton(column, "Refresh confirmed inventory / after cancelling", () => { _steam.NotifyApplicationFocusReturned(); _ = RefreshInventoryAsync(true); });
		_result = AddText(column, "Starting checkout does not confirm payment or credit Golden Bones.\nReport whether the Steam overlay is fully visible and clickable.", 14);
		_events = new RichTextLabel { ScrollFollowing = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 100), BbcodeEnabled = false };
		column.AddChild(_events);
		_diagnostic.PopupCentered();
	}

	private static Label AddText(VBoxContainer parent, string text, int size)
	{
		var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		label.AddThemeFontSizeOverride("font_size", size); parent.AddChild(label); return label;
	}
	private static Button AddButton(VBoxContainer parent, string text, Action action)
	{
		var button = new Button { Text = text }; button.Pressed += action; parent.AddChild(button); return button;
	}
	private async Task LoadPricesAsync()
	{
		if (_action || !_steam.IsInitialized || SteamUtils.GetAppID().m_AppId != ExpectedAppId) return;
		_action = true;
		try
		{
			_steam.ConfigureCommerce(true, false, new[] { PackId });
			var loaded = await _steam.RequestPricesAsync(new[] { PackId }, _shutdown.Token);
			if (loaded && _steam.CurrencyPrices.TryGetValue(PackId, out var price))
			{
				_price.Text = $"Steam-localized price for 100 Golden Bones: {price.FormattedPrice}";
				Log($"PRICE_READY pack={PackId} currency={price.Currency} steamHundredths={price.CurrentPrice} display={price.FormattedPrice}");
			}
			else { _price.Text = _steam.CommerceStatus; Log("PRICE_UNAVAILABLE " + _steam.CommerceStatus); }
		}
		finally { _steam.ConfigureCommerce(false, false, new[] { PackId }); _action = false; }
	}
	private async Task CheckoutAsync()
	{
		if (_action || _checkout.Disabled || !IsUnlocked() || SteamUtils.GetAppID().m_AppId != ExpectedAppId || !SteamUtils.IsOverlayEnabled() || _backend.ConfirmedGoldenBones is not long baseline) return;
		_action = true;
		try
		{
			_session = new(_steam.SteamId, baseline, DateTimeOffset.UtcNow); SaveSession();
			Log($"CHECKOUT_REQUESTED pack={PackId} baselineBones={baseline} appId={SteamUtils.GetAppID().m_AppId} overlayEnabled={SteamUtils.IsOverlayEnabled()} rootSize={GetWindow().Size}");
			_steam.ConfigureCommerce(true, false, new[] { PackId });
			var started = await _steam.StartCurrencyPurchaseAsync(PackId, _shutdown.Token);
			_session = _session with { CheckoutStarted = started }; SaveSession();
			_result.Text = started ? "Steam checkout started. This is not payment confirmation.\nComplete or cancel in Steam, then verify the confirmed inventory." : _steam.CommerceStatus;
			Log($"CHECKOUT_START_RESULT started={started} confirmedBones={_backend.ConfirmedGoldenBones} status={_steam.CommerceStatus}");
		}
		finally { _steam.ConfigureCommerce(false, false, new[] { PackId }); _action = false; }
	}
	private async Task RefreshInventoryAsync(bool report)
	{
		if (_action || !_backend.IsAuthenticated) return;
		_action = true;
		try
		{
			var confirmed = await _backend.RefreshShopAsync(_shutdown.Token);
			if (report) Log($"INVENTORY_REFRESH confirmed={confirmed} goldenBones={_backend.ConfirmedGoldenBones} status={_backend.ShopStatus}");
		}
		finally { _action = false; }
	}
	private bool IsUnlocked()
	{
		try
		{
			var approval = JsonSerializer.Deserialize<Approval>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "checkout-approval.json")));
			var now = DateTimeOffset.UtcNow;
			return approval != null && approval.AppId == ExpectedAppId && approval.PackId == PackId && approval.SteamId == ValidatedTestSteamId &&
				_steam.SteamId == ValidatedTestSteamId && string.Equals(approval.SchemaSha256, ExpectedPricedSchemaSha256, StringComparison.OrdinalIgnoreCase) &&
				approval.IssuedAtUtc <= now && approval.ExpiresAtUtc > now && approval.ExpiresAtUtc - approval.IssuedAtUtc <= TimeSpan.FromMinutes(30) &&
				approval.IssuedAtUtc.Offset == TimeSpan.Zero && approval.ExpiresAtUtc.Offset == TimeSpan.Zero;
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return false; }
	}
	private T ReadPetField<T>(string name) where T : class => typeof(DesktopPet).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_pet) as T ?? throw new InvalidOperationException("Missing diagnostic pet field: " + name);
	internal static void LogSteamLaunchEnvironment(string stage)
	{
		// Only these numeric launch identifiers may be logged; never enumerate environment variables.
		var identifiers = string.Join(" ", new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId" }.Select(name =>
		{
			var value = System.Environment.GetEnvironmentVariable(name);
			return name + "=" + (string.IsNullOrWhiteSpace(value) ? "<unset>"
				: value.Length <= 32 && value.All(character => character >= '0' && character <= '9') ? value : "<non-numeric>");
		}));
		var line = DateTimeOffset.UtcNow.ToString("O") + $" LAUNCH_IDS stage={stage} {identifiers}";
		GD.Print(line);
		var path = ProjectSettings.GlobalizePath("user://checkout-sandbox.log");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.AppendAllText(path, line + System.Environment.NewLine);
	}
	private void SaveSession() => File.WriteAllText(_sessionPath, JsonSerializer.Serialize(_session));
	private void OnInventoryChanged() => Log("STEAM_INVENTORY_OR_FOCUS_SIGNAL: requesting the authoritative Worker snapshot; no local credit.");
	private void Log(string message)
	{
		var line = DateTimeOffset.UtcNow.ToString("O") + " " + message;
		GD.Print(line); _events?.AppendText(line + "\n"); File.AppendAllText(_logPath, line + System.Environment.NewLine);
	}
	public override void _ExitTree()
	{
		_shutdown.Cancel();
		if (_steam != null) _steam.InventoryChanged -= OnInventoryChanged;
	}
}
#endif
