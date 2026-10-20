using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Steamworks;

public sealed record SteamCurrencyPrice(int ItemDefId, string Currency, ulong CurrentPrice, ulong BasePrice)
{
	// Steam reports hundredths even for currencies such as JPY and KWD.
	public string FormattedPrice => $"{(CurrentPrice / 100m).ToString("N2", CultureInfo.CurrentCulture)} {Currency}";
}

public sealed partial class SteamIntegration
{
	private const int CommerceTimeoutSeconds = 30;
	private bool _cashPurchasesEnabled;
	private bool _marketEnabled;
	private bool _checkoutOpen;
	private HashSet<int> _allowedPackIds = new();
	private readonly HashSet<SteamInventoryResult_t> _explicitInventoryResults = new();
	private Callback<SteamInventoryResultReady_t>? _inventoryReadyCallback;
	private Callback<SteamInventoryFullUpdate_t>? _inventoryUpdateCallback;
	private Callback<GameOverlayActivated_t>? _overlayCallback;
	private CallResult<SteamInventoryRequestPricesResult_t>? _pricesCallResult;
	private CallResult<SteamInventoryStartPurchaseResult_t>? _purchaseCallResult;
	private TaskCompletionSource<SteamInventoryRequestPricesResult_t>? _pricesCompletion;
	private TaskCompletionSource<SteamInventoryStartPurchaseResult_t>? _purchaseCompletion;
	public event Action? InventoryChanged;
	public IReadOnlyDictionary<int, SteamCurrencyPrice> CurrencyPrices { get; private set; } = new ReadOnlyDictionary<int, SteamCurrencyPrice>(new Dictionary<int, SteamCurrencyPrice>());
	public bool IsCheckoutInFlight => _checkoutOpen || _purchaseCompletion is { Task.IsCompleted: false };
	public bool IsPriceRequestInFlight => _pricesCompletion is { Task.IsCompleted: false };
	public string CommerceStatus { get; private set; } = "Steam purchases are not available yet.";

	public void ConfigureCommerce(bool cashPurchasesEnabled, bool marketEnabled, IEnumerable<int> allowedPackIds)
	{
		_cashPurchasesEnabled = cashPurchasesEnabled;
		_marketEnabled = marketEnabled;
		_allowedPackIds = allowedPackIds.Where(id => id > 0).ToHashSet();
		if (!cashPurchasesEnabled && !IsCheckoutInFlight) CommerceStatus = "Golden Bones purchases are not available yet.";
	}

	public async Task<bool> RequestPricesAsync(IEnumerable<int> itemDefIds, CancellationToken cancellationToken)
	{
		if (!_initialized || IsPriceRequestInFlight || cancellationToken.IsCancellationRequested) return false;
		var ids = itemDefIds.Where(id => id > 0 && _allowedPackIds.Contains(id)).Distinct().ToArray();
		if (ids.Length == 0) return false;
		var completion = new TaskCompletionSource<SteamInventoryRequestPricesResult_t>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pricesCompletion = completion;
		try
		{
			_pricesCallResult?.Dispose();
			_pricesCallResult = CallResult<SteamInventoryRequestPricesResult_t>.Create((result, failed) =>
			{
				if (failed || result.m_result != EResult.k_EResultOK) completion.TrySetException(new InvalidOperationException("Steam could not load local prices."));
				else completion.TrySetResult(result);
			});
			_pricesCallResult.Set(SteamInventory.RequestPrices());
			var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(CommerceTimeoutSeconds), cancellationToken);
			var currency = result.m_rgchCurrency?.Trim().ToUpperInvariant();
			if (currency?.Length != 3 || currency.Any(character => character < 'A' || character > 'Z')) throw new InvalidOperationException("Steam returned an invalid currency code.");
			var prices = new Dictionary<int, SteamCurrencyPrice>();
			foreach (var id in ids)
				if (SteamInventory.GetItemPrice(new SteamItemDef_t(id), out var current, out var original) && current > 0)
					prices[id] = new(id, currency, current, original);
			CurrencyPrices = new ReadOnlyDictionary<int, SteamCurrencyPrice>(prices);
			CommerceStatus = prices.Count > 0 ? "Prices provided by Steam." : "These packs are not currently for sale through Steam.";
			return prices.Count > 0;
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception exception) { CommerceStatus = exception.Message; CurrencyPrices = new ReadOnlyDictionary<int, SteamCurrencyPrice>(new Dictionary<int, SteamCurrencyPrice>()); return false; }
		finally
		{
			_pricesCallResult?.Dispose(); _pricesCallResult = null;
			if (ReferenceEquals(_pricesCompletion, completion)) _pricesCompletion = null;
		}
	}

	// True means Steam opened checkout, never that the player paid or received currency.
	public async Task<bool> StartCurrencyPurchaseAsync(int itemDefId, CancellationToken cancellationToken)
	{
		if (!_initialized || !_cashPurchasesEnabled || !_allowedPackIds.Contains(itemDefId) || !CurrencyPrices.ContainsKey(itemDefId) || IsCheckoutInFlight || cancellationToken.IsCancellationRequested)
		{
			CommerceStatus = "This pack is not available for purchase right now.";
			return false;
		}
		if (!SteamUtils.IsOverlayEnabled())
		{
			CommerceStatus = "Enable the Steam Overlay and launch the game through Steam to purchase Golden Bones.";
			return false;
		}
		var completion = new TaskCompletionSource<SteamInventoryStartPurchaseResult_t>(TaskCreationOptions.RunContinuationsAsynchronously);
		_purchaseCompletion = completion;
		_checkoutOpen = true;
		try
		{
			_purchaseCallResult?.Dispose();
			_purchaseCallResult = CallResult<SteamInventoryStartPurchaseResult_t>.Create((result, failed) =>
			{
				if (failed || result.m_result != EResult.k_EResultOK || result.m_ulOrderID == 0 || result.m_ulTransID == 0)
					completion.TrySetException(new InvalidOperationException("Steam could not open checkout."));
				else completion.TrySetResult(result);
			});
			_purchaseCallResult.Set(SteamInventory.StartPurchase(new[] { new SteamItemDef_t(itemDefId) }, new uint[] { 1 }, 1));
			await completion.Task.WaitAsync(TimeSpan.FromSeconds(CommerceTimeoutSeconds), cancellationToken);
			CommerceStatus = "Complete your purchase in Steam. Golden Bones appear after Steam confirms your inventory.";
			return true;
		}
		catch (OperationCanceledException) { CommerceStatus = "Check Steam for your checkout status, then return to refresh inventory."; return false; }
		catch (Exception exception) { CommerceStatus = exception.Message; _checkoutOpen = false; InventoryChanged?.Invoke(); return false; }
		finally
		{
			_purchaseCallResult?.Dispose(); _purchaseCallResult = null;
			if (ReferenceEquals(_purchaseCompletion, completion)) _purchaseCompletion = null;
		}
	}

	public bool OpenMarket() => OpenCommercePage($"https://steamcommunity.com/market/search?appid={ResolveAppId()}", true);
	public bool OpenSteamInventory() => OpenCommercePage($"https://steamcommunity.com/profiles/{SteamId}/inventory/#{ResolveAppId()}_2", false);
	private bool OpenCommercePage(string url, bool requiresMarket)
	{
		if (!_initialized || (requiresMarket && !_marketEnabled)) { CommerceStatus = "Steam Market is not available for this game yet."; return false; }
		try
		{
			if (SteamUtils.IsOverlayEnabled()) SteamFriends.ActivateGameOverlayToWebPage(url, EActivateGameOverlayToWebPageMode.k_EActivateGameOverlayToWebPageMode_Default);
			else if (OS.ShellOpen(url) != Error.Ok) throw new InvalidOperationException("Could not open the Steam page.");
			CommerceStatus = "Steam handles listings and confirmations. Sales pay Steam Wallet funds.";
			return true;
		}
		catch (Exception exception) { CommerceStatus = exception.Message; return false; }
	}

	public void NotifyApplicationFocusReturned()
	{
		if (!_initialized) return;
		_checkoutOpen = false;
		InventoryChanged?.Invoke();
	}

	// Explicit native inventory readers retain their result until ReleaseInventorySnapshot.
	// All such requests go through this wrapper so unsolicited purchase results can
	// be released by our callback without invalidating another reader's handle.
	public bool TryRequestInventorySnapshot(out SteamInventoryResult_t handle)
	{
		handle = default;
		if (!_initialized || !SteamInventory.GetAllItems(out handle)) return false;
		_explicitInventoryResults.Add(handle);
		return true;
	}

	public void ReleaseInventorySnapshot(SteamInventoryResult_t handle)
	{
		if (_explicitInventoryResults.Remove(handle) && _initialized) SteamInventory.DestroyResult(handle);
	}

	private void InitializeCommerceCallbacks()
	{
		_inventoryReadyCallback = Callback<SteamInventoryResultReady_t>.Create(result =>
		{
			var explicitReaderOwnsResult = _explicitInventoryResults.Contains(result.m_handle);
			try { if (result.m_result == EResult.k_EResultOK) InventoryChanged?.Invoke(); }
			finally
			{
				// StartPurchase creates an unsolicited result. Gameplay reads the Worker
				// snapshot instead of this handle, but Steam still requires its release.
				if (!explicitReaderOwnsResult) SteamInventory.DestroyResult(result.m_handle);
			}
		});
		_inventoryUpdateCallback = Callback<SteamInventoryFullUpdate_t>.Create(_ => InventoryChanged?.Invoke());
		_overlayCallback = Callback<GameOverlayActivated_t>.Create(result => { if (result.m_bActive == 0) NotifyApplicationFocusReturned(); });
	}

	private void DisposeCommerce()
	{
		_pricesCompletion?.TrySetCanceled(); _purchaseCompletion?.TrySetCanceled();
		_pricesCallResult?.Dispose(); _purchaseCallResult?.Dispose();
		_inventoryReadyCallback?.Dispose(); _inventoryUpdateCallback?.Dispose(); _overlayCallback?.Dispose();
		_pricesCallResult = null; _purchaseCallResult = null; _inventoryReadyCallback = null; _inventoryUpdateCallback = null; _overlayCallback = null;
		_checkoutOpen = false; _cashPurchasesEnabled = false; _marketEnabled = false; _allowedPackIds.Clear();
		if (_initialized) foreach (var handle in _explicitInventoryResults) SteamInventory.DestroyResult(handle);
		_explicitInventoryResults.Clear();
		InventoryChanged = null;
	}
}
