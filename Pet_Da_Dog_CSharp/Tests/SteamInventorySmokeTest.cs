using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class SteamInventorySmokeTest : Node
{
	private int _assertions;
	private readonly string _savePath = $"res://.godot/steam-cosmetic-smoke-{Guid.NewGuid():N}.cfg";
	public override void _Ready() => CallDeferred(nameof(Run));
	private async void Run()
	{
		var exitCode = 0;
		AccessoryWardrobe? wardrobe = null;
		StatusWindow? menu = null;
		DesktopPet? pet = null;
		try
		{
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1", "Steam smoke uses isolated fake ownership.");
			wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
			wardrobe.StoragePath = _savePath;
			wardrobe.Clear();
			wardrobe.SetSteamOwnership(Array.Empty<int>());
			Require(SteamCosmeticCatalog.All.Count == 136 && SteamCosmeticCatalog.All.Select(item => item.ItemDefId).Distinct().Count() == 136,
				"All 136 Steam cosmetics have unique definition IDs.");
			Require(SteamCosmeticCatalog.All.Count(item => item.Kind == "dog") == 73
				&& SteamCosmeticCatalog.All.Count(item => item.Kind == "accessory") == 63, "Both complete reward pools are mapped.");
			Require(SteamCosmeticCatalog.All.All(item => ResourceLoader.Exists(item.AssetPath) && ResourceLoader.Exists(item.UiAssetPath)),
				"Every mapped source and preview resource exists.");
			foreach (var cosmetic in SteamCosmeticCatalog.All)
				foreach (var path in new[] { cosmetic.AssetPath, cosmetic.UiAssetPath })
				{
					var texture = ResourceLoader.Load<Texture2D>(path);
					using var image = texture?.GetImage();
					Require(image != null && image.GetUsedRect().HasArea(), $"{cosmetic.Name} decodes with visible pixels: {path}.");
				}
			Require(wardrobe.Catalog.Count(item => item.SteamItemDefId > 0) == 63, "Items includes every Steam accessory.");
			var accessory = SteamCosmeticCatalog.All.First(item => item.Kind == "accessory");
			var dog = SteamCosmeticCatalog.All.First(item => item.Kind == "dog");
			wardrobe.Equip(accessory.AccessoryId, Vector2.One * 0.5f);
			wardrobe.SelectDog(dog.ItemDefId);
			Require(wardrobe.GetPlacement(accessory.AccessoryId) == null && wardrobe.SelectedDogItemDefId == 0,
				"Unconfirmed items cannot be equipped or selected.");
			VerifyAccessoryQuantities(wardrobe, accessory);
			GetNode<PetSettings>("/root/PetSettings").DismissWelcome();
			GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession").SetActive(false);
			pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>();
			AddChild(pet);
			await Settle();
			VerifyDogClickQueue(pet);
			wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, dog.ItemDefId });
			wardrobe.Equip(accessory.AccessoryId, Vector2.One * 0.5f);
			wardrobe.SelectDog(dog.ItemDefId);
			await Settle();
			Require(wardrobe.GetPlacement(accessory.AccessoryId) != null, "Confirmed accessory ownership allows equip.");
			Require(pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite").Texture.ResourcePath == dog.AssetPath,
				"Selecting a confirmed dog changes the desktop texture.");
			Require(wardrobe.Save(), "Only cosmetic selection and placement preferences are saved.");
			using var saved = Godot.FileAccess.Open(_savePath, Godot.FileAccess.ModeFlags.Read);
			var savedText = saved.GetAsText();
			Require(!savedText.Contains("owned", StringComparison.OrdinalIgnoreCase) && !savedText.Contains("pets", StringComparison.OrdinalIgnoreCase),
				"Local preferences contain no inventory authority or balance.");
			wardrobe.SetSteamOwnership(Array.Empty<int>());
			Require(wardrobe.GetPlacement(accessory.AccessoryId) == null && !wardrobe.Equipped.Any(item => item.Id == accessory.AccessoryId)
				&& wardrobe.SelectedDogItemDefId == 0, "Removing confirmed ownership hides saved cosmetics and restores starter dog.");
			wardrobe.Undo();
			Require(!wardrobe.Equipped.Any(item => item.Id == accessory.AccessoryId), "Undo cannot bypass Steam ownership.");
			menu = ResourceLoader.Load<PackedScene>("res://StatusWindow.tscn").Instantiate<StatusWindow>();
			AddChild(menu);
			var backend = (BackendPetClient)typeof(DesktopPet).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;
			menu.ConfigureSteamInventory(backend, wardrobe, CancellationToken.None);
			await Settle();
			Require(menu.GetNode<Button>("%BuyDogBox").Disabled && menu.GetNode<Button>("%BuyAccessoryBox").Disabled
				&& menu.GetNode<Button>("%OpenBox").Disabled, "Offline shop disables currency operations.");
			var dogs = menu.GetNode<GridContainer>("%DogGrid");
			RequireDogChoices(dogs, new[] { 0 }, "Empty Steam ownership shows only Starter dog.");
			Require(SelectedDogId(dogs) == 0, "Starter dog is selected with empty ownership.");
			await VerifyOwnedDogSelector(wardrobe, dogs, pet, accessory, dog);
			await VerifyDogGridLayout(menu, wardrobe, dogs, pet);
			if (DisplayServer.GetName() != "headless")
			{
				menu.GetNode<Button>("%ShopTab").EmitSignal(BaseButton.SignalName.Pressed);
				menu.ShowStatusWindow();
				await Settle(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				using var image = menu.GetTexture().GetImage();
				Require(image.SavePng("res://.godot/steam-shop-smoke.png") == Error.Ok, "Shop layout captures for visual review.");
				menu.Hide();
			}
			await VerifyDesktopCounterAndMenuAccess(pet);
			GD.Print($"STEAM_INVENTORY_SMOKE_PASS: {_assertions} assertions");
		}
		catch (Exception exception) { exitCode = 1; GD.PushError($"STEAM_INVENTORY_SMOKE_FAIL: {exception}"); }
		finally
		{
			menu?.QueueFree(); pet?.QueueFree();
			if (wardrobe != null) { wardrobe.Clear(); wardrobe.SelectDog(0); wardrobe.Save(); }
			System.IO.File.Delete(ProjectSettings.GlobalizePath(_savePath));
			GetTree().Quit(exitCode);
		}
	}
	private async Task Settle() { for (var i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
	private void VerifyAccessoryQuantities(AccessoryWardrobe wardrobe, SteamCosmeticDefinition accessory)
	{
		var id = accessory.AccessoryId;
		wardrobe.SetSteamInventory(new[] { new SteamInventoryItem("stack", accessory.ItemDefId, 2) });
		Require(wardrobe.GetOwnedQuantity(id) == 2 && wardrobe.CanAddCopy(id),
			"A Steam stack of quantity two permits two accessory copies.");
		wardrobe.Equip(id, new Vector2(0.25f, 0.3f));
		var extra = wardrobe.CreatePlacementId(id);
		Require(extra != null && extra != id && wardrobe.Find(extra) == wardrobe.Find(id),
			"Another owned copy receives a distinct placement ID mapped to the same artwork.");
		wardrobe.Equip(extra!, new Vector2(0.75f, 0.3f));
		wardrobe.SetTransform(extra!, 1.4f, 35);
		Require(wardrobe.GetEquippedCount(id) == 2 && !wardrobe.CanAddCopy(id) && wardrobe.CreatePlacementId(id) == null,
			"Two equipped copies exhaust a confirmed quantity-two stack.");
		var forgedExtra = $"{id}::{Guid.NewGuid():N}";
		wardrobe.Equip(forgedExtra, Vector2.One);
		Require(wardrobe.GetPlacement(forgedExtra) == null && wardrobe.GetEquippedCount(id) == 2,
			"A caller cannot bypass Steam quantity by inventing another valid placement ID.");
		var expected = wardrobe.Equipped.ToArray();
		wardrobe.SetSteamInventory(new[] { new SteamInventoryItem("stack", accessory.ItemDefId, 1) });
		Require(wardrobe.GetEquippedCount(id) == 1 && wardrobe.GetPlacement(id) == expected[0] && wardrobe.GetPlacement(extra!) == null,
			"Losing one owned copy hides only the excess placement while retaining the first copy.");
		Require(wardrobe.Save(), "Hidden excess placement preferences can be saved.");
		var restored = new AccessoryWardrobe { StoragePath = _savePath };
		AddChild(restored);
		Require(restored.GetOwnedQuantity(id) == 0 && restored.GetEquippedCount(id) == 0 && !restored.CanAddCopy(id),
			"Restart restores no Steam quantity from local cosmetic preferences.");
		restored.SetSteamInventory(new[] { new SteamInventoryItem("fresh", accessory.ItemDefId, 1) });
		Require(restored.GetPlacement(id) == expected[0] && restored.GetPlacement(extra!) == null,
			"A fresh one-copy snapshot reveals only the first saved placement after restart.");
		restored.SetSteamInventory(new[] { new SteamInventoryItem("fresh", accessory.ItemDefId, 2) });
		Require(restored.Equipped.SequenceEqual(expected),
			"A fresh two-copy snapshot restores the hidden placement's ID, position and transform.");
		restored.Free();
		wardrobe.SetSteamInventory(new[] { new SteamInventoryItem("stack", accessory.ItemDefId, 2) });
		Require(wardrobe.Equipped.SequenceEqual(expected), "Regaining quantity restores the retained outfit without equipping again.");
		wardrobe.SetSteamInventory(new[]
		{
			new SteamInventoryItem("first", accessory.ItemDefId, 1), new SteamInventoryItem("second", accessory.ItemDefId, 2),
			new SteamInventoryItem("empty", accessory.ItemDefId, 0), new SteamInventoryItem("invalid", accessory.ItemDefId, -5)
		});
		Require(wardrobe.GetOwnedQuantity(id) == 3 && wardrobe.CanAddCopy(id),
			"Separate inventory rows sum their positive stack quantities without counting invalid rows.");
		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, accessory.ItemDefId });
		Require(wardrobe.GetOwnedQuantity(id) == 2 && wardrobe.GetEquippedCount(id) == 2,
			"Legacy ownership snapshots retain repeated definitions as separate owned copies.");
		wardrobe.Clear(); wardrobe.ClearHistory(); wardrobe.SetSteamOwnership(Array.Empty<int>());
	}
	private async Task VerifyOwnedDogSelector(AccessoryWardrobe wardrobe, GridContainer dogs, DesktopPet pet,
		SteamCosmeticDefinition accessory, SteamCosmeticDefinition firstDog)
	{
		var otherDog = SteamCosmeticCatalog.All.Last(item => item.Kind == "dog");
		var starterCard = DogCard(dogs, 0);
		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId });
		await Settle();
		RequireDogChoices(dogs, new[] { 0 }, "Owned accessories do not appear in the dog selector.");

		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, otherDog.ItemDefId });
		await Settle();
		RequireDogChoices(dogs, new[] { 0, otherDog.ItemDefId }, "Gaining one dog adds only that owned dog beside Starter.");
		var otherCard = DogCard(dogs, otherDog.ItemDefId);
		SelectDogByItemId(dogs, otherDog.ItemDefId);
		await Settle();
		Require(wardrobe.SelectedDogItemDefId == otherDog.ItemDefId && SelectedDogId(dogs) == otherDog.ItemDefId,
			"Choosing a picture card selects the correct owned Steam dog by its stable item ID.");
		Require(pet.EditableDog.Texture.ResourcePath == otherDog.AssetPath, "Picture card selection updates the desktop to the matching owned dog.");
		await Settle();
		Require(DogCard(dogs, 0) == starterCard && DogCard(dogs, otherDog.ItemDefId) == otherCard,
			"Ordinary refreshes preserve card instances, pictures and focus targets.");

		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, firstDog.ItemDefId, otherDog.ItemDefId, otherDog.ItemDefId, 100 });
		await Settle();
		RequireDogChoices(dogs, new[] { 0, firstDog.ItemDefId, otherDog.ItemDefId }, "Gained dog ownership refreshes the list without duplicates, accessories or currency.");
		Require(wardrobe.SelectedDogItemDefId == otherDog.ItemDefId && SelectedDogId(dogs) == otherDog.ItemDefId,
			"Adding a dog retains the selected owned dog when its grid position changes.");
		Require(DogCard(dogs, 0) == starterCard && DogCard(dogs, otherDog.ItemDefId) == otherCard,
			"Adding ownership inserts a card without replacing the existing cards.");
		var firstCard = DogCard(dogs, firstDog.ItemDefId);

		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, otherDog.ItemDefId });
		firstCard.EmitSignal(BaseButton.SignalName.Pressed);
		Require(wardrobe.SelectedDogItemDefId == otherDog.ItemDefId, "A stale picture card cannot select a dog after ownership is removed.");
		await Settle();
		RequireDogChoices(dogs, new[] { 0, otherDog.ItemDefId }, "Losing an unselected dog removes it from the menu immediately.");
		Require(wardrobe.SelectedDogItemDefId == otherDog.ItemDefId && SelectedDogId(dogs) == otherDog.ItemDefId,
			"Removing another dog preserves the selected dog while it is still owned.");
		Require(DogCard(dogs, 0) == starterCard && DogCard(dogs, otherDog.ItemDefId) == otherCard,
			"Removing another dog keeps all remaining cards stable.");

		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, firstDog.ItemDefId });
		await Settle();
		RequireDogChoices(dogs, new[] { 0, firstDog.ItemDefId }, "A lost selected dog disappears while other owned dogs remain available.");
		Require(wardrobe.SelectedDogItemDefId == 0 && SelectedDogId(dogs) == 0 && pet.EditableDog.Texture.ResourcePath == "res://Sprites/Doggo.png",
			"Losing the selected dog's ownership falls back to Starter in both the menu and desktop.");
		SelectDogByItemId(dogs, firstDog.ItemDefId);
		await Settle();
		Require(wardrobe.SelectedDogItemDefId == firstDog.ItemDefId && SelectedDogId(dogs) == firstDog.ItemDefId,
			"A remaining owned dog can be selected using its item ID after the list changes.");
		SelectDogByItemId(dogs, 0);
		await Settle();
		Require(wardrobe.SelectedDogItemDefId == 0 && SelectedDogId(dogs) == 0, "Starter remains selectable while Steam dogs are owned.");

		wardrobe.SetSteamOwnership(Array.Empty<int>());
		await Settle();
		RequireDogChoices(dogs, new[] { 0 }, "Removing all ownership returns the menu to Starter only.");
		Require(SelectedDogId(dogs) == 0, "The empty refreshed list keeps Starter selected.");
	}
	private async Task VerifyDogGridLayout(StatusWindow menu, AccessoryWardrobe wardrobe, GridContainer dogs, DesktopPet pet)
	{
		var originalSize = menu.Size;
		try
		{
			wardrobe.SetSteamOwnership(SteamCosmeticCatalog.All.Where(item => item.Kind == "dog").Select(item => item.ItemDefId));
			menu.GetNode<Button>("%DogsTab").EmitSignal(BaseButton.SignalName.Pressed);
			menu.ShowStatusWindow();
			menu.Size = new Vector2I(1040, originalSize.Y);
			await Settle();
			var wideColumns = dogs.Columns;
			var firstCard = DogCard(dogs, 0);
			menu.Size = new Vector2I(menu.MinSize.X, originalSize.Y);
			await Settle();
			Require(dogs.Columns >= 1 && dogs.Columns < wideColumns,
				"The picture grid reduces columns when the actual browser viewport narrows.");
			var scroll = menu.GetNode<ScrollContainer>("%DogsScroll");
			var usableWidth = scroll.Size.X - (scroll.GetVScrollBar().Visible ? scroll.GetVScrollBar().GetCombinedMinimumSize().X : 0);
			var rowWidth = DogCards(dogs).Take(dogs.Columns).Sum(card => card.GetCombinedMinimumSize().X)
				+ Math.Max(0, dogs.Columns - 1) * dogs.GetThemeConstant("h_separation");
			Require(rowWidth <= usableWidth + 1 && DogCard(dogs, 0) == firstCard,
				"Visible card rows fit the available scroll viewport while retaining card identity.");
			Require(scroll.GetHScrollBar().MaxValue <= scroll.GetHScrollBar().Page + 1,
				"The adaptive dog collection requires no hidden horizontal scrolling.");
			RequireDogChoices(dogs, new[] { 0 }.Concat(SteamCosmeticCatalog.All.Where(item => item.Kind == "dog").Select(item => item.ItemDefId)).ToArray(),
				"All owned dog pictures appear once in the vertically scrolling collection.");
			Require(menu.GetNode<Label>("%DogSelectionStatus").Text.Contains("Starter dog", StringComparison.Ordinal),
				"The collection names the current selected dog separately from its cards.");
			foreach (var dog in SteamCosmeticCatalog.All.Where(item => item.Kind == "dog"))
			{
				SelectDogByItemId(dogs, dog.ItemDefId);
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				Require(wardrobe.SelectedDogItemDefId == dog.ItemDefId && SelectedDogId(dogs) == dog.ItemDefId
					&& pet.EditableDog.Texture.ResourcePath == dog.AssetPath,
					$"Every owned dog card selects its matching desktop texture: {dog.Name}.");
			}
			SelectDogByItemId(dogs, 0);
			await Settle();
			if (DisplayServer.GetName() != "headless")
			{
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				using var image = menu.GetTexture().GetImage();
				Require(image.SavePng("res://.godot/steam-dogs-smoke.png") == Error.Ok, "Owned dog picture cards capture for visual review.");
			}
		}
		finally { wardrobe.SetSteamOwnership(Array.Empty<int>()); menu.Size = originalSize; menu.Hide(); }
	}
	private void RequireDogChoices(GridContainer dogs, int[] expectedIds, string message)
	{
		var cards = DogCards(dogs);
		var actualIds = cards.Select(card => card.GetMeta("dog_itemdef_id").AsInt32()).ToArray();
		Require(actualIds.SequenceEqual(expectedIds) && actualIds.Distinct().Count() == actualIds.Length && cards.All(card => !card.Disabled), message);
		Require(cards.All(card => card.GetNode<TextureRect>("%DogChoicePreview").Texture != null
			&& card.GetNode<Label>("%DogChoiceName").Text == (SteamCosmeticCatalog.Find(card.GetMeta("dog_itemdef_id").AsInt32())?.Name ?? "Starter dog")),
			"Each owned dog card shows its picture and matching name.");
		Require(cards.Count(card => card.ButtonPressed) == 1 && cards.All(card =>
			card.GetNode<Label>("%DogChoiceStatus").Text == (card.ButtonPressed ? "Selected" : "Choose dog")),
			"Exactly one dog has the selected visual state and caption.");
	}
	private void SelectDogByItemId(GridContainer dogs, int itemDefId)
	{
		var card = DogCard(dogs, itemDefId);
		card.EmitSignal(BaseButton.SignalName.Pressed);
	}
	private static Button[] DogCards(GridContainer dogs) => dogs.GetChildren().OfType<Button>().ToArray();
	private static Button DogCard(GridContainer dogs, int itemDefId) => DogCards(dogs).Single(card => card.GetMeta("dog_itemdef_id").AsInt32() == itemDefId);
	private static int SelectedDogId(GridContainer dogs) => DogCards(dogs).Single(card => card.ButtonPressed).GetMeta("dog_itemdef_id").AsInt32();
	private void VerifyDogClickQueue(DesktopPet pet)
	{
		var settings = GetNode<PetSettings>("/root/PetSettings");
		settings.SetDogClickThrough(false);
		settings.SetDogTransparency(1);
		var backend = (BackendPetClient)typeof(DesktopPet).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;
		var sprite = pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		var hand = pet.GetNode<Sprite2D>("FootAnchor/PettingHand");
		void UpdateHand(float delta) => typeof(DesktopPet).GetMethod("UpdatePettingHand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet, new object[] { delta });
		Require(hand.Texture != null && !hand.Visible, "The Drive petting-hand artwork starts hidden as feedback, outside the wearable catalog.");
		using var image = sprite.Texture.GetImage();
		Vector2? opaque = null;
		Vector2? transparent = null;
		for (var y = 0; y < image.GetHeight() && (opaque == null || transparent == null); y++)
			for (var x = 0; x < image.GetWidth() && (opaque == null || transparent == null); x++)
			{
				var point = sprite.ToGlobal(new Vector2(x + 0.5f - image.GetWidth() * 0.5f, y + 0.5f - image.GetHeight() * 0.5f));
				if (image.GetPixel(x, y).A > 0.5f) opaque ??= point;
				else if (image.GetPixel(x, y).A == 0) transparent ??= point;
			}
		Require(opaque.HasValue && transparent.HasValue, "Dog texture provides opaque and transparent input test pixels.");
		using var clearClick = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = transparent!.Value };
		using var release = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = opaque!.Value };
		pet._Input(clearClick); pet._Input(release);
		Require(backend.PendingGrantCount == 0 && backend.ConfirmedPets == null, "Local texture hits and releases do not duplicate background grants.");
		Require(!hand.Visible, "Transparent pixels and button releases never show petting feedback.");
		var windowSizeField = typeof(DesktopPet).GetField("_windowSize", BindingFlags.Instance | BindingFlags.NonPublic)!;
		var originalWindowSize = (Vector2I)windowSizeField.GetValue(pet)!;
		try
		{
			windowSizeField.SetValue(pet, Vector2I.Zero);
			typeof(DesktopPet).GetMethod("ShowPettingHand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pet, new object[] { opaque.Value });
			Require(!hand.Visible && backend.PendingGrantCount == 0,
				"A zero-sized headless or transient viewport safely keeps petting feedback hidden without creating an input grant.");
		}
		finally { windowSizeField.SetValue(pet, originalWindowSize); }
		using var click = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = opaque.Value };
		pet._Input(click);
		Require(backend.PendingGrantCount == 0, "Local dog feedback never queues a duplicate input grant.");
		Require(hand.Visible, "An opaque left press shows the petting hand without granting currency locally.");
		var anchor = pet.GetNode<Node2D>("FootAnchor");
		var anchorPosition = anchor.Position;
		var viewportSize = pet.GetWindow().Size;
		anchor.Position += (Vector2)viewportSize;
		UpdateHand(0.01f);
		var handBounds = AccessoryGeometry.BoundsFromCorners(hand, hand.GetRect());
		Require(handBounds.Position.X >= 0 && handBounds.Position.Y >= 0
			&& handBounds.End.X <= viewportSize.X && handBounds.End.Y <= viewportSize.Y,
			"Moving the dog during a pat keeps the hand inside the overlay.");
		anchor.Position = anchorPosition;
		UpdateHand(0.4f);
		Require(!hand.Visible, "Petting-hand feedback expires after the brief pat.");
		pet.RecordInputActivity(1);
		Require(backend.PendingGrantCount == 1 && backend.ConfirmedPets == null, "One observed input queues exactly one grant without inventing a total.");
		pet._Input(click);
		pet.RecordInputActivity(1);
		Require(backend.PendingGrantCount == 2 && backend.ConfirmedPets == null, "A second input queues one additional grant while Steam is offline.");
		settings.SetDogClickThrough(true);
		UpdateHand(0.01f);
		pet._Input(click);
		pet.RecordInputActivity(1);
		Require(backend.PendingGrantCount == 3, "Background earning continues with dog click-through enabled.");
		Require(!hand.Visible, "Click-through mode hides petting feedback and ignores local dog clicks.");
		settings.SetDogClickThrough(false);
		settings.SetDogTransparency(0);
		UpdateHand(0.01f);
		pet._Input(click);
		pet.RecordInputActivity(2);
		Require(backend.PendingGrantCount == 5, "Background earning continues with an invisible dog and counts every input in a batch.");
		pet.RecordInputActivity(0);
		pet.RecordInputActivity(-1);
		Require(backend.PendingGrantCount == 5 && backend.ConfirmedPets == null, "Empty activity never creates grants or a local Pets total.");
		Require(!hand.Visible, "An invisible dog never shows petting-hand feedback.");
		settings.SetDogTransparency(1);
		pet._Input(click);
		GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession").SetActive(true);
		UpdateHand(0.01f);
		Require(!hand.Visible && backend.PendingGrantCount == 5, "Entering accessory editing hides the hand without duplicating input grants.");
		GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession").SetActive(false);
	}
	private async Task VerifyDesktopCounterAndMenuAccess(DesktopPet pet)
	{
		const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		var settings = GetNode<PetSettings>("/root/PetSettings");
		var backend = (BackendPetClient)typeof(DesktopPet).GetField("_backend", flags)!.GetValue(pet)!;
		var counter = pet.GetNode<PetCounterDisplay>("PetCounter");
		var bridge = GetNode<NativeWindowBridge>("/root/NativeWindowBridge");
		var originalCounter = settings.ShowPetCounter; var originalPosition = settings.PetCounterPosition;
		var originalRightClick = settings.OpenMenuOnRightClick; var originalClickThrough = settings.DogClickThrough;
		var originalTransparency = settings.DogTransparency; var originalTotal = backend.ConfirmedPets;
		var originalInventory = backend.InventoryItems; var processing = pet.IsProcessing();
		var pending = backend.PendingGrantCount;
		void UpdateCounter() => typeof(DesktopPet).GetMethod("UpdatePetCounter", flags)!.Invoke(pet, null);
		StatusWindow? PetMenu() => (StatusWindow?)typeof(DesktopPet).GetField("_statusWindow", flags)!.GetValue(pet);
		void Confirm(long value) => typeof(BackendPetClient).GetMethod("ApplyInventory", flags)!.Invoke(backend,
			new object[] { value, new[] { new SteamInventoryItem("fixture-confirmed-pets", 100, value) } });
		pet.SetProcess(false);
		try
		{
			PetMenu()?.Hide(); GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession").SetActive(false);
			settings.SetShowPetCounter(false); UpdateCounter();
			Require(!counter.Visible && counter.GetRenderBounds() == null, "The optional desktop Pets counter begins hidden and contributes no render bounds.");
			settings.SetShowPetCounter(true); UpdateCounter();
			Require(counter.CounterText == "Pets: —" && backend.ConfirmedPets == null,
				"An enabled counter with no confirmed Steam balance shows an unknown value instead of inventing zero or displaying queued input.");
			pet.RecordInputActivity(1); UpdateCounter();
			Require(counter.CounterText == "Pets: —" && backend.PendingGrantCount == pending + 1,
				"Another offline input remains internal and cannot increment the desktop counter before Steam confirms a balance.");
			Confirm(1234); UpdateCounter();
			Require(counter.CounterText == "Pets: 1,234", "The desktop counter displays the backend-confirmed Steam inventory total.");
			Confirm(1235); UpdateCounter();
			Require(counter.CounterText == "Pets: 1,235" && backend.PendingGrantCount == pending + 1,
				"A new confirmed snapshot updates the counter without adding or subtracting pending input.");
			Require(counter.MouseFilter == Control.MouseFilterEnum.Ignore && counter.GetNode<Label>("Value").MouseFilter == Control.MouseFilterEnum.Ignore,
				"The counter panel and its label are decorative and do not capture desktop input.");
			settings.SetDogClickThrough(false); settings.SetDogTransparency(1); settings.SetOpenMenuOnRightClick(true);
			foreach (var placement in Enum.GetValues<PetCounterPlacement>())
			{
				settings.SetPetCounterPosition(placement); UpdateCounter();
				var bounds = counter.GetRenderBounds()!.Value;
				Require(pet.GetViewportRect().Grow(1).Encloses(bounds), $"The {placement} desktop counter fits fully inside its actual overlay viewport.");
				var center = bounds.GetCenter();
				Require(!(bool)typeof(DesktopPet).GetMethod("IsVisibleDogPixel", flags)!.Invoke(pet, new object[] { center })!,
					$"The {placement} counter does not enlarge the dog's opaque-pixel click target.");
				pet._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = center });
				Require(PetMenu()?.Visible != true && backend.PendingGrantCount == pending + 1,
					"Right-clicking a decorative counter cannot open the dog menu or create a duplicate pet grant.");
				typeof(DesktopPet).GetMethod("UpdateDogMouseRegion", flags)!.Invoke(pet, null);
				var region = (Rect2I?)typeof(DesktopPet).GetField("_lastMouseRegion", flags)!.GetValue(pet);
				Require(region.HasValue && new Rect2(region.Value.Position, region.Value.Size).Grow(1).Encloses(bounds),
					"The native overlay render clip includes the visible counter while its dog hit geometry stays separate.");
				if (DisplayServer.GetName() != "headless" && placement is PetCounterPlacement.DogFeet or PetCounterPlacement.TopRight)
				{
					await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
					using var rendered = pet.GetViewport().GetTexture().GetImage();
					var start = new Vector2I(Mathf.FloorToInt(bounds.Position.X), Mathf.FloorToInt(bounds.Position.Y));
					var end = new Vector2I(Mathf.CeilToInt(bounds.End.X), Mathf.CeilToInt(bounds.End.Y));
					var crop = new Rect2I(start, end - start).Intersection(new Rect2I(Vector2I.Zero, rendered.GetSize()));
					Require(crop.HasArea(), "The visible desktop counter occupies a nonempty region of the rendered native viewport.");
					using var badge = rendered.GetRegion(crop);
					var backgroundPixels = 0; var glyphPixels = 0;
					for (var y = 0; y < badge.GetHeight(); y++) for (var x = 0; x < badge.GetWidth(); x++)
					{
						var color = badge.GetPixel(x, y);
						if (color.A <= 0.5f) continue;
						if (color.R > 0.75f && color.G > 0.7f && color.B > 0.5f) backgroundPixels++;
						if ((color.R + color.G + color.B) / 3 < 0.45f) glyphPixels++;
					}
					Require(backgroundPixels > 20 && glyphPixels > 5,
						"The native counter renders both its light badge background and contrasting Pets total glyphs.");
					var capturePath = placement == PetCounterPlacement.DogFeet ? "res://.godot/pet-counter-feet.png" : "res://.godot/pet-counter-corner.png";
					Require(badge.SavePng(capturePath) == Error.Ok, "The rendered counter crop saves for feet and corner visual review.");
				}
			}
			var sprite = pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
			using var image = sprite.Texture.GetImage(); var opaque = Vector2.Zero; var found = false;
			for (var y = 0; y < image.GetHeight() && !found; y++) for (var x = 0; x < image.GetWidth() && !found; x++)
				if (image.GetPixel(x, y).A > 0.5f)
				{
					opaque = sprite.ToGlobal(new Vector2(x + 0.5f, y + 0.5f) - sprite.Texture.GetSize() * 0.5f); found = true;
				}
			Require(found, "The right-click preference fixture uses an opaque dog pixel.");
			settings.SetOpenMenuOnRightClick(false);
			pet._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = opaque });
			Require(PetMenu()?.Visible != true, "Disabling dog right-click menus ignores an otherwise valid opaque dog press.");
			settings.SetOpenMenuOnRightClick(true);
			pet._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = opaque });
			Require(PetMenu()?.Visible == true, "Re-enabling dog right-click menus restores normal menu access.");
			PetMenu()!.Hide();
			settings.SetOpenMenuOnRightClick(false); settings.SetDogClickThrough(true); settings.SetDogTransparency(0);
			typeof(NativeWindowBridge).GetField("_statusRequested", flags)!.SetValue(bridge, true); bridge._Process(0);
			Require(PetMenu()?.Visible == true && backend.PendingGrantCount == pending + 1 && counter.CounterText == "Pets: 1,235",
				"The native global menu request opens Settings even with dog right-click disabled, click-through enabled and an invisible dog, without granting Pets.");
		}
		finally
		{
			PetMenu()?.Hide();
			typeof(BackendPetClient).GetProperty(nameof(BackendPetClient.ConfirmedPets))!.SetValue(backend, originalTotal);
			typeof(BackendPetClient).GetProperty(nameof(BackendPetClient.InventoryItems))!.SetValue(backend, originalInventory);
			settings.SetOpenMenuOnRightClick(originalRightClick); settings.SetDogClickThrough(originalClickThrough);
			settings.SetDogTransparency(originalTransparency); settings.SetPetCounterPosition(originalPosition); settings.SetShowPetCounter(originalCounter);
			UpdateCounter(); pet.SetProcess(processing);
		}
	}

	private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _assertions++; }
}
