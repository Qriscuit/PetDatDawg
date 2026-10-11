using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public sealed record SteamInventoryItem(string ItemId, int ItemDefId, long Quantity);

public sealed class BackendPetClient : IDisposable
{
	private const string DefaultBackendUrl = "http://127.0.0.1:5155";
	private const double RetryDelaySeconds = 2.0;
	private readonly System.Net.Http.HttpClient _httpClient;
	// Apply every inventory response in request order, including clicks and exchanges.
	private readonly SemaphoreSlim _requests = new(1, 1);
	private readonly Queue<Guid> _pendingGrantIds = new();
	private string? _sessionToken;
	private string? _authenticatedSteamId;
	private bool _authInFlight;
	private bool _requestInFlight;
	private bool _exchangeInFlight;
	private bool _disposed;
	private double _retryDelay;
	private PendingExchange? _pendingExchange;

	public BackendPetClient() : this(new System.Net.Http.HttpClient
		{
			BaseAddress = new Uri(GetBackendUrl()),
			Timeout = TimeSpan.FromSeconds(15.0),
		})
	{
	}

	public BackendPetClient(System.Net.Http.HttpClient httpClient)
	{
		_httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
		_httpClient.BaseAddress ??= new Uri(GetBackendUrl());
	}

	public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_sessionToken);
	public int PendingGrantCount => _pendingGrantIds.Count;
	public long? ConfirmedPets { get; private set; }
	public IReadOnlyList<SteamInventoryItem> InventoryItems { get; private set; } = Array.Empty<SteamInventoryItem>();
	public IReadOnlyList<SteamInventoryItem> ReceivedItems { get; private set; } = Array.Empty<SteamInventoryItem>();
	public bool IsOperationInFlight => _authInFlight || _requestInFlight || _exchangeInFlight;
	public bool HasPendingExchange => _pendingExchange != null;
	public string Status { get; private set; } = "Backend is not authenticated.";

	public void EnqueuePetGrant(Guid clientEventId) => _pendingGrantIds.Enqueue(clientEventId);

	public void Tick(double delta, CancellationToken cancellationToken)
	{
		_retryDelay = Math.Max(0.0, _retryDelay - delta);
		if (_disposed || cancellationToken.IsCancellationRequested || !IsAuthenticated || IsOperationInFlight || _retryDelay > 0.0)
			return;
		// Resolve an uncertain exchange before any new Steam inventory mutation.
		if (_pendingExchange != null) _ = ProcessPendingExchangeAsync(cancellationToken);
		else if (_pendingGrantIds.Count > 0) _ = GrantNextPetAsync(cancellationToken);
	}

	public async Task AuthenticateAsync(SteamIntegration steam, CancellationToken cancellationToken)
	{
		if (_disposed || _authInFlight || IsAuthenticated) return;
		_authInFlight = true;
		try
		{
			var ticketHex = await steam.RequestBackendTicketAsync(cancellationToken);
			await _requests.WaitAsync(cancellationToken);
			_requestInFlight = true;
			try
			{
				using var response = await _httpClient.PostAsJsonAsync("v1/auth/steam",
					new SteamAuthRequest(ticketHex, SteamIntegration.BackendIdentity), cancellationToken);
				await RequireSuccessAsync(response, cancellationToken);
				var auth = await response.Content.ReadFromJsonAsync<SteamAuthResponse>(cancellationToken: cancellationToken);
				if (auth == null || string.IsNullOrWhiteSpace(auth.SessionToken) || string.IsNullOrWhiteSpace(auth.SteamId))
					throw new InvalidOperationException("Backend auth returned an empty session.");
				if (_authenticatedSteamId != null && _authenticatedSteamId != auth.SteamId)
				{
					ConfirmedPets = null;
					InventoryItems = Array.Empty<SteamInventoryItem>();
					ReceivedItems = Array.Empty<SteamInventoryItem>();
					_pendingExchange = null;
					_pendingGrantIds.Clear();
				}
				_authenticatedSteamId = auth.SteamId;
				_sessionToken = auth.SessionToken;
				_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _sessionToken);
				Status = $"Backend authenticated for Steam {auth.SteamId}.";
			}
			finally
			{
				_requestInFlight = false;
				_requests.Release();
			}
			await RefreshInventoryAsync(cancellationToken);
		}
		catch (OperationCanceledException) { Status = "Backend auth canceled."; }
		catch (Exception exception)
		{
			ResetSession();
			Status = $"Backend auth failed: {exception.Message}";
			GD.PushWarning(Status);
		}
		finally { _authInFlight = false; }
	}

	public Task<bool> BuyBoxAsync(string boxType, CancellationToken cancellationToken)
	{
		if (boxType is not ("dog" or "accessory"))
		{
			Status = "Choose a dog box or an accessory box.";
			return Task.FromResult(false);
		}
		return StartExchangeAsync(new PendingExchange(Guid.NewGuid(), "buy", boxType, null), cancellationToken);
	}

	public Task<bool> OpenBoxAsync(string boxItemId, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(boxItemId))
		{
			Status = "Choose a Steam inventory box to open.";
			return Task.FromResult(false);
		}
		return StartExchangeAsync(new PendingExchange(Guid.NewGuid(), "open", null, boxItemId), cancellationToken);
	}

	public async Task<bool> RefreshInventoryAsync(CancellationToken cancellationToken)
	{
		if (_disposed || !IsAuthenticated) return false;
		var acquired = false;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true;
			_requestInFlight = true;
			if (!IsAuthenticated) return false;
			using var response = await _httpClient.GetAsync("v1/inventory", cancellationToken);
			await RequireSuccessAsync(response, cancellationToken);
			var inventory = await response.Content.ReadFromJsonAsync<InventoryResponse>(cancellationToken: cancellationToken);
			ApplyInventory(inventory?.Pets, inventory?.Items);
			RecoverPendingExchange(inventory?.PendingOperation);
			Status = HasPendingExchange ? "Steam is still confirming the previous box exchange." : $"Steam inventory synced: {ConfirmedPets} Pets.";
			return true;
		}
		catch (OperationCanceledException) { Status = "Inventory refresh canceled."; }
		catch (Exception exception)
		{
			Status = IsAuthenticated ? $"Inventory refresh failed: {exception.Message}" : "Steam session expired; reconnecting.";
			GD.PushWarning(Status);
		}
		finally
		{
			if (acquired)
			{
				_requestInFlight = false;
				_requests.Release();
			}
		}
		return false;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		ResetSession();
		_httpClient.Dispose();
	}

	private Task<bool> StartExchangeAsync(PendingExchange exchange, CancellationToken cancellationToken)
	{
		if (_disposed || cancellationToken.IsCancellationRequested) return Task.FromResult(false);
		if (!IsAuthenticated)
		{
			Status = "Connect to Steam before buying or opening a box.";
			return Task.FromResult(false);
		}
		if (_pendingExchange != null)
		{
			Status = "Wait for Steam to confirm the previous box exchange.";
			return Task.FromResult(false);
		}
		_pendingExchange = exchange;
		ReceivedItems = Array.Empty<SteamInventoryItem>();
		return ProcessPendingExchangeAsync(cancellationToken);
	}

	private async Task<bool> ProcessPendingExchangeAsync(CancellationToken cancellationToken)
	{
		if (_exchangeInFlight || _pendingExchange == null) return false;
		_exchangeInFlight = true;
		var exchange = _pendingExchange;
		var acquired = false;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true;
			_requestInFlight = true;
			if (!IsAuthenticated) return false;
			using var response = exchange.Kind == "buy"
				? await _httpClient.PostAsJsonAsync("v1/boxes/buy", new BuyBoxRequest(exchange.ClientEventId, exchange.BoxType!), cancellationToken)
				: await _httpClient.PostAsJsonAsync("v1/boxes/open", new OpenBoxRequest(exchange.ClientEventId, exchange.BoxItemId!), cancellationToken);
			await RequireSuccessAsync(response, cancellationToken);
			var result = await response.Content.ReadFromJsonAsync<ExchangeResponse>(cancellationToken: cancellationToken);
			if (response.StatusCode == HttpStatusCode.Accepted || result?.Status == "pending")
			{
				// Never interpret an uncertain exchange as a new operation on the next retry.
				_retryDelay = RetryDelaySeconds;
				Status = "Steam is still confirming the box exchange.";
				return false;
			}
			if (result?.Status != "complete" || result.ReceivedItems == null)
				throw new InvalidOperationException("Backend exchange did not return a confirmed result.");
			ApplyInventory(result.Pets, result.Items);
			ReceivedItems = Array.AsReadOnly(result.ReceivedItems);
			_pendingExchange = null;
			_retryDelay = 0;
			Status = exchange.Kind == "buy" ? "Box added to your Steam inventory." : "Box opened in Steam inventory.";
			return true;
		}
		catch (BackendRequestException exception) when (IsDefiniteExchangeRejection(exception))
		{
			_pendingExchange = null;
			Status = exception.Message;
		}
		catch (OperationCanceledException)
		{
			_retryDelay = RetryDelaySeconds;
			Status = "Box exchange pending confirmation.";
		}
		catch (Exception exception)
		{
			_retryDelay = RetryDelaySeconds;
			Status = IsAuthenticated ? $"Box exchange pending confirmation: {exception.Message}" : "Steam session expired; reconnecting before checking the box exchange.";
			GD.PushWarning(Status);
		}
		finally
		{
			if (acquired)
			{
				_requestInFlight = false;
				_requests.Release();
			}
			_exchangeInFlight = false;
		}
		return false;
	}

	private async Task GrantNextPetAsync(CancellationToken cancellationToken)
	{
		if (_pendingGrantIds.Count == 0) return;
		var acquired = false;
		var clientEventId = _pendingGrantIds.Peek();
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true;
			_requestInFlight = true;
			if (!IsAuthenticated || HasPendingExchange) return;
			using var response = await _httpClient.PostAsJsonAsync("v1/pets/grant", new GrantPetRequest(clientEventId), cancellationToken);
			await RequireSuccessAsync(response, cancellationToken);
			var grant = await response.Content.ReadFromJsonAsync<GrantPetResponse>(cancellationToken: cancellationToken);
			if (grant?.Pets is not long pets || pets < 0)
				throw new InvalidOperationException("Backend grant returned no confirmed pets total.");
			_pendingGrantIds.Dequeue();
			if (grant.Items != null) ApplyInventory(pets, grant.Items);
			else ConfirmedPets = pets; // The older ASP.NET grant response contains only the total.
			Status = $"Pets synced: {ConfirmedPets}.";
		}
		catch (OperationCanceledException)
		{
			_retryDelay = RetryDelaySeconds;
			Status = "Pet grant pending retry.";
		}
		catch (Exception exception)
		{
			_retryDelay = RetryDelaySeconds;
			Status = IsAuthenticated ? $"Pet grant pending retry: {exception.Message}" : "Steam session expired; reconnecting before syncing clicks.";
			GD.PushWarning(Status);
		}
		finally
		{
			if (acquired)
			{
				_requestInFlight = false;
				_requests.Release();
			}
		}
	}

	private void ApplyInventory(long? pets, SteamInventoryItem[]? items)
	{
		if (pets is not long confirmed || confirmed < 0 || items == null)
			throw new InvalidOperationException("Backend returned no confirmed Steam inventory.");
		ConfirmedPets = confirmed;
		InventoryItems = Array.AsReadOnly(items);
	}

	private async Task RequireSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode) return;
		if (response.StatusCode == HttpStatusCode.Unauthorized) ResetSession();
		BackendError? error = null;
		try { error = await response.Content.ReadFromJsonAsync<BackendError>(cancellationToken: cancellationToken); }
		catch (JsonException) { }
		catch (NotSupportedException) { }
		if (error?.Code == "exchange_pending") RecoverPendingExchange(error.PendingOperation);
		var message = error?.Error ?? $"Backend returned HTTP {(int)response.StatusCode}.";
		if (error?.SteamEndpoint is
			"ISteamUserAuth/AuthenticateUserTicket" or "ISteamUser/CheckAppOwnership" or
			"IInventoryService/GetInventory" or "IInventoryService/AddItem" or "IInventoryService/ExchangeItem")
		{
			var details = new List<string>();
			if (error.SteamHttpStatus is >= 100 and <= 599) details.Add($"HTTP {error.SteamHttpStatus}");
			var category = error.SteamFailureCategory.ValueKind == JsonValueKind.String ? error.SteamFailureCategory.GetString() : null;
			// These fixed Worker labels describe the response, not a confirmed cause.
			if (category is "item_properties_error" or "item_definition_error" or "inventory_disabled" or "permission_denied" or
				"invalid_parameters" or "steam_rejected" or "missing_response" or "invalid_success_flag" or "missing_item_json" or
				"invalid_item_json" or "invalid_item_data" or "invalid_response_json" or "invalid_response_document" or
				"request_timeout" or "connection_failed" or "missing_pets_receipt" or "invalid_replayed_flag")
			{
				var detail = category;
				if (error.SteamResult.ValueKind == JsonValueKind.Number && error.SteamResult.TryGetInt32(out var result) && result is >= 0 and <= 65535)
					detail += $", Steam result {result}";
				details.Add(detail);
			}
			if (details.Count > 0) message += $" ({error.SteamEndpoint}: {string.Join("; ", details)}.)";
		}
		throw new BackendRequestException(response.StatusCode, error?.Code,
			message);
	}

	private void RecoverPendingExchange(PendingOperationResponse? operation)
	{
		if (operation == null) return;
		if (!Guid.TryParse(operation.ClientEventId, out var clientEventId) ||
			(operation.Kind == "buy" && operation.BoxType is not ("dog" or "accessory")) ||
			(operation.Kind == "open" && string.IsNullOrWhiteSpace(operation.BoxItemId)) ||
			operation.Kind is not ("buy" or "open"))
			throw new InvalidOperationException("Backend returned an invalid pending exchange.");
		_pendingExchange = new PendingExchange(clientEventId, operation.Kind, operation.BoxType, operation.BoxItemId);
	}

	private static bool IsDefiniteExchangeRejection(BackendRequestException exception) =>
		exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound ||
		(exception.StatusCode == HttpStatusCode.Conflict && exception.Code is "insufficient_pets" or "operation_conflict" or "exchange_rejected" or "box_not_found");

	private void ResetSession()
	{
		_sessionToken = null;
		_httpClient.DefaultRequestHeaders.Authorization = null;
	}

	private static string GetBackendUrl()
	{
		var configured = System.Environment.GetEnvironmentVariable("PDD_BACKEND_URL");
		if (string.IsNullOrWhiteSpace(configured) && ProjectSettings.HasSetting("pdd/backend_url"))
			configured = ProjectSettings.GetSetting("pdd/backend_url").AsString();
		var url = string.IsNullOrWhiteSpace(configured) ? DefaultBackendUrl : configured.Trim();
		return url.EndsWith("/", StringComparison.Ordinal) ? url : $"{url}/";
	}

	private sealed record PendingExchange(Guid ClientEventId, string Kind, string? BoxType, string? BoxItemId);
	private sealed record SteamAuthRequest(string TicketHex, string Identity);
	private sealed record SteamAuthResponse(string SessionToken, string SteamId);
	private sealed record GrantPetRequest(Guid ClientEventId);
	private sealed record GrantPetResponse(long? Pets, int Granted, bool Replayed, SteamInventoryItem[]? Items);
	private sealed record BuyBoxRequest(Guid ClientEventId, string BoxType);
	private sealed record OpenBoxRequest(Guid ClientEventId, string BoxItemId);
	private sealed record InventoryResponse(long? Pets, SteamInventoryItem[]? Items, PendingOperationResponse? PendingOperation);
	private sealed record ExchangeResponse(long? Pets, SteamInventoryItem[]? Items, SteamInventoryItem[]? ReceivedItems, string? Status);
	private sealed record PendingOperationResponse(string ClientEventId, string Kind, string? BoxType, string? BoxItemId);
	private sealed record BackendError(string? Error, string? Code, PendingOperationResponse? PendingOperation,
		string? SteamEndpoint = null, int? SteamHttpStatus = null,
		JsonElement SteamFailureCategory = default, JsonElement SteamResult = default);
	private sealed class BackendRequestException(HttpStatusCode statusCode, string? code, string message) : Exception(message)
	{
		public HttpStatusCode StatusCode { get; } = statusCode;
		public string? Code { get; } = code;
	}
}
