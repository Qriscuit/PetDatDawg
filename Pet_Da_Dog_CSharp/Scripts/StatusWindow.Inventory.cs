using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class StatusWindow
{
	private BackendPetClient? _inventoryBackend;
	private AccessoryWardrobe? _inventoryWardrobe;
	private CancellationToken _inventoryCancellation;
	private Button? _buyDogBox, _buyAccessoryBox, _openBox, _refreshInventory;
	private OptionButton? _boxChoices, _dogChoices;
	private Label? _boxSummary, _shopStatus;
	private TextureRect? _dogPortrait;
	private IReadOnlyList<SteamInventoryItem>? _displayedInventory;
	private string _displayedDogPath = string.Empty;
	private IReadOnlyList<SteamInventoryItem> _displayedBoxes = Array.Empty<SteamInventoryItem>();

	private void BindInventoryControls()
	{
		_buyDogBox = GetNodeOrNull<Button>("%BuyDogBox");
		_buyAccessoryBox = GetNodeOrNull<Button>("%BuyAccessoryBox");
		_openBox = GetNodeOrNull<Button>("%OpenBox");
		_refreshInventory = GetNodeOrNull<Button>("%RefreshInventory");
		_boxChoices = GetNodeOrNull<OptionButton>("%BoxChoices");
		_dogChoices = GetNodeOrNull<OptionButton>("%DogChoices");
		_boxSummary = GetNodeOrNull<Label>("%BoxSummary");
		_shopStatus = GetNodeOrNull<Label>("%ShopStatus");
		_dogPortrait = GetNodeOrNull<TextureRect>("%DogPortrait");
		if (_buyDogBox != null) _buyDogBox.Pressed += () => _ = RunInventoryAction("dog");
		if (_buyAccessoryBox != null) _buyAccessoryBox.Pressed += () => _ = RunInventoryAction("accessory");
		if (_openBox != null) _openBox.Pressed += () => _ = RunInventoryAction("open");
		if (_refreshInventory != null) _refreshInventory.Pressed += () => _ = RunInventoryAction("refresh");
		if (_boxChoices != null) _boxChoices.ItemSelected += _ => UpdateInventoryControls();
		if (_dogChoices != null)
		{
			_dogChoices.AddItem("Starter dog", 0);
			_dogChoices.ItemSelected += index =>
			{
				_inventoryWardrobe?.SelectDog(_dogChoices.GetItemId((int)index));
				UpdateInventoryControls();
			};
		}
		UpdateInventoryControls();
	}

	public void ConfigureSteamInventory(BackendPetClient backend, AccessoryWardrobe? wardrobe, CancellationToken cancellation)
	{
		_inventoryBackend = backend;
		_inventoryWardrobe = wardrobe;
		ConfigurePresetWardrobe(wardrobe);
		_inventoryCancellation = cancellation;
		_displayedInventory = null;
		UpdateInventoryControls();
	}

	private async Task RunInventoryAction(string action)
	{
		var backend = _inventoryBackend;
		if (backend == null || backend.IsOperationInFlight || !backend.IsAuthenticated) return;
		if (action == "refresh") await backend.RefreshInventoryAsync(_inventoryCancellation);
		else if (backend.HasPendingExchange) return;
		else if (action == "open")
		{
			var index = _boxChoices?.Selected ?? -1;
			if (index < 0 || index >= _displayedBoxes.Count) return;
			await backend.OpenBoxAsync(_displayedBoxes[index].ItemId, _inventoryCancellation);
		}
		else await backend.BuyBoxAsync(action, _inventoryCancellation);
		if (GodotObject.IsInstanceValid(this) && IsInsideTree()) UpdateInventoryControls();
	}

	private void UpdateInventoryControls()
	{
		if (_buyDogBox == null) return;
		var backend = _inventoryBackend;
		var ready = backend?.IsAuthenticated == true && !backend.IsOperationInFlight && !backend.HasPendingExchange;
		_buyDogBox.Disabled = !ready || backend?.ConfirmedPets is not >= 1;
		_buyAccessoryBox!.Disabled = !ready || backend?.ConfirmedPets is not >= 2;
		_refreshInventory!.Disabled = backend?.IsAuthenticated != true || backend.IsOperationInFlight;
		if (backend != null && !ReferenceEquals(_displayedInventory, backend.InventoryItems))
		{
			_displayedInventory = backend.InventoryItems;
			var selectedId = _boxChoices!.Selected >= 0 && _boxChoices.Selected < _displayedBoxes.Count
				? _displayedBoxes[_boxChoices.Selected].ItemId : null;
			_displayedBoxes = backend.InventoryItems.Where(item => item.Quantity > 0 && item.ItemDefId is 1000 or 1001).ToArray();
			_boxChoices.Clear();
			foreach (var box in _displayedBoxes)
				_boxChoices.AddItem($"{(box.ItemDefId == 1001 ? "Dog Box" : "Accessories Box")} · {box.Quantity}");
			var selected = _displayedBoxes.ToList().FindIndex(item => item.ItemId == selectedId);
			if (_displayedBoxes.Count > 0) _boxChoices.Select(Math.Max(0, selected));
			_boxSummary!.Text = $"Your boxes: {_displayedBoxes.Where(item => item.ItemDefId == 1001).Sum(item => item.Quantity)} dog · "
				+ $"{_displayedBoxes.Where(item => item.ItemDefId == 1000).Sum(item => item.Quantity)} accessories";
		}
		_boxChoices!.Disabled = !ready || _displayedBoxes.Count == 0;
		_openBox!.Disabled = !ready || _displayedBoxes.Count == 0 || _boxChoices.Selected < 0;
		if (_shopStatus != null)
		{
			_shopStatus.Text = backend == null || !backend.IsAuthenticated ? "Connect to Steam to earn Pets and use boxes."
				: backend.HasPendingExchange ? "Your box transaction is awaiting Steam confirmation. Box actions and pet syncing are paused. Contact support if this persists."
				: backend.ReceivedItems.Count > 0 ? "Received: " + string.Join(", ", backend.ReceivedItems.Select(item =>
					SteamCosmeticCatalog.Find(item.ItemDefId)?.Name ?? (item.ItemDefId == 1001 ? "Dog Box" : item.ItemDefId == 1000 ? "Accessories Box" : "Pets")))
				: backend.Status;
		}
		if (_dogChoices != null)
		{
			// Keep the open menu stable between inventory updates, and compare by Steam definition ID.
			var ownedDogCount = 0;
			var dogsChanged = false;
			foreach (var dog in SteamCosmeticCatalog.All)
			{
				if (dog.Kind != "dog" || _inventoryWardrobe?.OwnsSteamItem(dog.ItemDefId) != true) continue;
				ownedDogCount++;
				if (ownedDogCount >= _dogChoices.ItemCount || _dogChoices.GetItemId(ownedDogCount) != dog.ItemDefId)
					dogsChanged = true;
			}
			if (dogsChanged || _dogChoices.ItemCount != ownedDogCount + 1)
			{
				_dogChoices.Clear();
				_dogChoices.AddItem("Starter dog", 0);
				foreach (var dog in SteamCosmeticCatalog.All)
					if (dog.Kind == "dog" && _inventoryWardrobe?.OwnsSteamItem(dog.ItemDefId) == true)
						_dogChoices.AddItem(dog.Name, dog.ItemDefId);
			}
			_dogChoices.Select(Math.Max(0, _dogChoices.GetItemIndex(_inventoryWardrobe?.SelectedDogItemDefId ?? 0)));
			var path = _inventoryWardrobe?.CurrentDogTexturePath ?? "res://Sprites/Doggo.png";
			if (_dogPortrait != null && _displayedDogPath != path)
			{
				_displayedDogPath = path;
				_dogPortrait.Texture = SteamCosmeticCatalog.CroppedTexture(path);
			}
		}
	}
}
