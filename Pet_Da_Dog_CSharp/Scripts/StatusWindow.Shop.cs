using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class StatusWindow
{
	public event Action<string>? BoxRevealRequested;
	public event Action<BoxReward, Action?>? BoxRewardConfirmed;
	public event Action<string>? BoxRevealFailed;
	public event Action<BoxReward, Action?>? DirectRewardConfirmed;
	private SteamIntegration? _shopSteam;
	private string _shopSection = "Boxes", _shopGridSignature = "";
	private bool _shopActionActive, _shopReceiptVisible, _shopPricesRequested;
	private ulong _shopPriceRetryAt;
	private ulong _nextShopRefresh;
	private bool? _compactShopHeader;
	private readonly HashSet<Guid> _presentedShopReceipts = new();
	private readonly Dictionary<string, Button> _shopBoxButtons = new();
	private readonly Dictionary<int, Control> _shopCosmeticCards = new();
	private readonly Dictionary<int, Button> _shopPackButtons = new();
	private ShopCatalog? _renderedShopCatalog;
	private GridContainer? _shopItemGrid;
	private LineEdit? _shopSearch;
	private OptionButton? _shopOwnedFilter;
	private ConfirmationDialog? _recycleConfirmation;
	private int _recycleItem;
	private bool IsShopBusy => _shopActionActive || _shopReceiptVisible || _inventoryBackend?.IsShopOperationInFlight == true
		|| _inventoryBackend?.HasPendingShopOperation == true || _shopSteam?.IsCheckoutInFlight == true
		|| _inventoryBackend?.UnacknowledgedReceipts.Any(receipt => !_presentedShopReceipts.Contains(receipt.ClientEventId)) == true;

	private void BindShopControls()
	{
		_shopItemGrid = GetNode<GridContainer>("%ShopItemGrid");
		_shopSearch = GetNode<LineEdit>("%ShopSearch");
		_shopOwnedFilter = GetNode<OptionButton>("%ShopOwnedFilter");
		_shopSearch.TextChanged += _ => _shopGridSignature = "";
		_shopOwnedFilter.ItemSelected += _ => _shopGridSignature = "";
		foreach (var section in new[] { "Boxes", "Dogs", "Accessories", "Bones", "Recycle" })
			GetNode<Button>($"%Shop{section}Tab").Pressed += () => SelectShopSection(section);
		GetNode<Button>("%AddGoldenBones").Pressed += () => SelectShopSection("Bones");
		foreach (var (id, prefix, button) in new[] { ("dig", "Dig", "BuyDigBox"), ("dog", "Dog", "BuyDogBox"), ("accessory", "Accessory", "BuyAccessoryBox") })
		{
			_shopBoxButtons[id] = GetNode<Button>($"%{button}");
			_shopBoxButtons[id].Pressed += () => _ = PurchaseShopOffer(id);
			GetNode<Button>($"%{prefix}BoxOdds").Pressed += () => ShowBoxOdds(id);
			GetNode<TextureRect>($"%{prefix}BoxArt").Texture = ResourceLoader.Load<Texture2D>($"res://Art/UI/Shop/{prefix}Present1.svg");
		}
		GetNode<Button>("%SteamMarket").Pressed += () => _shopSteam?.OpenMarket();
		GetNode<Button>("%SteamInventoryLink").Pressed += () => _shopSteam?.OpenSteamInventory();
		FocusEntered += OnShopFocusReturned;
		VisibilityChanged += () => { if (Visible) SynchronizeOpenShop(); };
		_recycleConfirmation = new ConfirmationDialog { Title = "Recycle a spare copy", OkButtonText = "Recycle one copy",
			MinSize = new Vector2I(400, 180), Theme = Theme, Exclusive = true };
		AddChild(_recycleConfirmation);
		_recycleConfirmation.Confirmed += () => _ = RecycleShopItem(_recycleItem);
		SelectShopSection("Boxes");
		BindDeveloperShopControls();
	}

	public void ConfigureSteamCommerce(SteamIntegration steam)
	{
		_shopSteam = steam;
		_shopPricesRequested = false;
		_nextShopRefresh = 0;
	}

	private void OnShopFocusReturned()
	{
		_shopSteam?.NotifyApplicationFocusReturned();
		SynchronizeOpenShop();
	}

	private void SynchronizeOpenShop()
	{
		if (!Visible || _activeTab != StatusTab.Shop) return;
		if (_inventoryBackend?.IsAuthenticated == true && !IsShopBusy)
			_ = _inventoryBackend.SyncShopAsync(_inventoryCancellation);
	}

	private void SelectShopSection(string section)
	{
		_shopSection = section;
		_shopGridSignature = "";
		GetNode<Control>("%ShopBoxes").Visible = section == "Boxes";
		GetNode<Control>("%ShopPacks").Visible = section == "Bones";
		_shopItemGrid!.Visible = section is "Dogs" or "Accessories" or "Recycle";
		GetNode<Control>("%ShopSearchRow").Visible = _shopItemGrid.Visible;
		_shopOwnedFilter!.Visible = section != "Recycle";
		foreach (var name in new[] { "Boxes", "Dogs", "Accessories", "Bones", "Recycle" })
			GetNode<Button>($"%Shop{name}Tab").SetPressedNoSignal(name == section);
		GetNode<Label>("%ShopDescription").Text = section switch
		{
			"Dogs" => "Choose exactly the dog you want.",
			"Accessories" => "Pick your next accessory. Each owned copy can be placed on your dog.",
			"Bones" => "Golden Bones buy premium boxes and specific cosmetics.",
			"Recycle" => "Turn one spare copy into Golden Bones. Your last copy and accessories in use are protected. Steam Market sales pay Steam Wallet funds instead.",
			_ => "Find a new friend. One click buys a box and sends your dog digging."
		};
		GetNode<ScrollContainer>("%ShopScroll").ScrollVertical = 0;
		_nextShopRefresh = 0;
	}

	private void UpdateShopControls()
	{
		if (_shopItemGrid == null) return;
		UpdateShopResponsiveHeader();
		if (Time.GetTicksMsec() < _nextShopRefresh) return;
		_nextShopRefresh = Time.GetTicksMsec() + 100;
		var backend = _inventoryBackend;
		var pets = GetNode<Label>("%ShopPets");
		var bones = GetNode<Label>("%ShopBones");
		pets.Text = "Pets: " + (backend?.ConfirmedPets?.ToString("N0") ?? "—");
		pets.TooltipText = pets.Text;
		var boneCount = backend?.ConfirmedGoldenBones?.ToString("N0") ?? "—";
		bones.Text = (_compactShopHeader == true ? "" : "Golden Bones: ") + boneCount;
		bones.TooltipText = "Golden Bones: " + boneCount;
		var capabilities = backend?.ShopCapabilities;
		var catalog = backend?.ShopCatalog;
		var ready = backend?.IsAuthenticated == true && capabilities?.ShopEnabled == true && !IsShopBusy && !backend.HasPendingExchange;
		var remaining = backend?.DigCooldownRemaining ?? TimeSpan.Zero;
		foreach (var (id, button) in _shopBoxButtons)
		{
			var offer = catalog?.Offers.FirstOrDefault(candidate => candidate.Id == id);
			button.Disabled = !ready || offer == null || (id == "dig" && remaining > TimeSpan.Zero);
			if (offer != null) button.Text = $"{(CanAfford(offer) ? "Dig" : "Sync")} · {offer.Price:N0} {(offer.CurrencyItemDefId == 100 ? "Pets" : "Golden Bones")}";
		}
		GetNode<Label>("%DigCooldown").Text = backend?.IsAuthenticated != true ? "Connect to check availability."
			: capabilities?.ShopEnabled != true ? "The new Shop is being prepared."
			: "Ready to dig!";
		if (remaining > TimeSpan.Zero)
			GetNode<Label>("%DigCooldown").Text = $"Next dig in {(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
		GetNode<Control>("%LegacyBoxes").Visible = _displayedBoxes.Count > 0;
		GetNode<Button>("%SteamMarket").Disabled = capabilities?.MarketEnabled != true || _shopSteam?.IsInitialized != true;
		GetNode<Button>("%SteamMarket").TooltipText = capabilities?.MarketEnabled == true
			? "Sell through Steam for Steam Wallet funds." : "Steam Market support is awaiting availability for this game.";
		GetNode<Button>("%SteamInventoryLink").Disabled = _shopSteam?.IsInitialized != true;
		_shopSteam?.ConfigureCommerce(capabilities?.CashPurchasesEnabled == true, capabilities?.MarketEnabled == true,
			catalog?.Packs.Select(pack => pack.ItemDefId) ?? Array.Empty<int>());
		if (!ReferenceEquals(_renderedShopCatalog, catalog))
		{
			_renderedShopCatalog = catalog;
			_shopGridSignature = "";
			BuildCurrencyPacks();
		}
		if (!_shopPricesRequested && Time.GetTicksMsec() >= _shopPriceRetryAt && capabilities?.CashPurchasesEnabled == true && _shopSteam?.IsInitialized == true && catalog?.Packs.Length > 0)
		{
			_shopPricesRequested = true;
			_ = LoadShopPrices();
		}
		UpdateCurrencyPacks(ready);
		UpdateShopGrid(ready);
		ResizeShopGrid(GetNode<GridContainer>("%ShopBoxGrid"), 210);
		ResizeShopGrid(_shopItemGrid, 160);
		UpdateDeveloperShopControls();
		if (!_shopActionActive && !_shopReceiptVisible && _shopSteam?.IsCheckoutInFlight != true) PresentNextShopReceipt(false);
	}

	private void UpdateShopResponsiveHeader()
	{
		// At the smallest window with maximum independent UI/font scaling there are
		// only ~320 logical pixels across. Keep balances fixed without consuming the
		// entire scroll viewport: the currency icon labels the compact exact total.
		var canvas = GetVisibleRect().Size;
		var compact = canvas.X < 400 || canvas.Y < 420;
		if (_compactShopHeader == compact) return;
		_compactShopHeader = compact;
		var margin = GetNode<MarginContainer>("Background/Margin/Layout/Pages/ShopPage/Margin");
		margin.AddThemeConstantOverride("margin_top", compact ? 2 : 12);
		margin.AddThemeConstantOverride("margin_bottom", compact ? 2 : 12);
		GetNode<VBoxContainer>("Background/Margin/Layout/Pages/ShopPage/Margin/ShopLayout")
			.AddThemeConstantOverride("separation", compact ? 4 : 8);
		GetNode<TextureRect>("%ShopBonesIcon").Visible = compact;
		foreach (var label in new[] { GetNode<Label>("%ShopPets"), GetNode<Label>("%ShopBones") })
		{
			label.AutowrapMode = compact ? TextServer.AutowrapMode.Off : TextServer.AutowrapMode.WordSmart;
			label.ClipText = compact;
			label.TextOverrunBehavior = compact ? TextServer.OverrunBehavior.TrimEllipsis : TextServer.OverrunBehavior.NoTrimming;
		}
		GetNode<ScrollContainer>("%ShopScroll").CustomMinimumSize = new Vector2(0, compact ? 44 : 0);
		_nextShopRefresh = 0;
	}

	private bool CanAfford(ShopOffer offer) => (offer.CurrencyItemDefId == 100 ? _inventoryBackend?.ConfirmedPets : _inventoryBackend?.ConfirmedGoldenBones) >= offer.Price;
	private long OwnedShopQuantity(int itemDefId) => _inventoryBackend?.InventoryItems.Where(item => item.ItemDefId == itemDefId).Sum(item => item.Quantity) ?? 0;
	private int EquippedShopQuantity(SteamCosmeticDefinition cosmetic) => cosmetic.Kind == "accessory" ? _inventoryWardrobe?.GetEquippedCount(cosmetic.AccessoryId) ?? 0 : 0;
	public static long RecyclableQuantity(long owned, long equipped) => Math.Max(0, owned - Math.Max(1, equipped));

	private void UpdateShopGrid(bool ready)
	{
		if (!_shopItemGrid!.Visible) { GetNode<Control>("%ShopEmpty").Visible = false; return; }
		var catalog = _inventoryBackend?.ShopCatalog;
		var cosmetics = SteamCosmeticCatalog.All.Where(item => _shopSection == "Recycle" || item.Kind == (_shopSection == "Dogs" ? "dog" : "accessory"))
			.Where(item => item.Name.Contains(_shopSearch!.Text.Trim(), StringComparison.OrdinalIgnoreCase))
			.Where(item => _shopSection == "Recycle" ? RecyclableQuantity(OwnedShopQuantity(item.ItemDefId), EquippedShopQuantity(item)) > 0
				: _shopOwnedFilter!.Selected == 1 ? OwnedShopQuantity(item.ItemDefId) > 0 : _shopOwnedFilter.Selected != 2 || OwnedShopQuantity(item.ItemDefId) == 0).ToArray();
		var signature = _shopSection + ":" + string.Join(',', cosmetics.Select(item => item.ItemDefId));
		if (_shopGridSignature != signature)
		{
			_shopGridSignature = signature;
			foreach (var child in _shopItemGrid.GetChildren()) { _shopItemGrid.RemoveChild(child); child.QueueFree(); }
			_shopCosmeticCards.Clear();
			var scene = ResourceLoader.Load<PackedScene>("res://UI/ShopCosmeticCard.tscn");
			foreach (var cosmetic in cosmetics)
			{
				var card = scene.Instantiate<PanelContainer>();
				card.Name = $"ShopItem_{cosmetic.ItemDefId}";
				card.AddThemeStyleboxOverride("panel", ResourceLoader.Load<StyleBox>("res://UI/Theme/Panel.tres"));
				card.GetNode<TextureRect>("%Preview").Texture = SteamCosmeticCatalog.CroppedTexture(cosmetic.UiAssetPath);
				card.GetNode<Label>("%Title").Text = cosmetic.Name;
				card.GetNode<Button>("%Purchase").Pressed += () =>
				{
					if (_shopSection == "Recycle") ConfirmShopRecycle(cosmetic);
					else _ = PurchaseShopOffer($"{cosmetic.Kind}:{cosmetic.ItemDefId}");
				};
				_shopItemGrid.AddChild(card);
				_shopCosmeticCards[cosmetic.ItemDefId] = card;
			}
		}
		foreach (var cosmetic in cosmetics)
		{
			var card = _shopCosmeticCards[cosmetic.ItemDefId];
			var owned = OwnedShopQuantity(cosmetic.ItemDefId);
			var equipped = EquippedShopQuantity(cosmetic);
			var recycle = _shopSection == "Recycle";
			var offer = catalog?.Offers.FirstOrDefault(item => item.Id == $"{cosmetic.Kind}:{cosmetic.ItemDefId}");
			card.GetNode<Label>("%Owned").Text = recycle ? $"Owned: {owned} · In use: {equipped}\nSpare: {RecyclableQuantity(owned, equipped)}" : $"Owned: {owned}";
			var button = card.GetNode<Button>("%Purchase");
			button.Text = recycle ? $"Recycle 1 · +{catalog?.RecycleValue ?? 5} Bones" : offer == null ? "Unavailable" : $"{(CanAfford(offer) ? "Buy" : "Sync")} · {offer.Price:N0} Bones";
			button.Disabled = !ready || (recycle ? catalog == null || RecyclableQuantity(owned, equipped) == 0 : offer == null);
		}
		var empty = GetNode<Label>("%ShopEmpty");
		empty.Visible = cosmetics.Length == 0;
		empty.Text = _shopSection == "Recycle" ? "No spare copies to recycle. Keep collecting!" : "No matching items.";
	}

	private void ResizeShopGrid(GridContainer grid, float minimum)
	{
		var children = grid.GetChildren().OfType<Control>().Where(child => child.Visible).ToArray();
		if (children.Length == 0) return;
		var scroll = GetNode<ScrollContainer>("%ShopScroll");
		var outer = GetNode<MarginContainer>("Background/Margin");
		var inner = GetNode<MarginContainer>("Background/Margin/Layout/Pages/ShopPage/Margin");
		var horizontalInsets = GetNode<PanelContainer>("Background").GetThemeStylebox("panel").GetMinimumSize().X
			+ outer.GetThemeConstant("margin_left") + outer.GetThemeConstant("margin_right")
			+ GetNode<PanelContainer>("%ShopPage").GetThemeStylebox("panel").GetMinimumSize().X
			+ inner.GetThemeConstant("margin_left") + inner.GetThemeConstant("margin_right");
		// A stale multi-column minimum can itself expand the ScrollContainer during a
		// shrink. Bound by the actual window canvas so that feedback cannot retain it.
		var width = Math.Min(scroll.Size.X, Math.Max(0, GetVisibleRect().Size.X - horizontalInsets))
			- (scroll.GetVScrollBar().Visible ? scroll.GetVScrollBar().GetCombinedMinimumSize().X : 0);
		var cardWidth = Math.Max(minimum, children.Max(child => child.GetCombinedMinimumSize().X));
		grid.Columns = Math.Clamp((int)((width + 10) / (cardWidth + 10)), 1, children.Length);
	}

	private async Task PurchaseShopOffer(string id)
	{
		var backend = _inventoryBackend;
		var offer = backend?.ShopCatalog?.Offers.FirstOrDefault(candidate => candidate.Id == id);
		// A displayed balance may predate queued earnings or a Steam inventory change.
		// The backend synchronizes first and Steam decides whether this purchase can spend.
		if (backend == null || offer == null || IsShopBusy || !backend.IsAuthenticated || !backend.ShopCapabilities.ShopEnabled) return;
		_shopActionActive = true;
		var box = offer.Kind == "box";
		if (box) BoxRevealRequested?.Invoke(id);
		try
		{
			var complete = await backend.PurchaseOfferAsync(id, _inventoryCancellation);
			if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
			if (complete) PresentNextShopReceipt(box);
			else if (box) BoxRevealFailed?.Invoke(backend.ShopStatus);
		}
		finally { _shopActionActive = false; _nextShopRefresh = 0; }
	}

	private void PresentNextShopReceipt(bool animated)
	{
		if (_shopReceiptVisible || _inventoryBackend == null) return;
		var receipt = _inventoryBackend.UnacknowledgedReceipts.FirstOrDefault(item => !_presentedShopReceipts.Contains(item.ClientEventId));
		if (receipt == null) return;
		var received = receipt.ReceivedItems.FirstOrDefault();
		if (received == null) return;
		_presentedShopReceipts.Add(receipt.ClientEventId);
		_shopReceiptVisible = true;
		Action acknowledge = () =>
		{
			_shopReceiptVisible = false;
			if (_inventoryBackend != null) _ = _inventoryBackend.AcknowledgeReceiptAsync(receipt.ClientEventId, _inventoryCancellation);
		};
		var reward = new BoxReward(received.ItemDefId, (int)Math.Min(received.Quantity, int.MaxValue), receipt.IsDuplicate,
			received.ItemDefId == 101 ? "Golden Bones" : null, received.ItemDefId == 101 ? "res://Art/UI/Shop/GoldenBones.svg" : null);
		if (animated && BoxRewardConfirmed != null) BoxRewardConfirmed(reward, acknowledge);
		else if (DirectRewardConfirmed != null) DirectRewardConfirmed(reward, acknowledge);
		else ShowShopMessage("Received", $"{reward.DisplayName ?? SteamCosmeticCatalog.Find(reward.ItemDefId)?.Name ?? "Item"} × {reward.Quantity}", acknowledge);
	}

	private void ConfirmShopRecycle(SteamCosmeticDefinition cosmetic)
	{
		if (IsShopBusy || RecyclableQuantity(OwnedShopQuantity(cosmetic.ItemDefId), EquippedShopQuantity(cosmetic)) <= 0) return;
		_recycleItem = cosmetic.ItemDefId;
		_recycleConfirmation!.DialogText = $"Recycle one {cosmetic.Name} for {_inventoryBackend?.ShopCatalog?.RecycleValue ?? 5} Golden Bones?\n\nThis consumes that copy. Your last copy and accessories in use will be kept.";
		_recycleConfirmation.PopupCentered();
	}

	private async Task RecycleShopItem(int itemDefId)
	{
		var cosmetic = SteamCosmeticCatalog.Find(itemDefId);
		if (cosmetic == null || _inventoryBackend == null || IsShopBusy) return;
		var equipped = EquippedShopQuantity(cosmetic);
		if (RecyclableQuantity(OwnedShopQuantity(itemDefId), equipped) == 0) return;
		_shopActionActive = true;
		try { await _inventoryBackend.RecycleAsync(itemDefId, 1, equipped, _inventoryCancellation); }
		finally { _shopActionActive = false; _nextShopRefresh = 0; }
	}

	private void ShowBoxOdds(string id)
	{
		var offer = _inventoryBackend?.ShopCatalog?.Offers.FirstOrDefault(item => item.Id == id);
		if (offer == null) { ShowShopMessage("Box contents", "Connect to the Shop to load the current contents and exact odds."); return; }
		var chance = 100m / Math.Max(1, offer.RewardItemDefIds.Length);
		var contents = string.Join("\n", offer.RewardItemDefIds.Select(item => $"{SteamCosmeticCatalog.Find(item)?.Name ?? item.ToString()} — {chance:0.####}%"));
		ShowShopMessage(offer.Name, $"One random cosmetic per box. All {offer.RewardItemDefIds.Length} outcomes are equally likely.\nDuplicates are possible.\n\n{contents}", scroll: true);
	}

	private void ShowShopMessage(string title, string text, Action? closed = null, bool scroll = false)
	{
		var dialog = new AcceptDialog { Title = title, Theme = Theme, MinSize = new Vector2I(400, scroll ? 400 : 180), Exclusive = true };
		if (scroll)
		{
			var container = new ScrollContainer { CustomMinimumSize = new Vector2(400, 400), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
			container.AddThemeStyleboxOverride("panel", ResourceLoader.Load<StyleBox>("res://UI/Theme/Panel.tres"));
			var marginContainer = new MarginContainer { CustomMinimumSize = new Vector2(400, 400) };
			marginContainer.AddThemeConstantOverride("margin_left", 15);
			marginContainer.AddThemeConstantOverride("margin_right", 15);
			marginContainer.AddThemeConstantOverride("margin_top", 15);
			marginContainer.AddThemeConstantOverride("margin_bottom", 150);
			container.AddChild(marginContainer);
			marginContainer.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			dialog.AddChild(container);
		}
		else dialog.DialogText = text;
		AddChild(dialog);
		var done = false;
		void Finish() { if (done) return; done = true; closed?.Invoke(); dialog.QueueFree(); }
		dialog.Confirmed += Finish;
		dialog.Canceled += Finish;
		dialog.PopupCentered();
	}

	private void BuildCurrencyPacks()
	{
		var container = GetNode<VBoxContainer>("%ShopPacks");
		foreach (var child in container.GetChildren()) { container.RemoveChild(child); child.QueueFree(); }
		_shopPackButtons.Clear();
		container.AddChild(new Label { Text = "Purchases are in Steam testing. Public cash sales stay off until pricing and Steam setup are confirmed.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		foreach (var pack in _inventoryBackend?.ShopCatalog?.Packs ?? Array.Empty<ShopCurrencyPack>())
		{
			var button = new Button { Name = $"Pack_{pack.ItemDefId}", Text = $"{pack.Quantity:N0} Golden Bones · Unavailable", Disabled = true,
				AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = "ActionButton" };
			button.Pressed += () => _ = PurchaseCurrencyPack(pack.ItemDefId);
			container.AddChild(button);
			_shopPackButtons[pack.ItemDefId] = button;
		}
	}

	private async Task LoadShopPrices()
	{
		if (_shopSteam == null || _inventoryBackend?.ShopCatalog == null) return;
		if (!await _shopSteam.RequestPricesAsync(_inventoryBackend.ShopCatalog.Packs.Select(pack => pack.ItemDefId), _inventoryCancellation))
		{ _shopPricesRequested = false; _shopPriceRetryAt = Time.GetTicksMsec() + 10000; }
	}

	private void UpdateCurrencyPacks(bool ready)
	{
		foreach (var pack in _inventoryBackend?.ShopCatalog?.Packs ?? Array.Empty<ShopCurrencyPack>())
		{
			if (!_shopPackButtons.TryGetValue(pack.ItemDefId, out var button)) continue;
			var enabled = _inventoryBackend?.ShopCapabilities.CashPurchasesEnabled == true;
			var price = _shopSteam?.CurrencyPrices.GetValueOrDefault(pack.ItemDefId);
			button.Text = $"{pack.Quantity:N0} Golden Bones · {(enabled ? price?.FormattedPrice ?? "Loading Steam price…" : "Not on sale yet")}";
			button.Disabled = !ready || !enabled || price == null || _shopSteam?.IsInitialized != true;
		}
	}

	private async Task PurchaseCurrencyPack(int itemDefId)
	{
		if (IsShopBusy || _shopSteam == null || _inventoryBackend?.ShopCapabilities.CashPurchasesEnabled != true) return;
		_shopActionActive = true;
		try
		{
			if (!await _shopSteam.StartCurrencyPurchaseAsync(itemDefId, _inventoryCancellation))
				ShowShopMessage("Steam checkout", _shopSteam.CommerceStatus);
		}
		finally { _shopActionActive = false; }
	}

	private void BindDeveloperShopControls()
	{
#if DEBUG
		if (System.Environment.GetEnvironmentVariable("PDD_DEV_ECONOMY") != "1") return;
		var container = GetNode<HFlowContainer>("%ShopDeveloperControls");
		foreach (var (label, action) in new (string, Action)[] {
			("Preview dig", () => { BoxRevealRequested?.Invoke("dig"); BoxRewardConfirmed?.Invoke(new BoxReward(3000, DisplayName: "Preview · Australian Shepherd 1"), null); }),
			("Seed test currency", () => { if (_inventoryBackend?.ShopCapabilities.DeveloperControls == true) _ = _inventoryBackend.TestSeedCurrenciesAsync(5000, 1000, _inventoryCancellation); }),
			("Reset test cooldown", () => { if (_inventoryBackend?.ShopCapabilities.DeveloperControls == true) _ = _inventoryBackend.TestResetCooldownAsync(_inventoryCancellation); }) })
		{
			var button = new Button { Text = label, ThemeTypeVariation = "SecondaryButton" };
			button.Pressed += action;
			container.AddChild(button);
		}
#endif
	}

	private void UpdateDeveloperShopControls()
	{
#if DEBUG
		var container = GetNode<Control>("%ShopDeveloperControls");
		container.Visible = System.Environment.GetEnvironmentVariable("PDD_DEV_ECONOMY") == "1"
			&& _inventoryBackend?.ShopCapabilities.DeveloperControls == true;
		foreach (var button in container.GetChildren().OfType<Button>()) button.Disabled = IsShopBusy;
#endif
	}
}
