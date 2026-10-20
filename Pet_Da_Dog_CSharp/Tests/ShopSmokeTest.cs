using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

public partial class ShopSmokeTest : Node
{
	private int _assertions;
	public override void _Ready() => CallDeferred(nameof(Run));
	private async void Run()
	{
		StatusWindow? menu = null;
		var result = 0;
		try
		{
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1"
				&& System.Environment.GetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT") == "1", "Test disables live economy and desktop input.");
			var fake = new ShopHandler();
			using var backend = new BackendPetClient(new System.Net.Http.HttpClient(fake) { BaseAddress = new Uri("https://shop.invalid/") });
			typeof(BackendPetClient).GetField("_sessionToken", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(backend, "isolated-fixture");
			Require(await backend.RefreshShopAsync(CancellationToken.None), "Loads authoritative fake Steam Shop state.");
			var wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
			wardrobe.SetSteamInventory(backend.InventoryItems);
			var settings = GetNode<PetSettings>("/root/PetSettings");
			settings.DismissWelcome();
			menu = ResourceLoader.Load<PackedScene>("res://StatusWindow.tscn").Instantiate<StatusWindow>();
			AddChild(menu);
			menu.Configure(settings);
			menu.ConfigureSteamInventory(backend, wardrobe, CancellationToken.None);
			menu.GetNode<Button>("%ShopTab").EmitSignal(BaseButton.SignalName.Pressed);
			menu.ShowStatusWindow();
			await Settle();
			Require(menu.GetNode<Label>("%ShopPets").Text.Contains("900"), "Pets header uses confirmed balance.");
			Require(menu.GetNode<Label>("%ShopBones").Text.Contains("800"), "Bones header uses confirmed balance.");
			Require(!menu.GetNode<Button>("%BuyDigBox").Disabled && !menu.GetNode<Button>("%BuyDogBox").Disabled, "Funded enabled Shop accepts purchases.");
			Require(menu.GetNode<Button>("%SteamMarket").Disabled, "Market remains gated.");
			Require(!menu.GetNode<Control>("%ShopDeveloperControls").Visible, "Test controls absent from ordinary session.");
			Require(StatusWindow.RecyclableQuantity(1, 0) == 0 && StatusWindow.RecyclableQuantity(3, 2) == 1
				&& StatusWindow.RecyclableQuantity(2, 9) == 0, "Recycling protects last copy and current outfit.");
			menu.GetNode<Button>("%ShopDogsTab").EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			Require(menu.GetNode<GridContainer>("%ShopItemGrid").GetChildCount() == 73, "Direct dog grid contains all 73 dogs.");
			menu.GetNode<LineEdit>("%ShopSearch").Text = "Shiba";
			await Settle();
			Require(menu.GetNode<GridContainer>("%ShopItemGrid").GetChildCount() == 2, "Dog search filters names.");
			menu.GetNode<LineEdit>("%ShopSearch").Text = "";
			menu.GetNode<Button>("%ShopAccessoriesTab").EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			Require(menu.GetNode<GridContainer>("%ShopItemGrid").GetChildCount() == 63, "Direct accessory grid contains all 63 accessories.");
			menu.GetNode<Button>("%ShopRecycleTab").EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			Require(menu.GetNode<GridContainer>("%ShopItemGrid").GetChildCount() == 2, "Recycling shows only owned spares.");
			menu.GetNode<Button>("%ShopBonesTab").EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			Require(menu.GetNode<VBoxContainer>("%ShopPacks").GetChildren().OfType<Button>().All(button => button.Disabled), "Cash packs cannot start checkout while gated.");
			menu.GetNode<Button>("%ShopBoxesTab").EmitSignal(BaseButton.SignalName.Pressed);
			fake.BlockGrant = true;
			backend.EnqueuePetGrant(Guid.NewGuid()); backend.Tick(1, CancellationToken.None);
			await Settle();
			Require(backend.IsOperationInFlight && !backend.IsShopOperationInFlight, "Pending ordinary Pets sync is not a foreground purchase.");
			Require(!menu.GetNode<Button>("%BuyDigBox").Disabled && !menu.GetNode<Button>("%BuyDogBox").Disabled,
				"Purchase buttons stay enabled during desktop input synchronization.");
			var reveals = 0; var rewards = 0;
			menu.BoxRevealRequested += _ => reveals++;
			menu.BoxRewardConfirmed += (_, acknowledge) => { rewards++; acknowledge?.Invoke(); };
			menu.GetNode<Button>("%BuyDigBox").EmitSignal(BaseButton.SignalName.Pressed);
			menu.GetNode<Button>("%BuyDigBox").EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			Require(reveals == 1 && fake.PurchaseCount == 0, "One action is accepted behind the existing grant; duplicate clicks are ignored.");
			fake.ReleaseGrant.TrySetResult();
			await Settle(); await Settle();
			Require(fake.PurchaseCount == 1 && rewards == 1, "Queued purchase executes once and reveals its confirmed receipt.");
			Require(menu.GetNode<Button>("%BuyDigBox").Disabled && !menu.GetNode<Button>("%BuyDogBox").Disabled,
				"The cooldown applies only to the Dig Box.");
			Require(menu.GetNode<Label>("%DigCooldown").Text.StartsWith("Next dig in"), "Cooldown is visible.");
			foreach (var size in new[] { new Vector2I(560, 600), new Vector2I(960, 780) })
				foreach (var font in new[] { 0.75f, 1.5f })
				{
					menu.Size = size; settings.SetFontScale(font); await Settle();
					var scroll = menu.GetNode<ScrollContainer>("%ShopScroll");
					Require(scroll.GetHScrollBar().MaxValue <= scroll.Size.X + 2, "Shop content fits width at independent font sizes.");
					var initial = menu.Size; scroll.ScrollVertical = 100000; await Settle();
					Require(menu.Size == initial, "Scrolling and font size do not change the menu window.");
				}
			foreach (var section in new[] { "Boxes", "Dogs", "Accessories" })
			{
				settings.SetFontScale(1); settings.SetUiScale(1); menu.Size = new Vector2I(960, 780);
				menu.GetNode<Button>($"%Shop{section}Tab").EmitSignal(BaseButton.SignalName.Pressed);
				await Settle();
				settings.SetFontScale(PetSettings.MaxFontScale); settings.SetUiScale(PetSettings.MaxUiScale); menu.Size = menu.MinSize;
				await Settle(); await Settle();
				Require(menu.GetVisibleRect().Grow(1).Encloses(menu.GetNode<Control>("Background").GetGlobalRect()),
					$"{section} reflows from multiple columns into the actual minimum canvas at maximum font and menu scales.");
				var shopScroll = menu.GetNode<ScrollContainer>("%ShopScroll");
				Require(shopScroll.Size.Y >= 44 && shopScroll.GetHScrollBar().MaxValue <= shopScroll.Size.X + 2,
					$"{section} retains reachable vertical scrolling without horizontal overflow.");
			}
			settings.SetUiScale(1); settings.SetFontScale(1); menu.Size = new Vector2I(740, 820);
			menu.GetNode<Button>("%ShopBoxesTab").EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			if (DisplayServer.GetName() != "headless")
			{
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				using var image = menu.GetTexture().GetImage();
				Require(image.SavePng("res://.godot/shop-redesign-smoke.png") == Error.Ok, "Shop screenshot saved for visual review.");
				foreach (var section in new[] { "Dogs", "Accessories", "Recycle" })
				{
					menu.GetNode<Button>($"%Shop{section}Tab").EmitSignal(BaseButton.SignalName.Pressed);
					await Settle(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
					using var sectionImage = menu.GetTexture().GetImage();
					Require(sectionImage.SavePng($"res://.godot/shop-{section.ToLowerInvariant()}-smoke.png") == Error.Ok,
						$"{section} Shop grid screenshot saved for visual review.");
				}
			}
			GD.Print($"SHOP_SMOKE_PASS: {_assertions} assertions");
		}
		catch (Exception exception) { result = 1; GD.PushError($"SHOP_SMOKE_FAIL: {exception}"); }
		finally { menu?.QueueFree(); GetTree().Quit(result); }
	}
	private async Task Settle()
	{
		await ToSignal(GetTree().CreateTimer(0.18), SceneTreeTimer.SignalName.Timeout);
		for (var i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}
	private void Require(bool condition, string message) { _assertions++; if (!condition) throw new Exception(message); }

	private sealed class ShopHandler : HttpMessageHandler
	{
		public bool BlockGrant;
		public readonly TaskCompletionSource ReleaseGrant = new();
		public int PurchaseCount;
		private DateTimeOffset? _nextDig;
		private readonly List<ShopReceipt> _receipts = new();
		private long _pets = 900;
		private SteamInventoryItem[] Items => new[] { new SteamInventoryItem("10", 100, _pets), new("11", 101, 800), new("12", 2004, 3), new("13", 3000, 2) };
		private readonly ShopCatalog _catalog = new()
		{
			RecycleValue = 5,
			Offers = new[] {
				new ShopOffer { Id = "dig", Name = "Dig Box", Kind = "box", CurrencyItemDefId = 100, Price = 500, RewardItemDefIds = new[] { 3000, 2004 }, CooldownSeconds = 1200 },
				new ShopOffer { Id = "dog", Name = "Premium Dog Box", Kind = "box", CurrencyItemDefId = 101, Price = 50, RewardItemDefIds = new[] { 3001 } },
				new ShopOffer { Id = "accessory", Name = "Premium Accessory Box", Kind = "box", CurrencyItemDefId = 101, Price = 25, RewardItemDefIds = new[] { 2000 } }
			}.Concat(SteamCosmeticCatalog.All.Select(c => new ShopOffer { Id = $"{c.Kind}:{c.ItemDefId}", Name = c.Name, Kind = c.Kind,
				CurrencyItemDefId = 101, Price = c.Kind == "dog" ? 200 : 100, RewardItemDefIds = new[] { c.ItemDefId } })).ToArray(),
			Packs = new[] { new ShopCurrencyPack { ItemDefId = 1200, Quantity = 100, TestPriceUsd = 0.99m }, new() { ItemDefId = 1201, Quantity = 550, TestPriceUsd = 4.99m }, new() { ItemDefId = 1202, Quantity = 1200, TestPriceUsd = 9.99m } }
		};
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.RequestUri!.AbsolutePath == "/v1/pets/grant")
			{
				if (BlockGrant) await ReleaseGrant.Task.WaitAsync(cancellationToken);
				_pets++;
				return Response(new { pets = _pets, items = Items, granted = 1 });
			}
			if (request.RequestUri.AbsolutePath == "/v2/shop/purchase")
			{
				PurchaseCount++;
				var body = await request.Content!.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: cancellationToken);
				_pets -= 500; _nextDig = DateTimeOffset.UtcNow.AddMinutes(20);
				_receipts.Add(new ShopReceipt { ClientEventId = body.GetProperty("clientEventId").GetGuid(), Kind = "purchase", OfferId = "dig",
					ReceivedItems = new[] { new SteamInventoryItem("13", 3000, 1) }, CompletedAt = DateTimeOffset.UtcNow, IsDuplicate = true });
			}
			if (request.RequestUri.AbsolutePath == "/v2/shop/ack") _receipts.Clear();
			return Response(new { pets = _pets, goldenBones = 800, items = Items, economyVersion = "golden-bones-v1", serverTime = DateTimeOffset.UtcNow,
				nextDigAt = _nextDig, catalog = _catalog, capabilities = new { shopEnabled = true }, receipts = _receipts.ToArray(), status = "complete", receivedItems = _receipts.LastOrDefault()?.ReceivedItems });
		}
		private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
	}
}
