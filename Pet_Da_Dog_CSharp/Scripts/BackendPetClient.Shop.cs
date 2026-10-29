using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed partial class BackendPetClient
{
	private const string CheckingPetsShopStatus = "Checking Pets with Steam…";
	private bool _shopRequestInFlight;
	private bool _shopForegroundReserved;
	private bool _shopAckInFlight;
	private double _shopAckRetryDelay;
	private readonly HashSet<Guid> _pendingReceiptAckIds = new();
	private PendingShopRequest? _pendingShopRequest;
	private Guid? _pendingShopOperationId;
	private DateTimeOffset _serverTime;
	private readonly Stopwatch _serverElapsed = new();
	public long? ConfirmedGoldenBones { get; private set; }
	public string? EconomyVersion { get; private set; }
	public ShopCatalog ShopCatalog { get; private set; } = new();
	public ShopCapabilities ShopCapabilities { get; private set; } = new();
	public DateTimeOffset ServerTime => _serverTime + _serverElapsed.Elapsed;
	public DateTimeOffset? NextDigAt { get; private set; }
	public TimeSpan DigCooldownRemaining => NextDigAt is DateTimeOffset next && next > ServerTime ? next - ServerTime : TimeSpan.Zero;
	// Background click grants and inventory synchronization must never flash purchase buttons.
	public bool IsShopOperationInFlight => _shopRequestInFlight || _exchangeInFlight || _shopForegroundReserved;
	public bool HasPendingShopOperation => _pendingShopOperationId.HasValue;
	public IReadOnlyList<ShopReceipt> UnacknowledgedReceipts { get; private set; } = Array.Empty<ShopReceipt>();
	public string ShopStatus { get; private set; } = "Connect to Steam to browse the Shop.";

	public Task<bool> PurchaseOfferAsync(string offerId, CancellationToken cancellationToken)
	{
		var offer = ShopCatalog.Offers.FirstOrDefault(offer => offer.Id == offerId);
		if (offer == null) return ShopRejected("Refresh the Shop before choosing an offer.");
		if (offer.Id == "dig" && NextDigAt is DateTimeOffset next && next > ServerTime)
			return ShopRejected("Your next Dig Box is still on cooldown.");
		return StartShopRequest("v2/shop/purchase", new ShopPurchaseRequest(Guid.NewGuid(), offerId, EconomyVersion!), cancellationToken, settleGrants: true);
	}

	public Task<bool> RecycleAsync(int itemDefId, long quantity, long equippedQuantity, CancellationToken cancellationToken)
	{
		var owned = InventoryItems.Where(item => item.ItemDefId == itemDefId).Sum(item => item.Quantity);
		var cosmetic = ShopCatalog.Offers.Any(offer => (offer.Kind is "dog" or "accessory") && offer.RewardItemDefIds.Contains(itemDefId));
		if (!cosmetic || quantity < 1 || quantity > ShopCatalog.MaxRecycleQuantity || equippedQuantity < 0 || quantity > owned - Math.Max(1, equippedQuantity))
			return ShopRejected("Keep at least one copy and every copy currently in use.");
		return StartShopRequest("v2/shop/recycle", new ShopRecycleRequest(Guid.NewGuid(), itemDefId, quantity, equippedQuantity, EconomyVersion!), cancellationToken);
	}

	public async Task<bool> RefreshShopAsync(CancellationToken cancellationToken)
	{
		if (_disposed || !IsAuthenticated) return false;
		var acquired = false;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true;
			_requestInFlight = true;
			if (!IsAuthenticated) return false;
			return await RefreshShopUnderLockAsync(cancellationToken);
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception exception)
		{
			ShopStatus = IsAuthenticated ? $"Shop refresh failed: {exception.Message}" : "Reconnect to Steam to use the Shop.";
			return false;
		}
		finally
		{
			if (acquired) { _requestInFlight = false; _requests.Release(); }
		}
	}

	// Opening the Shop settles only the input prefix present at entry. Inputs
	// observed during this request remain queued for the next scheduled flush.
	public Task<bool> SyncShopAsync(CancellationToken cancellationToken)
	{
		if (_disposed || cancellationToken.IsCancellationRequested || !IsAuthenticated) return Task.FromResult(false);
		if (IsShopOperationInFlight) return ShopRejected("Wait for the current Shop request to finish.");
		_shopForegroundReserved = true;
		ShopStatus = CheckingPetsShopStatus;
		return SyncShopPrefixAsync(CaptureGrantPrefix(), cancellationToken);
	}

	private async Task<bool> SyncShopPrefixAsync(GrantPrefix prefix, CancellationToken cancellationToken)
	{
		var acquired = false;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true; _requestInFlight = true;
			if (!IsAuthenticated || prefix.RunId != _grantRunId) return false;
			// Existing accepted purchases retain their ordinary recovery path. Do
			// not place a fresh grant ahead of an unresolved inventory exchange.
			if (!HasPendingExchange && !await SettleGrantPrefixUnderLockAsync(prefix, cancellationToken))
				return await ShopRejected("Pet syncing has not finished. Please retry when the service is ready.");
			return await RefreshShopUnderLockAsync(cancellationToken);
		}
		catch (OperationCanceledException) { return false; }
		catch (Exception exception) { ShopStatus = $"Shop refresh failed: {exception.Message}"; return false; }
		finally
		{
			if (acquired) { _requestInFlight = false; _requests.Release(); }
			_shopForegroundReserved = false;
		}
	}

	private async Task<bool> RefreshShopUnderLockAsync(CancellationToken cancellationToken)
	{
		if (!IsAuthenticated) return false;
		using var response = await _httpClient.GetAsync("v2/shop", cancellationToken);
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			ShopStatus = "The updated Shop is not available on this server yet.";
			return false;
		}
		await RequireSuccessAsync(response, cancellationToken);
		ApplyShop(await response.Content.ReadFromJsonAsync<ShopResponse>(cancellationToken: cancellationToken), true);
		ShopStatus = HasPendingShopOperation ? "Steam is confirming your previous transaction." : ShopCapabilities.ShopEnabled ? "" : "The new Shop is being prepared.";
		return true;
	}

	public Task<bool> AcknowledgeReceiptAsync(Guid clientEventId, CancellationToken cancellationToken)
	{
		if (clientEventId == Guid.Empty || !UnacknowledgedReceipts.Any(receipt => receipt.ClientEventId == clientEventId)) return Task.FromResult(false);
		_pendingReceiptAckIds.Add(clientEventId);
		if (_shopAckInFlight) return Task.FromResult(false);
		return SendShopAcknowledgmentAsync(clientEventId, cancellationToken);
	}

	private Task<bool> RetryShopAcknowledgmentAsync(CancellationToken cancellationToken) =>
		_pendingReceiptAckIds.Count == 0 || _shopAckInFlight ? Task.FromResult(false) : SendShopAcknowledgmentAsync(_pendingReceiptAckIds.First(), cancellationToken);

#if DEBUG
	public async Task<bool> AuthenticateTestEconomyAsync(CancellationToken cancellationToken)
	{
		if (_disposed || _authInFlight || IsAuthenticated || Environment.GetEnvironmentVariable("PDD_DEV_ECONOMY") != "1" ||
			Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") != "1" || _httpClient.BaseAddress is not Uri backend ||
			backend.Scheme != Uri.UriSchemeHttp || !backend.IsLoopback) return false;
		_authInFlight = true;
		var acquired = false;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true;
			// This fixed fake ticket is only accepted by the isolated local fake-Steam binding.
			// No real Steam initialization, ticket, publisher secret, or inventory is involved.
			using var response = await _httpClient.PostAsJsonAsync("v1/auth/steam", new SteamAuthRequest("aabb", SteamIntegration.BackendIdentity), cancellationToken);
			await RequireSuccessAsync(response, cancellationToken);
			var auth = await response.Content.ReadFromJsonAsync<SteamAuthResponse>(cancellationToken: cancellationToken);
			if (auth == null || string.IsNullOrWhiteSpace(auth.SessionToken) || string.IsNullOrWhiteSpace(auth.SteamId)) throw new InvalidOperationException("The local test server did not authenticate.");
			_maxGrantBatchSize = Math.Clamp(auth.MaxGrantBatchSize ?? 300, 1, 300);
			_sessionToken = auth.SessionToken; _authenticatedSteamId = auth.SteamId;
			_httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _sessionToken);
			_requests.Release(); acquired = false;
			if (!await RefreshShopAsync(cancellationToken) || !ShopCapabilities.DeveloperControls) throw new InvalidOperationException("This server is not an isolated test economy.");
			Status = "Isolated test economy connected. No real Steam inventory is used.";
			return true;
		}
		catch (Exception exception)
		{
			ResetSession(); ResetShopAccount(); ConfirmedPets = null; InventoryItems = Array.Empty<SteamInventoryItem>();
			ShopStatus = $"Test economy connection failed: {exception.Message}";
			return false;
		}
		finally { if (acquired) _requests.Release(); _authInFlight = false; }
	}

	public Task<bool> TestSeedCurrenciesAsync(long pets, long goldenBones, CancellationToken cancellationToken) =>
		SendDeveloperRequestAsync("v2/shop/dev/seed", new { clientEventId = Guid.NewGuid(), pets, goldenBones }, cancellationToken);
	public Task<bool> TestResetCooldownAsync(CancellationToken cancellationToken) =>
		SendDeveloperRequestAsync("v2/shop/dev/reset-cooldown", new { clientEventId = Guid.NewGuid() }, cancellationToken);
	private async Task<bool> SendDeveloperRequestAsync<T>(string path, T body, CancellationToken cancellationToken)
	{
		if (!ShopCapabilities.DeveloperControls || Environment.GetEnvironmentVariable("PDD_DEV_ECONOMY") != "1" || !IsAuthenticated || IsShopOperationInFlight || HasPendingExchange) return false;
		_shopRequestInFlight = true;
		var acquired = false;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true;
			using var response = await _httpClient.PostAsJsonAsync(path, body, cancellationToken);
			await RequireSuccessAsync(response, cancellationToken);
			ApplyShop(await response.Content.ReadFromJsonAsync<ShopResponse>(cancellationToken: cancellationToken), true);
			ShopStatus = "Isolated test economy updated.";
			return true;
		}
		catch (Exception exception) { ShopStatus = $"Test economy request failed: {exception.Message}"; return false; }
		finally { if (acquired) _requests.Release(); _shopRequestInFlight = false; }
	}
#endif

	private async Task<bool> SendShopAcknowledgmentAsync(Guid clientEventId, CancellationToken cancellationToken)
	{
		if (_disposed || !IsAuthenticated) return false;
		_shopAckInFlight = true;
		var acquired = false;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true;
			_requestInFlight = true;
			using var response = await _httpClient.PostAsJsonAsync("v2/shop/ack", new { clientEventId }, cancellationToken);
			await RequireSuccessAsync(response, cancellationToken);
			ApplyShop(await response.Content.ReadFromJsonAsync<ShopResponse>(cancellationToken: cancellationToken), true);
			_pendingReceiptAckIds.Remove(clientEventId);
			_shopAckRetryDelay = 0;
			return true;
		}
		catch (OperationCanceledException) { _shopAckRetryDelay = 5; return false; }
		catch (Exception) { _shopAckRetryDelay = 5; ShopStatus = "Your reward is safe. Its confirmation will retry when the Shop reconnects."; return false; }
		finally { if (acquired) { _requestInFlight = false; _requests.Release(); } _shopAckInFlight = false; }
	}

	private Task<bool> StartShopRequest(string path, IShopRequest body, CancellationToken cancellationToken, bool settleGrants = false)
	{
		if (_disposed || cancellationToken.IsCancellationRequested) return Task.FromResult(false);
		if (!IsAuthenticated) return ShopRejected("Connect to Steam before purchasing.");
		if (!ShopCapabilities.ShopEnabled || string.IsNullOrEmpty(EconomyVersion)) return ShopRejected("The new Shop is not available yet.");
		if (IsShopOperationInFlight || HasPendingExchange) return ShopRejected("Wait for Steam to confirm the previous transaction.");
		if (settleGrants)
		{
			_shopForegroundReserved = true;
			ShopStatus = CheckingPetsShopStatus;
			return PrepareShopPurchaseAsync(path, body, CaptureGrantPrefix(), cancellationToken);
		}
		MarkShopRequestPending(path, body);
		return ProcessShopOperationAsync(false, cancellationToken);
	}

	private void MarkShopRequestPending(string path, IShopRequest body)
	{
		_pendingShopRequest = new(path, body);
		_pendingShopOperationId = body.ClientEventId;
		ReceivedItems = Array.Empty<SteamInventoryItem>();
		// Set this before the first await. Tick yields to this queued request, even during a stream of clicks.
		_shopRequestInFlight = true;
		ShopStatus = "Confirming with Steam…";
	}

	private async Task<bool> PrepareShopPurchaseAsync(string path, IShopRequest body, GrantPrefix prefix, CancellationToken cancellationToken)
	{
		try
		{
			await _requests.WaitAsync(cancellationToken);
			_requestInFlight = true;
			try
			{
				if (!IsAuthenticated || HasPendingExchange || !await SettleGrantPrefixUnderLockAsync(prefix, cancellationToken))
					return await ShopRejected("Pet syncing has not finished. Your purchase was not sent; please retry when the service is ready.");
				// Reserving foreground work did not create a purchase. Its stable
				// operation begins only after every captured grant is confirmed.
				MarkShopRequestPending(path, body);
			}
			finally { _requestInFlight = false; _requests.Release(); }
			return await ProcessShopOperationAsync(false, cancellationToken);
		}
		catch (OperationCanceledException) { return await ShopRejected("Purchase canceled before it was sent."); }
		catch (Exception exception) { return await ShopRejected($"Purchase was not sent: {exception.Message}"); }
		finally { _shopForegroundReserved = false; }
	}

	private Task<bool> RecoverShopOperationAsync(CancellationToken cancellationToken)
	{
		if (_shopRequestInFlight || !_pendingShopOperationId.HasValue) return Task.FromResult(false);
		_shopRequestInFlight = true;
		return ProcessShopOperationAsync(true, cancellationToken);
	}

	private async Task<bool> ProcessShopOperationAsync(bool recover, CancellationToken cancellationToken)
	{
		var acquired = false;
		var mayHaveSent = false;
		var requestedId = _pendingShopOperationId;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true;
			_requestInFlight = true;
			cancellationToken.ThrowIfCancellationRequested();
			if (!IsAuthenticated || !_pendingShopOperationId.HasValue) return false;
			if (!recover) mayHaveSent = true;
			using var response = recover
				? await _httpClient.GetAsync($"v2/shop/operations/{_pendingShopOperationId.Value:D}", cancellationToken)
				: await PostPendingShopRequestAsync(cancellationToken);
			await RequireSuccessAsync(response, cancellationToken);
			var shop = await response.Content.ReadFromJsonAsync<ShopResponse>(cancellationToken: cancellationToken);
			if (shop?.Status == "not_found")
			{
				// The server confirmed it never accepted this ID. Retrying the same request is safe;
				// the Worker atomically owns the operation before any non-idempotent Steam exchange.
				if (_pendingShopRequest == null) { ClearShopPending(); ShopStatus = "The previous transaction was not accepted."; return false; }
				using var retry = await PostPendingShopRequestAsync(cancellationToken);
				await RequireSuccessAsync(retry, cancellationToken);
				shop = await retry.Content.ReadFromJsonAsync<ShopResponse>(cancellationToken: cancellationToken);
			}
			if (shop?.Status == "rejected")
			{
				ApplyShop(shop, false);
				ClearShopPending();
				ShopStatus = "Steam did not complete this transaction. Your inventory has been refreshed.";
				return false;
			}
			if (shop?.Status != "complete")
			{
				// Never present a reward or apply an optimistic wallet from a pending operation response.
				_retryDelay = RetryDelaySeconds;
				ShopStatus = "Steam is still confirming your transaction. It will recover automatically.";
				return false;
			}
			if (shop.OperationId != requestedId && !(shop.Receipts?.Any(receipt => receipt.ClientEventId == requestedId) ?? false))
				throw new InvalidOperationException("Steam returned a receipt for another transaction.");
			ApplyShop(shop, true);
			ReceivedItems = Array.AsReadOnly(shop.ReceivedItems ?? Array.Empty<SteamInventoryItem>());
			ClearShopPending();
			ShopStatus = "Confirmed in Steam inventory.";
			return true;
		}
		catch (BackendRequestException exception) when (exception.Code == "exchange_pending")
		{
			// Another device may have started a transaction since our last snapshot.
			// Keep its recovered ID, but abandon our never-accepted request.
			if (_pendingExchange != null && _pendingShopOperationId == requestedId) ClearShopPending();
			_retryDelay = RetryDelaySeconds;
			ShopStatus = "Steam is confirming a previous transaction.";
		}
		catch (BackendRequestException exception) when (IsDefiniteShopRejection(exception))
		{
			ClearShopPending();
			ShopStatus = exception.Message;
		}
		catch (OperationCanceledException)
		{
			if (!recover && !mayHaveSent && _pendingShopOperationId == requestedId)
			{
				// A cancelled wait for the request gate cannot have reached Steam.
				// Do not later turn this local reservation into an automatic purchase.
				ClearShopPending();
				ShopStatus = "Purchase canceled before it was sent.";
			}
			else
			{
				_retryDelay = RetryDelaySeconds;
				ShopStatus = "Transaction awaiting confirmation. Reconnect to recover its result.";
			}
		}
		catch (Exception exception)
		{
			_retryDelay = RetryDelaySeconds;
			ShopStatus = IsAuthenticated ? $"Transaction awaiting confirmation: {exception.Message}" : "Reconnect to Steam to recover this transaction.";
		}
		finally
		{
			if (acquired) { _requestInFlight = false; _requests.Release(); }
			_shopRequestInFlight = false;
		}
		return false;
	}

	private Task<System.Net.Http.HttpResponseMessage> PostPendingShopRequestAsync(CancellationToken cancellationToken)
	{
		var request = _pendingShopRequest ?? throw new InvalidOperationException("Missing transaction request.");
		return request.Body switch
		{
			ShopPurchaseRequest purchase => _httpClient.PostAsJsonAsync(request.Path, purchase, cancellationToken),
			ShopRecycleRequest recycle => _httpClient.PostAsJsonAsync(request.Path, recycle, cancellationToken),
			_ => throw new InvalidOperationException("Unknown transaction request."),
		};
	}

	private void ApplyShop(ShopResponse? shop, bool requireInventory)
	{
		if (shop == null) throw new InvalidOperationException("No Shop response was returned.");
		if (requireInventory && (shop.Pets is not >= 0 || shop.GoldenBones is not >= 0 || shop.Items == null || shop.ServerTime == default || string.IsNullOrEmpty(shop.EconomyVersion)))
			throw new InvalidOperationException("No confirmed Shop inventory was returned.");
		if (shop.Pets is >= 0 && shop.GoldenBones is >= 0 && shop.Items != null)
		{
			if (shop.Items.Any(item => item.Quantity < 0)) throw new InvalidOperationException("Invalid Steam quantity.");
			ApplyInventory(shop.Pets, shop.Items);
			ConfirmedGoldenBones = shop.GoldenBones;
		}
		if (shop.Catalog != null) ShopCatalog = shop.Catalog;
		if (shop.Capabilities != null) ShopCapabilities = shop.Capabilities;
		if (!string.IsNullOrEmpty(shop.EconomyVersion)) EconomyVersion = shop.EconomyVersion;
		if (shop.ServerTime != default) { _serverTime = shop.ServerTime; _serverElapsed.Restart(); NextDigAt = shop.NextDigAt; }
		if (shop.Receipts != null)
		{
			if (shop.Receipts.Any(receipt => receipt.ClientEventId == Guid.Empty || receipt.ReceivedItems.Any(item => item.Quantity <= 0)))
				throw new InvalidOperationException("Invalid Shop receipt.");
			UnacknowledgedReceipts = Array.AsReadOnly(shop.Receipts);
			_pendingReceiptAckIds.RemoveWhere(id => !shop.Receipts.Any(receipt => receipt.ClientEventId == id));
			if (_pendingShopOperationId is Guid id && shop.Receipts.Any(receipt => receipt.ClientEventId == id)) ClearShopPending();
			if (_pendingExchange != null && shop.Receipts.Any(receipt => receipt.ClientEventId == _pendingExchange.ClientEventId)) _pendingExchange = null;
		}
		if (shop.PendingOperation != null && !(_pendingExchange != null && shop.PendingOperation.ClientEventId == _pendingExchange.ClientEventId.ToString("D"))) RecoverShopPending(shop.PendingOperation);
	}

	private void RecoverShopPending(ShopPendingOperation operation)
	{
		if (!Guid.TryParse(operation.ClientEventId, out var id) || id == Guid.Empty) throw new InvalidOperationException("Invalid pending transaction ID.");
		if (_pendingShopOperationId != id) _pendingShopRequest = null;
		_pendingShopOperationId = id;
	}

	private void ApplyGoldenBonesFromInventory(IEnumerable<SteamInventoryItem> items)
	{
		ConfirmedGoldenBones = items.Where(item => item.ItemDefId == 101).Sum(item => item.Quantity);
	}

	private Task<bool> ShopRejected(string message) { ShopStatus = message; return Task.FromResult(false); }
	private void ClearShopPending() { _pendingShopOperationId = null; _pendingShopRequest = null; _retryDelay = 0; }
	private void ResetShopAccount()
	{
		ClearShopPending(); ConfirmedGoldenBones = null; ShopCatalog = new(); ShopCapabilities = new(); EconomyVersion = null;
		_pendingReceiptAckIds.Clear(); _shopAckRetryDelay = 0;
		NextDigAt = null; _serverTime = default; _serverElapsed.Reset(); UnacknowledgedReceipts = Array.Empty<ShopReceipt>();
	}
	private static bool IsDefiniteShopRejection(BackendRequestException exception) =>
		exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.Gone ||
		(exception.StatusCode == HttpStatusCode.ServiceUnavailable && exception.Code is "shop_unavailable" or "grant_pending") ||
		(exception.StatusCode == HttpStatusCode.Conflict && exception.Code is "insufficient_pets" or "insufficient_items" or "dig_cooldown" or "operation_conflict" or "exchange_rejected" or "no_spare_copy" or "update_required");
	private interface IShopRequest { Guid ClientEventId { get; } }
	private sealed record ShopPurchaseRequest(Guid ClientEventId, string OfferId, string EconomyVersion) : IShopRequest;
	private sealed record ShopRecycleRequest(Guid ClientEventId, int ItemDefId, long Quantity, long EquippedQuantity, string EconomyVersion) : IShopRequest;
	private sealed record PendingShopRequest(string Path, IShopRequest Body);
}
