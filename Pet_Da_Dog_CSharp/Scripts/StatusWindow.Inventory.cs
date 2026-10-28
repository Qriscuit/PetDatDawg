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
	private Button? _openBox, _refreshInventory;
	private OptionButton? _boxChoices;
	private Label? _boxSummary, _shopStatus, _dogSelectionStatus;
	private GridContainer? _dogGrid;
	private ScrollContainer? _dogChoiceScroll;
	private PackedScene? _dogChoiceCardScene;
	private readonly Dictionary<int, Button> _dogCards = new();
	private IReadOnlyList<SteamInventoryItem>? _displayedInventory;
	private IReadOnlyList<SteamInventoryItem> _displayedBoxes = Array.Empty<SteamInventoryItem>();

	private void BindInventoryControls()
	{
		_openBox = GetNodeOrNull<Button>("%OpenBox");
		_refreshInventory = GetNodeOrNull<Button>("%RefreshInventory");
		_boxChoices = GetNodeOrNull<OptionButton>("%BoxChoices");
		_dogGrid = GetNodeOrNull<GridContainer>("%DogGrid");
		_dogChoiceScroll = GetNodeOrNull<ScrollContainer>("%DogsScroll");
		_dogSelectionStatus = GetNodeOrNull<Label>("%DogSelectionStatus");
		if (_dogGrid != null)
			foreach (var preview in _dogGrid.GetChildren()) { _dogGrid.RemoveChild(preview); preview.QueueFree(); }
		_boxSummary = GetNodeOrNull<Label>("%BoxSummary");
		_shopStatus = GetNodeOrNull<Label>("%ShopStatus");
		if (_openBox != null) _openBox.Pressed += () => _ = RunInventoryAction("open");
		if (_refreshInventory != null) _refreshInventory.Pressed += () => _ = RunInventoryAction("refresh");
		if (_boxChoices != null) _boxChoices.ItemSelected += _ => UpdateInventoryControls();
		BindShopControls();
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
		if (backend == null || IsShopBusy || !backend.IsAuthenticated) return;
		if (action == "refresh") await backend.RefreshShopAsync(_inventoryCancellation);
		else if (backend.HasPendingExchange) return;
		else if (action == "open")
		{
			var index = _boxChoices?.Selected ?? -1;
			if (index < 0 || index >= _displayedBoxes.Count) return;
			_shopActionActive = true;
			BoxRevealRequested?.Invoke(_displayedBoxes[index].ItemDefId == 1001 ? "dog" : "accessory");
			try
			{
				if (await backend.OpenBoxAsync(_displayedBoxes[index].ItemId, _inventoryCancellation))
				{
					await backend.RefreshShopAsync(_inventoryCancellation);
					PresentNextShopReceipt(true);
				}
				else BoxRevealFailed?.Invoke(backend.Status);
			}
			finally { _shopActionActive = false; }
		}
		if (GodotObject.IsInstanceValid(this) && IsInsideTree()) UpdateInventoryControls();
	}

	private void UpdateInventoryControls()
	{
		if (_openBox == null) return;
		var backend = _inventoryBackend;
		var ready = backend?.IsAuthenticated == true && !IsShopBusy && !backend.HasPendingExchange;
		_refreshInventory!.Disabled = backend?.IsAuthenticated != true || IsShopBusy;
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
			_shopStatus.Text = backend == null || !backend.IsAuthenticated ? "Connect to Steam to use the Shop."
				: backend.HasPendingExchange || backend.HasPendingShopOperation ? "Steam is confirming your transaction. Your result will appear when it is confirmed."
				: backend.ShopStatus;
		}
		UpdateShopControls();
		RefreshDogGrid();
	}

	private void RefreshDogGrid()
	{
		if (_dogGrid == null) return;
		var ownedDogs = SteamCosmeticCatalog.All.Where(dog => dog.Kind == "dog" && _inventoryWardrobe?.OwnsSteamItem(dog.ItemDefId) == true).ToArray();
		var visibleIds = new HashSet<int>(ownedDogs.Select(dog => dog.ItemDefId)) { 0 };
		foreach (var itemDefId in _dogCards.Keys.Where(id => !visibleIds.Contains(id)).ToArray())
		{
			var removed = _dogCards[itemDefId];
			_dogCards.Remove(itemDefId);
			_dogGrid.RemoveChild(removed);
			removed.QueueFree();
		}
		EnsureDogCard(0, "Corgi 3", "res://Art/Dogs/Corgi3.png", 0);
		for (var index = 0; index < ownedDogs.Length; index++)
		{
			var dog = ownedDogs[index];
			EnsureDogCard(dog.ItemDefId, dog.Name, dog.UiAssetPath, index + 1);
		}
		var selectedId = _inventoryWardrobe?.SelectedDogItemDefId ?? 0;
		foreach (var (itemDefId, card) in _dogCards)
		{
			var selected = itemDefId == selectedId;
			card.SetPressedNoSignal(selected);
			card.GetNode<Label>("%DogChoiceStatus").Text = selected ? "Selected" : "Choose dog";
		}
		if (_dogSelectionStatus != null)
			_dogSelectionStatus.Text = "Selected: " + (SteamCosmeticCatalog.Find(selectedId)?.Name ?? "Starter dog");
		UpdateDogGridColumns();
	}

	private void EnsureDogCard(int itemDefId, string dogName, string previewPath, int index)
	{
		if (_dogGrid == null) return;
		if (!_dogCards.TryGetValue(itemDefId, out var card))
		{
			_dogChoiceCardScene ??= ResourceLoader.Load<PackedScene>("res://UI/DogChoiceCard.tscn");
			card = _dogChoiceCardScene.Instantiate<Button>();
			card.Name = $"Dog_{itemDefId}";
			card.SetMeta("dog_itemdef_id", itemDefId);
			card.TooltipText = dogName;
			card.GetNode<TextureRect>("%DogChoicePreview").Texture = SteamCosmeticCatalog.CroppedTexture(previewPath);
			card.GetNode<Label>("%DogChoiceName").Text = dogName;
			card.Pressed += () =>
			{
				// Recheck confirmed ownership even if an old card callback was queued.
				_inventoryWardrobe?.SelectDog(itemDefId);
				RefreshDogGrid();
			};
			_dogCards.Add(itemDefId, card);
			_dogGrid.AddChild(card);
		}
		if (card.GetIndex() != index) _dogGrid.MoveChild(card, index);
	}

	private void UpdateDogGridColumns()
	{
		if (_dogGrid == null || _dogCards.Count == 0) return;
		var scroll = _dogChoiceScroll;
		var scrollbarWidth = scroll?.GetVScrollBar().Visible == true ? scroll.GetVScrollBar().GetCombinedMinimumSize().X : 0;
		// Use the containing viewport so a previous multi-column minimum can shrink.
		var availableWidth = Math.Max(0, (scroll?.Size.X ?? _dogGrid.Size.X) - scrollbarWidth);
		var cardWidth = _dogCards.Values.Max(card => card.GetCombinedMinimumSize().X);
		var separation = _dogGrid.GetThemeConstant("h_separation");
		var columns = Math.Clamp((int)Math.Floor((availableWidth + separation) / Math.Max(1, cardWidth + separation)), 1, _dogCards.Count);
		if (_dogGrid.Columns != columns) _dogGrid.Columns = columns;
	}
}
