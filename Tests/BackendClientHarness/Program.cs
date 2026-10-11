using System.Net;
using System.Text;
using System.Text.Json;

await Run("401 renews authentication and preserves the click GUID", VerifyExpiredSessionAsync);
await Run("uncertain purchases reuse the original GUID and block new exchanges", VerifyUncertainPurchaseAsync);
await Run("202 responses do not apply an optimistic balance or rewards", VerifyPendingExchangeAsync);
await Run("grants and exchanges serialize their confirmed balance snapshots", VerifySerializedBalancesAsync);
await Run("server pending operations recover after a client restart", VerifyServerRecoveryAsync);
await Run("definite rejected exchanges permit a new purchase", VerifyDefiniteRejectionAsync);
await Run("Steam authentication sends the expected ticket identity and bearer session", VerifyAuthenticationContractAsync);
await Run("offline clicks grant once each in order without an optimistic total", VerifyQueuedClicksAsync);
await Run("uncertain and rate-limited grants retry one GUID without inventing pets", VerifyGrantRetriesAsync);
await Run("every inventory refresh replaces the total only with a valid Steam snapshot", VerifyAuthoritativeRefreshAsync);
await Run("changing Steam accounts discards the previous account's queued clicks", VerifyAccountChangeAsync);
await Run("Steam error diagnostics show only allowlisted endpoints, categories and bounded numeric results", VerifySafeSteamDiagnosticsAsync);
Console.WriteLine("BACKEND_CLIENT_HARNESS_PASS: 12 scenarios");

static async Task Run(string name, Func<Task> scenario)
{
	await scenario();
	Console.WriteLine($"PASS: {name}");
}

static async Task VerifyExpiredSessionAsync()
{
	var grantIds = new List<string>();
	var grantCalls = 0;
	using var client = CreateClient(async request =>
	{
		if (Path(request) == "/v1/pets/grant")
		{
			grantIds.Add(await EventId(request));
			return ++grantCalls == 1
				? Json(HttpStatusCode.Unauthorized, """{"error":"Session expired","code":"unauthorized"}""")
				: Json(HttpStatusCode.OK, """{"pets":11,"granted":1,"replayed":false,"items":[{"itemId":"10001","itemDefId":100,"quantity":11},{"itemId":"10002","itemDefId":2000,"quantity":1}]}""");
		}
		return AuthenticationOrInventory(request, 10);
	});
	var steam = new SteamIntegration();
	await client.AuthenticateAsync(steam, CancellationToken.None);
	var inventoryBefore = client.InventoryItems;
	client.EnqueuePetGrant(Guid.NewGuid());
	client.Tick(0, CancellationToken.None);
	await WaitForIdle(client);
	Require(!client.IsAuthenticated && client.PendingGrantCount == 1, "401 must clear the session without dropping the click.");
	Require(client.ConfirmedPets == 10 && ReferenceEquals(inventoryBefore, client.InventoryItems), "Expiration retains the previous confirmed snapshot.");
	await client.AuthenticateAsync(steam, CancellationToken.None);
	client.Tick(3, CancellationToken.None);
	await WaitForIdle(client);
	Require(steam.TicketRequests == 2 && client.IsAuthenticated, "Reauthentication must request a fresh Steam ticket.");
	Require(grantIds.Count == 2 && grantIds[0] == grantIds[1], "The grant retry must reuse the click GUID.");
	Require(client.PendingGrantCount == 0 && client.ConfirmedPets == 11, "Only the confirmed retry removes the click.");
	Require(client.InventoryItems.Count == 2 && client.InventoryItems[0].Quantity == 11 && client.InventoryItems[1].ItemDefId == 2000,
		"The confirmed grant refreshes currency stacks and cosmetic ownership from Steam's snapshot.");
}

static async Task VerifyUncertainPurchaseAsync()
{
	var purchaseIds = new List<string>();
	using var client = CreateClient(async request =>
	{
		if (Path(request) == "/v1/boxes/buy")
		{
			purchaseIds.Add(await EventId(request));
			if (purchaseIds.Count == 1) throw new HttpRequestException("Connection dropped after sending the exchange.");
			return Json(HttpStatusCode.OK, """{"pets":9,"items":[{"itemId":"box-1","itemDefId":2000,"quantity":4294967295}],"receivedItems":[{"itemId":"box-1","itemDefId":2000,"quantity":1}],"status":"complete"}""");
		}
		return AuthenticationOrInventory(request, 10);
	});
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	Require(!await client.BuyBoxAsync("dog", CancellationToken.None), "An uncertain response is not a confirmed purchase.");
	Require(client.HasPendingExchange && client.ConfirmedPets == 10, "An uncertain purchase retains the old confirmed balance.");
	Require(!await client.BuyBoxAsync("accessory", CancellationToken.None) && !await client.OpenBoxAsync("other-box", CancellationToken.None), "New purchases and opens must wait.");
	client.Tick(3, CancellationToken.None);
	await WaitForIdle(client);
	Require(purchaseIds.Count == 2 && purchaseIds[0] == purchaseIds[1], "Transport retries must use one operation GUID.");
	Require(!client.HasPendingExchange && client.ConfirmedPets == 9 && client.ReceivedItems.Count == 1, "Confirmed results update inventory and rewards.");
	Require(client.InventoryItems[0].Quantity == uint.MaxValue, "Steam quantities must support the full unsigned 32-bit range.");
}

static async Task VerifyPendingExchangeAsync()
{
	var openIds = new List<string>();
	using var client = CreateClient(async request =>
	{
		if (Path(request) == "/v1/boxes/open")
		{
			openIds.Add(await EventId(request));
			return openIds.Count == 1
				? Json(HttpStatusCode.Accepted, """{"status":"pending","pets":999,"receivedItems":[{"itemId":"unconfirmed","itemDefId":10001,"quantity":1}]}""")
				: Json(HttpStatusCode.OK, """{"pets":10,"items":[{"itemId":"reward","itemDefId":10001,"quantity":1}],"receivedItems":[{"itemId":"reward","itemDefId":10001,"quantity":1}],"status":"complete"}""");
		}
		return AuthenticationOrInventory(request, 10);
	});
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	Require(!await client.OpenBoxAsync("box-1", CancellationToken.None), "202 must remain pending.");
	Require(client.ConfirmedPets == 10 && client.ReceivedItems.Count == 0 && client.HasPendingExchange, "Pending payloads must not invent a balance or rewards.");
	client.Tick(3, CancellationToken.None);
	await WaitForIdle(client);
	Require(openIds.Count == 2 && openIds[0] == openIds[1], "Pending opens keep the original event ID.");
	Require(client.ReceivedItems[0].ItemId == "reward" && !client.HasPendingExchange, "Only confirmed open rewards become visible.");
}

static async Task VerifySerializedBalancesAsync()
{
	var grantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	var grantReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
	var buyCalls = 0;
	using var client = CreateClient(async request =>
	{
		if (Path(request) == "/v1/pets/grant")
		{
			grantStarted.SetResult();
			return await grantReply.Task;
		}
		if (Path(request) == "/v1/boxes/buy")
		{
			buyCalls++;
			return Json(HttpStatusCode.OK, """{"pets":4,"items":[{"itemId":"box","itemDefId":2001,"quantity":1}],"receivedItems":[{"itemId":"box","itemDefId":2001,"quantity":1}],"status":"complete"}""");
		}
		return AuthenticationOrInventory(request, 5);
	});
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	client.EnqueuePetGrant(Guid.NewGuid());
	client.Tick(0, CancellationToken.None);
	await grantStarted.Task;
	var purchase = client.BuyBoxAsync("accessory", CancellationToken.None);
	Require(buyCalls == 0, "The exchange must wait for the grant's inventory snapshot.");
	grantReply.SetResult(Json(HttpStatusCode.OK, """{"pets":6,"granted":1,"replayed":false}"""));
	Require(await purchase, "The purchase should succeed after the grant finishes.");
	Require(client.ConfirmedPets == 4 && client.PendingGrantCount == 0, "The final balance must reflect the later purchase, not an older grant response.");
}

static async Task VerifyServerRecoveryAsync()
{
	var pendingId = Guid.NewGuid().ToString();
	var retriedIds = new List<string>();
	var grantCalls = 0;
	using var client = CreateClient(async request =>
	{
		if (Path(request) == "/v1/inventory")
			return Json(HttpStatusCode.OK, $$$"""{"pets":3,"items":[],"pendingOperation":{"clientEventId":"{{{pendingId}}}","kind":"buy","boxType":"dog"}}""");
		if (Path(request) == "/v1/boxes/buy")
		{
			retriedIds.Add(await EventId(request));
			return Json(HttpStatusCode.Accepted, """{"status":"pending"}""");
		}
		if (Path(request) == "/v1/pets/grant") grantCalls++;
		return AuthenticationOrInventory(request, 3);
	});
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	Require(client.HasPendingExchange, "Authentication inventory must recover a server pending exchange.");
	Require(!await client.BuyBoxAsync("accessory", CancellationToken.None), "A recovered pending exchange blocks a new purchase.");
	client.EnqueuePetGrant(Guid.NewGuid());
	client.Tick(3, CancellationToken.None);
	await WaitForIdle(client);
	Require(retriedIds.Count == 1 && retriedIds[0] == pendingId && grantCalls == 0, "The original pending ID takes priority over new mutations.");
	Require(client.PendingGrantCount == 1, "Blocked pet clicks remain queued.");
}

static async Task VerifyDefiniteRejectionAsync()
{
	var purchaseIds = new List<string>();
	using var client = CreateClient(async request =>
	{
		if (Path(request) == "/v1/boxes/buy")
		{
			purchaseIds.Add(await EventId(request));
			return Json(HttpStatusCode.Conflict, """{"code":"insufficient_pets","error":"Not enough Pets."}""");
		}
		return AuthenticationOrInventory(request, 0);
	});
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	Require(!await client.BuyBoxAsync("dog", CancellationToken.None) && !client.HasPendingExchange, "A definite rejection must not trap the client in pending state.");
	await client.BuyBoxAsync("dog", CancellationToken.None);
	Require(purchaseIds.Count == 2 && purchaseIds[0] != purchaseIds[1], "A new explicitly requested purchase gets a new GUID after a definite rejection.");
	Require(client.ConfirmedPets == 0, "Rejected exchanges preserve Steam's confirmed balance.");
}

static async Task VerifyAuthenticationContractAsync()
{
	var inventoryCalls = 0;
	using var client = CreateClient(async request =>
	{
		if (Path(request) == "/v1/auth/steam")
		{
			using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
			Require(body.RootElement.GetProperty("ticketHex").GetString() == "fixture-ticket", "Authentication must forward Steam's Web API ticket.");
			Require(body.RootElement.GetProperty("identity").GetString() == SteamIntegration.BackendIdentity, "The ticket identity must match the Worker's validation identity.");
			Require(request.Headers.Authorization == null, "The first Steam authentication does not require a backend session.");
		}
		if (Path(request) == "/v1/inventory")
		{
			inventoryCalls++;
			Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "fixture-session", "Inventory requests must use the authenticated Worker session.");
		}
		return AuthenticationOrInventory(request, 17);
	});
	var steam = new SteamIntegration();
	Require(client.ConfirmedPets == null, "A new client cannot invent an initial Steam total.");
	await client.AuthenticateAsync(steam, CancellationToken.None);
	await client.AuthenticateAsync(steam, CancellationToken.None);
	Require(client.IsAuthenticated && steam.TicketRequests == 1 && inventoryCalls == 1 && client.ConfirmedPets == 17,
		"A valid session must authenticate once and immediately read its authoritative inventory.");
}

static async Task VerifyQueuedClicksAsync()
{
	var clickIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
	var grantIds = new List<string>();
	var grantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	var firstGrantReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
	using var client = CreateClient(async request =>
	{
		if (Path(request) != "/v1/pets/grant") return AuthenticationOrInventory(request, 20);
		Require(request.Headers.Authorization?.Parameter == "fixture-session", "A queued click must use the Worker session.");
		grantIds.Add(await EventId(request));
		if (grantIds.Count == 1)
		{
			grantStarted.SetResult();
			return await firstGrantReply.Task;
		}
		return Json(HttpStatusCode.OK, $$"""{"pets":{{20 + grantIds.Count}},"granted":1,"replayed":false,"items":[]}""");
	});
	foreach (var clickId in clickIds) client.EnqueuePetGrant(clickId);
	client.Tick(10, CancellationToken.None);
	Require(grantIds.Count == 0 && client.PendingGrantCount == 3 && client.ConfirmedPets == null, "Offline clicks must remain in memory without a grant request or an invented total.");
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	client.Tick(0, CancellationToken.None);
	await grantStarted.Task;
	client.Tick(10, CancellationToken.None);
	Require(grantIds.Count == 1 && client.PendingGrantCount == 3 && client.ConfirmedPets == 20, "A click in flight must remain pending and cannot increment the confirmed total.");
	firstGrantReply.SetResult(Json(HttpStatusCode.OK, """{"pets":21,"granted":1,"replayed":false,"items":[]}"""));
	await WaitForIdle(client);
	Require(client.PendingGrantCount == 2 && client.ConfirmedPets == 21, "One confirmed response removes exactly one click.");
	for (var index = 1; index < clickIds.Length; index++)
	{
		client.Tick(0, CancellationToken.None);
		await WaitForIdle(client);
	}
	Require(grantIds.SequenceEqual(clickIds.Select(id => id.ToString())) && client.PendingGrantCount == 0 && client.ConfirmedPets == 23,
		"Each click must produce one FIFO grant with its original GUID.");
}

static async Task VerifyGrantRetriesAsync()
{
	var grantIds = new List<string>();
	using var client = CreateClient(async request =>
	{
		if (Path(request) != "/v1/pets/grant") return AuthenticationOrInventory(request, 31);
		grantIds.Add(await EventId(request));
		return grantIds.Count switch
		{
			1 => throw new HttpRequestException("Connection dropped after Steam may have granted the Pet."),
			2 => Json(HttpStatusCode.TooManyRequests, """{"code":"rate_limited","error":"Retry shortly."}"""),
			3 => Json(HttpStatusCode.OK, """{"granted":1,"replayed":true,"items":[]}"""),
			_ => Json(HttpStatusCode.OK, """{"pets":9000000000,"granted":1,"replayed":true,"items":[]}"""),
		};
	});
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	var clickId = Guid.NewGuid();
	client.EnqueuePetGrant(clickId);
	for (var attempt = 1; attempt <= 3; attempt++)
	{
		client.Tick(attempt == 1 ? 0 : 3, CancellationToken.None);
		await WaitForIdle(client);
		Require(client.PendingGrantCount == 1 && client.ConfirmedPets == 31 && client.IsAuthenticated, "Transport failure, rate limits, and missing totals retain the click and previous confirmed Steam snapshot.");
		client.Tick(0, CancellationToken.None);
		Require(grantIds.Count == attempt, "Grant retries must respect their delay.");
	}
	client.Tick(3, CancellationToken.None);
	await WaitForIdle(client);
	Require(grantIds.Count == 4 && grantIds.All(id => id == clickId.ToString()), "All grant attempts must reuse the same GUID, including a replay after an uncertain response.");
	Require(client.PendingGrantCount == 0 && client.ConfirmedPets == 9000000000, "A replay uses the server's full 64-bit total rather than incrementing a local balance.");
}

static async Task VerifyAuthoritativeRefreshAsync()
{
	var inventoryCalls = 0;
	using var client = CreateClient(request => Task.FromResult(Path(request) == "/v1/inventory"
		? Json(HttpStatusCode.OK, ++inventoryCalls switch
		{
			1 => """{"pets":100,"items":[{"itemId":"pets","itemDefId":100,"quantity":100}]}""",
			2 => """{"pets":12,"items":[{"itemId":"pets","itemDefId":100,"quantity":12}]}""",
			3 => """{"pets":0,"items":[]}""",
			4 => """{"pets":999}""",
			_ => """{"pets":-1,"items":[]}""",
		})
		: AuthenticationOrInventory(request, 100)));
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	Require(await client.RefreshInventoryAsync(CancellationToken.None) && client.ConfirmedPets == 12 && client.InventoryItems[0].Quantity == 12,
		"A new Steam inventory response must replace both total and stacks even when the total falls.");
	Require(await client.RefreshInventoryAsync(CancellationToken.None) && client.ConfirmedPets == 0 && client.InventoryItems.Count == 0,
		"An empty Steam inventory is a valid confirmed zero.");
	var confirmedInventory = client.InventoryItems;
	for (var attempt = 0; attempt < 2; attempt++)
		Require(!await client.RefreshInventoryAsync(CancellationToken.None) && client.ConfirmedPets == 0 && ReferenceEquals(client.InventoryItems, confirmedInventory),
			"A missing inventory or negative total must not replace the last confirmed snapshot.");
}

static async Task VerifyAccountChangeAsync()
{
	var authCalls = 0;
	var grantCalls = 0;
	using var client = CreateClient(request => Task.FromResult(Path(request) switch
	{
		"/v1/auth/steam" => Json(HttpStatusCode.OK, ++authCalls == 1
			? """{"sessionToken":"first-session","steamId":"76561198000000000"}"""
			: """{"sessionToken":"second-session","steamId":"76561198000000001"}"""),
		"/v1/pets/grant" => ++grantCalls == 1
			? Json(HttpStatusCode.Unauthorized, """{"code":"unauthorized","error":"Steam account changed."}""")
			: throw new InvalidOperationException("The old account's click must not be granted to the new Steam account."),
		_ => AuthenticationOrInventory(request, authCalls == 1 ? 71 : 4),
	}));
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	client.EnqueuePetGrant(Guid.NewGuid());
	client.Tick(0, CancellationToken.None);
	await WaitForIdle(client);
	Require(!client.IsAuthenticated && client.PendingGrantCount == 1, "Session expiry retains a click until the Steam identity is known again.");
	await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
	client.Tick(3, CancellationToken.None);
	await WaitForIdle(client);
	Require(client.IsAuthenticated && client.PendingGrantCount == 0 && client.ConfirmedPets == 4 && grantCalls == 1,
		"A different Steam identity clears the previous account's queue and reads its own inventory.");
}

static async Task VerifySafeSteamDiagnosticsAsync()
{
	var bodies = new[]
	{
		"""{"error":"Steam request failed.","code":"service_unavailable","steamEndpoint":"ISteamUserAuth/AuthenticateUserTicket","steamHttpStatus":403}""",
		"""{"error":"Steam request failed.","code":"service_unavailable","steamEndpoint":"https://unsafe.invalid/?ticket=unsafe-ticket-placeholder","steamHttpStatus":403}""",
		"""{"error":"Steam request failed.","code":"service_unavailable","steamEndpoint":"ISteamUserAuth/AuthenticateUserTicket","steamHttpStatus":0}""",
	};
	for (var index = 0; index < bodies.Length; index++)
	{
		var body = bodies[index];
		using var client = CreateClient(_ => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, body)));
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Require(!client.IsAuthenticated && client.Status.Contains("Steam request failed.", StringComparison.Ordinal), "A Steam transport error must fail authentication with its safe summary.");
		if (index == 0)
			Require(client.Status.Contains("ISteamUserAuth/AuthenticateUserTicket: HTTP 403", StringComparison.Ordinal), "A recognized endpoint and HTTP status help diagnose publisher key permission errors.");
		else
			Require(!client.Status.Contains("unsafe", StringComparison.Ordinal) && !client.Status.Contains("HTTP", StringComparison.Ordinal) &&
				!client.Status.Contains("AuthenticateUserTicket", StringComparison.Ordinal), "Arbitrary endpoint text and invalid HTTP codes must not be echoed into client status.");
	}
	var categories = new[]
	{
		"item_properties_error", "item_definition_error", "inventory_disabled", "permission_denied", "invalid_parameters", "steam_rejected",
		"missing_response", "invalid_success_flag", "missing_item_json", "invalid_item_json", "invalid_item_data", "invalid_response_json",
		"invalid_response_document", "request_timeout", "connection_failed", "missing_pets_receipt", "invalid_replayed_flag",
	};
	foreach (var category in categories)
	{
		var body = JsonSerializer.Serialize(new
		{
			error = "Steam request failed.", code = "steam_unavailable", steamEndpoint = "IInventoryService/AddItem",
			steamFailureCategory = category, steamResult = 2, rawSteamResponse = "unsafe-raw-response",
			details = new { publisherKey = "unsafe-publisher-key", ticket = "unsafe-ticket", sessionToken = "unsafe-session" },
		});
		var status = await ReadGrantFailureStatusAsync(body);
		Require(status.Contains($"IInventoryService/AddItem: {category}, Steam result 2", StringComparison.Ordinal), "Each recognized Worker category and numeric Steam result must be visible for a failed grant.");
		Require(!status.Contains("HTTP", StringComparison.Ordinal), "A rejected HTTP 200 Steam payload must not invent a Steam HTTP error code.");
	}
	foreach (var (rawResult, expectedResult) in new (string, int?)[]
	{
		("0", 0), ("65535", 65535), ("-1", null), ("65536", null), ("2147483648", null), ("1.5", null),
		("\"2\"", null), ("\"unsafe-result\"", null), ("null", null), ("true", null), ("[2]", null), ("{\"ticket\":\"unsafe-ticket\"}", null),
	})
	{
		var body = $$"""{"error":"Steam request failed.","code":"steam_unavailable","steamEndpoint":"IInventoryService/AddItem","steamFailureCategory":"steam_rejected","steamResult":{{rawResult}}}""";
		var status = await ReadGrantFailureStatusAsync(body);
		Require(status.Contains("IInventoryService/AddItem: steam_rejected", StringComparison.Ordinal), "Malformed optional result values must preserve the safe category and main error.");
		Require(expectedResult.HasValue
			? status.Contains($"Steam result {expectedResult.Value}", StringComparison.Ordinal)
			: !status.Contains("Steam result", StringComparison.Ordinal), "Only numeric integer Steam results from 0 through 65535 may be shown.");
	}
	foreach (var rawCategory in new[] { "\"https://unsafe.invalid/?ticket=unsafe-ticket\"", "\"STEAM_REJECTED\"", "\"unknown_category\"", "null", "3", "[]", "{\"ticket\":\"unsafe-ticket\"}" })
	{
		var body = $$"""{"error":"Steam request failed.","code":"steam_unavailable","steamEndpoint":"IInventoryService/AddItem","steamFailureCategory":{{rawCategory}},"steamResult":2}""";
		var status = await ReadGrantFailureStatusAsync(body);
		Require(!status.Contains("AddItem", StringComparison.Ordinal) && !status.Contains("Steam result", StringComparison.Ordinal), "Unknown or nonstring categories must not display a result or arbitrary diagnostic text.");
	}
	var unsafeEndpointStatus = await ReadGrantFailureStatusAsync("""{"error":"Steam request failed.","code":"steam_unavailable","steamEndpoint":"https://unsafe.invalid/?key=unsafe-key","steamFailureCategory":"steam_rejected","steamResult":2}""");
	Require(!unsafeEndpointStatus.Contains("steam_rejected", StringComparison.Ordinal) && !unsafeEndpointStatus.Contains("Steam result", StringComparison.Ordinal), "A safe category and result still require a recognized Steam endpoint.");
	var legacyStatus = await ReadGrantFailureStatusAsync("""{"error":"Steam request failed.","code":"steam_unavailable","details":{"steamFailureCategory":"steam_rejected","steamResult":2,"ticket":"unsafe-ticket"}}""");
	Require(!legacyStatus.Contains("steam_rejected", StringComparison.Ordinal) && !legacyStatus.Contains("Steam result", StringComparison.Ordinal), "Older responses and arbitrary nested details must remain compatible and must not become diagnostics.");

	static async Task<string> ReadGrantFailureStatusAsync(string body)
	{
		using var client = CreateClient(request => Task.FromResult(Path(request) == "/v1/pets/grant"
			? Json(HttpStatusCode.ServiceUnavailable, body) : AuthenticationOrInventory(request, 17)));
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		client.EnqueuePetGrant(Guid.NewGuid());
		client.Tick(0, CancellationToken.None);
		await WaitForIdle(client);
		Require(client.IsAuthenticated && client.ConfirmedPets == 17 && client.PendingGrantCount == 1, "Grant diagnostics must preserve the session, confirmed total and pending retry.");
		Require(client.Status.Contains("Steam request failed.", StringComparison.Ordinal) && !client.Status.Contains("unsafe", StringComparison.Ordinal), "Only the safe primary error and recognized diagnostic fields may be displayed.");
		return client.Status;
	}
}

static BackendPetClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) =>
	new(new HttpClient(new FixtureHandler(handle)) { BaseAddress = new Uri("https://worker.test/") });

static HttpResponseMessage AuthenticationOrInventory(HttpRequestMessage request, long pets) => Path(request) switch
{
	"/v1/auth/steam" => Json(HttpStatusCode.OK, """{"sessionToken":"fixture-session","steamId":"76561198000000000"}"""),
	"/v1/inventory" => Json(HttpStatusCode.OK, $$"""{"pets":{{pets}},"items":[]}"""),
	_ => throw new InvalidOperationException($"Unexpected fixture request: {Path(request)}"),
};

static string Path(HttpRequestMessage request) => request.RequestUri!.AbsolutePath;

static async Task<string> EventId(HttpRequestMessage request)
{
	using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
	return document.RootElement.GetProperty("clientEventId").GetString()!;
}

static HttpResponseMessage Json(HttpStatusCode status, string body) =>
	new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

static async Task WaitForIdle(BackendPetClient client)
{
	using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
	while (client.IsOperationInFlight) await Task.Delay(5, timeout.Token);
}

static void Require(bool condition, string message)
{
	if (!condition) throw new InvalidOperationException(message);
}

sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
}
