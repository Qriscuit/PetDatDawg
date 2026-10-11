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
			Require(SteamCosmeticCatalog.All.Count == 88 && SteamCosmeticCatalog.All.Select(item => item.ItemDefId).Distinct().Count() == 88,
				"All 88 Steam cosmetics have unique definition IDs.");
			Require(SteamCosmeticCatalog.All.Count(item => item.Kind == "dog") == 45
				&& SteamCosmeticCatalog.All.Count(item => item.Kind == "accessory") == 43, "Both complete reward pools are mapped.");
			Require(SteamCosmeticCatalog.All.All(item => ResourceLoader.Exists(item.AssetPath) && ResourceLoader.Exists(item.UiAssetPath)),
				"Every mapped source and preview resource exists.");
			Require(wardrobe.Catalog.Count(item => item.SteamItemDefId > 0) == 43, "Items includes every Steam accessory.");
			var accessory = SteamCosmeticCatalog.All.First(item => item.Kind == "accessory");
			var dog = SteamCosmeticCatalog.All.First(item => item.Kind == "dog");
			wardrobe.Equip(accessory.AccessoryId, Vector2.One * 0.5f);
			wardrobe.SelectDog(dog.ItemDefId);
			Require(wardrobe.GetPlacement(accessory.AccessoryId) == null && wardrobe.SelectedDogItemDefId == 0,
				"Unconfirmed items cannot be equipped or selected.");
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
			var dogs = menu.GetNode<OptionButton>("%DogChoices");
			RequireDogChoices(dogs, new[] { 0 }, "Empty Steam ownership shows only Starter dog.");
			Require(dogs.Selected == 0 && dogs.GetItemId(dogs.Selected) == 0, "Starter dog is selected with empty ownership.");
			await VerifyOwnedDogSelector(wardrobe, dogs, pet, accessory, dog);
			if (DisplayServer.GetName() != "headless")
			{
				menu.GetNode<Button>("%ShopTab").EmitSignal(BaseButton.SignalName.Pressed);
				menu.ShowStatusWindow();
				await Settle(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				using var image = menu.GetTexture().GetImage();
				Require(image.SavePng("res://.godot/steam-shop-smoke.png") == Error.Ok, "Shop layout captures for visual review.");
				menu.Hide();
			}
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
	private async Task VerifyOwnedDogSelector(AccessoryWardrobe wardrobe, OptionButton dogs, DesktopPet pet,
		SteamCosmeticDefinition accessory, SteamCosmeticDefinition firstDog)
	{
		var otherDog = SteamCosmeticCatalog.All.Last(item => item.Kind == "dog");
		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId });
		await Settle();
		RequireDogChoices(dogs, new[] { 0 }, "Owned accessories do not appear in the dog selector.");

		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, otherDog.ItemDefId });
		await Settle();
		RequireDogChoices(dogs, new[] { 0, otherDog.ItemDefId }, "Gaining one dog adds only that owned dog beside Starter.");
		SelectDogByItemId(dogs, otherDog.ItemDefId);
		await Settle();
		Require(wardrobe.SelectedDogItemDefId == otherDog.ItemDefId && SelectedDogId(dogs) == otherDog.ItemDefId,
			"Selecting a nonconsecutive dropdown item ID selects the correct owned Steam dog.");
		Require(pet.EditableDog.Texture.ResourcePath == otherDog.AssetPath, "Dropdown selection updates the desktop to the matching owned dog.");

		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, firstDog.ItemDefId, otherDog.ItemDefId, otherDog.ItemDefId, 100 });
		await Settle();
		RequireDogChoices(dogs, new[] { 0, firstDog.ItemDefId, otherDog.ItemDefId }, "Gained dog ownership refreshes the list without duplicates, accessories or currency.");
		Require(wardrobe.SelectedDogItemDefId == otherDog.ItemDefId && SelectedDogId(dogs) == otherDog.ItemDefId,
			"Adding a dog retains the selected owned dog when its dropdown index changes.");

		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId, otherDog.ItemDefId });
		await Settle();
		RequireDogChoices(dogs, new[] { 0, otherDog.ItemDefId }, "Losing an unselected dog removes it from the menu immediately.");
		Require(wardrobe.SelectedDogItemDefId == otherDog.ItemDefId && SelectedDogId(dogs) == otherDog.ItemDefId,
			"Removing another dog preserves the selected dog while it is still owned.");

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
	private void RequireDogChoices(OptionButton dogs, int[] expectedIds, string message)
	{
		var actualIds = Enumerable.Range(0, dogs.ItemCount).Select(dogs.GetItemId).ToArray();
		Require(actualIds.Order().SequenceEqual(expectedIds.Order()) && actualIds.Distinct().Count() == actualIds.Length
			&& Enumerable.Range(0, dogs.ItemCount).All(index => !dogs.IsItemDisabled(index)), message);
	}
	private void SelectDogByItemId(OptionButton dogs, int itemDefId)
	{
		var index = dogs.GetItemIndex(itemDefId);
		Require(index >= 0, "The requested owned dog must have an entry with its Steam item ID.");
		dogs.Select(index);
		dogs.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
	}
	private static int SelectedDogId(OptionButton dogs) => dogs.Selected >= 0 ? dogs.GetItemId(dogs.Selected) : -1;
	private void VerifyDogClickQueue(DesktopPet pet)
	{
		var settings = GetNode<PetSettings>("/root/PetSettings");
		settings.SetDogClickThrough(false);
		settings.SetDogTransparency(1);
		var backend = (BackendPetClient)typeof(DesktopPet).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;
		var sprite = pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
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
		Require(backend.PendingGrantCount == 0 && backend.ConfirmedPets == null, "Transparent texture pixels and button release do not grant Pets.");
		using var click = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = opaque.Value };
		pet._Input(click);
		Require(backend.PendingGrantCount == 1 && backend.ConfirmedPets == null, "One visible dog click queues exactly one grant without inventing a total.");
		pet._Input(click);
		Require(backend.PendingGrantCount == 2 && backend.ConfirmedPets == null, "A second visible click queues one additional grant while Steam is offline.");
		settings.SetDogClickThrough(true);
		pet._Input(click);
		Require(backend.PendingGrantCount == 2, "Dog click-through mode does not enqueue grants.");
		settings.SetDogClickThrough(false);
		settings.SetDogTransparency(0);
		pet._Input(click);
		Require(backend.PendingGrantCount == 2, "An invisible dog does not enqueue grants.");
		settings.SetDogTransparency(1);
	}
	private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _assertions++; }
}
