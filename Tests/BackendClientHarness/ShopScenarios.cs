using System.Net;
using System.Text;
using System.Text.Json;

internal static class ShopScenarios
{
	private static readonly DateTimeOffset ServerNow = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
	public static async Task RunAsync()
	{
		await StableForegroundQueue();
		await UnknownExchangePollsWithoutRepost();
		await NeverAcceptedRequestRetainsId();
		await ReceiptRecoveryAndAcknowledgment();
		await RecyclingProtectsCopies();
		await PendingPayloadNeverCredits();
		await CooldownUsesServerTime();
		await DisabledOffersNeverSend();
		await DefiniteRejectionsDoNotLockEarning();
		await AnotherDeviceOperationIsRecovered();
		await UnrelatedReceiptCannotConfirmSpend();
#if DEBUG
		await DeveloperBootstrapIsIsolated();
#endif
	}

	private static async Task StableForegroundQueue()
	{
		var grantStarted = new TaskCompletionSource();
		var grantReply = new TaskCompletionSource<HttpResponseMessage>();
		var calls = 0;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v1/pets/grant") { grantStarted.SetResult(); return await grantReply.Task; }
			if (Path(request) == "/v2/shop/purchase") { calls++; return Snapshot(status: "complete", id: await EventId(request), bones: 50); }
			return Default(request);
		});
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		var stableStatus = client.ShopStatus;
		client.EnqueuePetGrant(Guid.NewGuid()); client.Tick(0, CancellationToken.None); await grantStarted.Task;
		Check(!client.IsShopOperationInFlight && client.ShopStatus == stableStatus, "An input grant must leave Shop buttons and status stable.");
		var purchase = client.PurchaseOfferAsync("dog", CancellationToken.None);
		Check(client.IsShopOperationInFlight && calls == 0, "A purchase marks foreground busy before waiting for a grant.");
		Check(!await client.PurchaseOfferAsync("dog", CancellationToken.None), "A second click cannot queue another spend.");
		client.Tick(10, CancellationToken.None);
		grantReply.SetResult(Json(HttpStatusCode.OK, new { pets = 501, granted = 1, replayed = false }));
		Check(await purchase && calls == 1 && client.ConfirmedGoldenBones == 50 && client.ConfirmedPets == 500, "The purchase follows the grant in authoritative snapshot order.");
		Pass("foreground Shop transactions queue once without flickering during input grants");
	}

	private static async Task UnknownExchangePollsWithoutRepost()
	{
		Guid operationId = default;
		var posts = 0; var polls = 0;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v2/shop/purchase") { posts++; operationId = await EventId(request); throw new HttpRequestException("Lost receipt."); }
			if (Path(request).StartsWith("/v2/shop/operations/")) { polls++; return Snapshot(status: "complete", id: operationId, bones: 50); }
			return Default(request);
		});
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Check(!await client.PurchaseOfferAsync("dog", CancellationToken.None) && client.HasPendingShopOperation, "Lost responses keep the operation pending.");
		client.Tick(3, CancellationToken.None); await Idle(client);
		Check(posts == 1 && polls == 1 && !client.HasPendingShopOperation && client.ConfirmedGoldenBones == 50 && client.UnacknowledgedReceipts.Single().ClientEventId == operationId,
			"Recovery queries the receipt, never repeats an unknown exchange.");
		Pass("uncertain Shop operations recover by status without reposting an exchange");
	}

	private static async Task NeverAcceptedRequestRetainsId()
	{
		var ids = new List<Guid>();
		using var client = Client(async request =>
		{
			if (Path(request) == "/v2/shop/purchase") { ids.Add(await EventId(request)); if (ids.Count == 1) throw new HttpRequestException("Never delivered."); return Snapshot(status: "complete", id: ids.Last()); }
			if (Path(request).StartsWith("/v2/shop/operations/")) return Json(HttpStatusCode.OK, new { status = "not_found" });
			return Default(request);
		});
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		await client.PurchaseOfferAsync("dog", CancellationToken.None);
		client.Tick(3, CancellationToken.None); await Idle(client);
		Check(ids.Count == 2 && ids[0] == ids[1] && !client.HasPendingShopOperation, "Only server-confirmed not_found permits resending the identical operation.");
		Pass("a never-accepted operation retries its original GUID after a not_found lookup");
	}

	private static async Task ReceiptRecoveryAndAcknowledgment()
	{
		var id = Guid.NewGuid(); var acknowledged = false; var ackCalls = 0;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v2/shop") return Snapshot(id: acknowledged ? null : id);
			if (Path(request) == "/v2/shop/ack") { Check(await EventId(request) == id, "Acknowledgment must identify the receipt, never create a new purchase."); if (++ackCalls == 1) throw new HttpRequestException("Offline."); acknowledged = true; return Snapshot(); }
			return Default(request);
		});
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Check(client.UnacknowledgedReceipts.Single().ClientEventId == id, "Startup must recover unacknowledged rewards even with the menu closed.");
		Check(!await client.AcknowledgeReceiptAsync(id, CancellationToken.None) && client.UnacknowledgedReceipts.Count == 1, "A failed acknowledgment retains the durable receipt.");
		client.Tick(6, CancellationToken.None); await Idle(client);
		Check(client.UnacknowledgedReceipts.Count == 0 && ackCalls == 2, "Tick retries the same acknowledgment and clears only the confirmed receipt.");
		Pass("startup recovers durable receipts and acknowledgment can retry safely");
	}

	private static async Task RecyclingProtectsCopies()
	{
		var recycleCalls = 0;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v2/shop") return Snapshot(copies: 3);
			if (Path(request) == "/v2/shop/recycle") { recycleCalls++; return Snapshot(copies: 2, bones: 105, status: "complete", id: await EventId(request)); }
			return Default(request);
		});
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Check(!await client.RecycleAsync(2004, 1, 3, CancellationToken.None), "Copies currently worn cannot be recycled.");
		Check(!await client.RecycleAsync(2004, 2, 0, CancellationToken.None), "Only the advertised atomic recycle quantity may be requested.");
		Check(!await client.RecycleAsync(9999, 1, 0, CancellationToken.None), "Local starter accessories cannot enter the Steam recycling workflow.");
		Check(await client.RecycleAsync(2004, 1, 2, CancellationToken.None) && recycleCalls == 1 && client.ConfirmedGoldenBones == 105, "One spare copy can be recycled without removing worn copies.");
		Check(!await client.RecycleAsync(2004, 1, 2, CancellationToken.None), "Updated confirmed quantities immediately protect the remaining equipped copies.");
		Pass("recycling protects equipped copies, the last copy, and non-Steam items");
	}

	private static async Task PendingPayloadNeverCredits()
	{
		using var client = Client(request => Task.FromResult(Path(request) == "/v2/shop/purchase"
			? Json(HttpStatusCode.Accepted, new { status = "pending", pets = 99999, goldenBones = 99999, receivedItems = new[] { new SteamInventoryItem("fake", 3000, 1) } })
			: Default(request)));
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Check(!await client.PurchaseOfferAsync("dog", CancellationToken.None) && client.ConfirmedPets == 500 && client.ConfirmedGoldenBones == 100 && client.ReceivedItems.Count == 0,
			"A pending response cannot credit currency or reveal unconfirmed rewards.");
		Check(!await client.OpenBoxAsync("legacy-box", CancellationToken.None), "An unknown Shop exchange also blocks legacy openings.");
		Pass("pending payloads cannot invent balances or rewards");
	}

	private static async Task CooldownUsesServerTime()
	{
		var purchases = 0;
		using var client = Client(request => { if (Path(request) == "/v2/shop/purchase") purchases++; return Task.FromResult(Path(request) == "/v2/shop" ? Snapshot(nextDigAt: ServerNow.AddMinutes(20)) : Default(request)); });
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Check(client.DigCooldownRemaining.TotalMinutes > 19.9 && client.DigCooldownRemaining.TotalMinutes <= 20, "The timer uses server time rather than the PC date.");
		Check(!await client.PurchaseOfferAsync("dig", CancellationToken.None) && purchases == 0, "The client honors the confirmed cooldown before sending a purchase.");
		Pass("cooldowns use server-anchored monotonic elapsed time");
	}

	private static async Task DisabledOffersNeverSend()
	{
		var purchases = 0;
		using var client = Client(request => { if (Path(request) == "/v2/shop/purchase") purchases++; return Task.FromResult(Path(request) == "/v2/shop" ? Snapshot(enabled: false) : Default(request)); });
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Check(!await client.PurchaseOfferAsync("dog", CancellationToken.None) && purchases == 0 && !client.ShopCapabilities.CashPurchasesEnabled && !client.ShopCapabilities.MarketEnabled,
			"Unreleased Shop, cash, and Market capabilities fail closed.");
		Pass("release capability gates prevent unavailable purchases");
	}

	private static async Task DefiniteRejectionsDoNotLockEarning()
	{
		foreach (var (status, code) in new[]
		{
			(HttpStatusCode.Conflict, "insufficient_items"), (HttpStatusCode.Conflict, "dig_cooldown"),
			(HttpStatusCode.Conflict, "no_spare_copy"), (HttpStatusCode.Conflict, "update_required"),
			(HttpStatusCode.ServiceUnavailable, "shop_unavailable"), (HttpStatusCode.ServiceUnavailable, "grant_pending"),
		})
		{
			var grants = 0;
			using var client = Client(request =>
			{
				if (Path(request) == "/v2/shop/purchase") return Task.FromResult(Json(status, new { code, error = "Fixture rejected before acceptance." }));
				if (Path(request) == "/v1/pets/grant") { grants++; return Task.FromResult(Json(HttpStatusCode.OK, new { pets = 501, granted = 1, replayed = false })); }
				return Task.FromResult(Default(request));
			});
			await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
			Check(!await client.PurchaseOfferAsync("dog", CancellationToken.None) && !client.HasPendingShopOperation, $"{code} must release an unaccepted purchase.");
			client.EnqueuePetGrant(Guid.NewGuid()); client.Tick(3, CancellationToken.None); await Idle(client);
			Check(grants == 1 && client.PendingGrantCount == 0, $"{code} must not starve subsequent click grants.");
		}
		Pass("definite Worker rejection codes release unaccepted operations and preserve earning");
	}

	private static async Task AnotherDeviceOperationIsRecovered()
	{
		var externalId = Guid.NewGuid(); var posts = 0; var polledIds = new List<string>();
		using var client = Client(request =>
		{
			if (Path(request) == "/v2/shop/purchase")
			{
				posts++; return Task.FromResult(Json(HttpStatusCode.Conflict, new { code = "exchange_pending", error = "Another exchange is pending.", pendingOperation = new { clientEventId = externalId, kind = "purchase", offerId = "dog" } }));
			}
			if (Path(request).StartsWith("/v2/shop/operations/")) { polledIds.Add(Path(request).Split('/').Last()); return Task.FromResult(Snapshot(status: "complete", id: externalId)); }
			return Task.FromResult(Default(request));
		});
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Check(!await client.PurchaseOfferAsync("dog", CancellationToken.None) && client.HasPendingShopOperation, "A concurrent transaction switches recovery to the server's accepted ID.");
		client.Tick(3, CancellationToken.None); await Idle(client);
		Check(posts == 1 && polledIds.SequenceEqual(new[] { externalId.ToString("D") }) && !client.HasPendingShopOperation, "The unaccepted local transaction must not be retried after recovering the external operation.");
		Pass("a transaction accepted on another device replaces the unaccepted local request");
	}

	private static async Task UnrelatedReceiptCannotConfirmSpend()
	{
		using var client = Client(request => Task.FromResult(Path(request) == "/v2/shop/purchase" ? Snapshot(status: "complete", id: Guid.NewGuid(), bones: 99999) : Default(request)));
		await client.AuthenticateAsync(new SteamIntegration(), CancellationToken.None);
		Check(!await client.PurchaseOfferAsync("dog", CancellationToken.None) && client.HasPendingShopOperation && client.ConfirmedGoldenBones == 100 && client.ReceivedItems.Count == 0,
			"A different operation's receipt cannot confirm or reveal the current spend.");
		Pass("receipt identity is checked before applying a purchase response");
	}

#if DEBUG
	private static async Task DeveloperBootstrapIsIsolated()
	{
		var previousDev = Environment.GetEnvironmentVariable("PDD_DEV_ECONOMY");
		var previousSteam = Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM");
		try
		{
			Environment.SetEnvironmentVariable("PDD_DEV_ECONOMY", "1");
			Environment.SetEnvironmentVariable("PDD_DISABLE_STEAM", "1");
			using var remote = Client(_ => throw new InvalidOperationException("Developer auth must never contact a remote backend."));
			Check(!await remote.AuthenticateTestEconomyAsync(CancellationToken.None), "A dev environment variable cannot authorize remote fake authentication.");
			var realTicketRequests = 0;
			using var local = new BackendPetClient(new HttpClient(new FixtureHandler(async request =>
			{
				if (Path(request) == "/v1/auth/steam")
				{
					using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
					Check(body.RootElement.GetProperty("ticketHex").GetString() == "aabb", "Local test authentication uses a fixed fake ticket.");
					realTicketRequests++;
					return Default(request);
				}
				return Snapshot(developer: true);
			})) { BaseAddress = new Uri("http://127.0.0.1:8787/") });
			Check(await local.AuthenticateTestEconomyAsync(CancellationToken.None) && local.ShopCapabilities.DeveloperControls && realTicketRequests == 1,
				"The isolated loopback Worker can authenticate without native Steam.");
			using var ordinary = new BackendPetClient(new HttpClient(new FixtureHandler(request => Task.FromResult(Default(request)))) { BaseAddress = new Uri("http://127.0.0.1:8787/") });
			Check(!await ordinary.AuthenticateTestEconomyAsync(CancellationToken.None) && !ordinary.IsAuthenticated && ordinary.ConfirmedPets == null,
				"A loopback server that lacks the isolated-test capability must be rejected and cleared.");
		}
		finally { Environment.SetEnvironmentVariable("PDD_DEV_ECONOMY", previousDev); Environment.SetEnvironmentVariable("PDD_DISABLE_STEAM", previousSteam); }
		Pass("developer authentication is restricted to an explicitly isolated loopback economy");
	}
#endif

	private static HttpResponseMessage Snapshot(string? status = null, Guid? id = null, long bones = 100, long copies = 1, DateTimeOffset? nextDigAt = null, bool enabled = true, bool developer = false) => Json(HttpStatusCode.OK, new
	{
		pets = 500, goldenBones = bones,
		items = new[] { new SteamInventoryItem("pets", 100, 500), new SteamInventoryItem("bones", 101, bones), new SteamInventoryItem("bow", 2004, copies) },
		economyVersion = "golden-bones-v2", serverTime = ServerNow, nextDigAt,
		catalog = new { offers = new[]
		{
			new ShopOffer { Id = "dig", Kind = "box", CurrencyItemDefId = 100, Price = 500, RewardItemDefIds = new[] { 3000 }, CooldownSeconds = 1200 },
			new ShopOffer { Id = "dog", Kind = "box", CurrencyItemDefId = 101, Price = 50, RewardItemDefIds = new[] { 3001 } },
			new ShopOffer { Id = "accessory:2004", Kind = "accessory", CurrencyItemDefId = 101, Price = 100, RewardItemDefIds = new[] { 2004 } },
		}, packs = Array.Empty<ShopCurrencyPack>(), recycleValue = 5, maxRecycleQuantity = 1 },
		capabilities = new { shopEnabled = enabled, cashPurchasesEnabled = false, marketEnabled = false, developerControls = developer },
		receipts = id.HasValue ? new[] { new ShopReceipt { ClientEventId = id.Value, Kind = "purchase", OfferId = "dog", ReceivedItems = new[] { new SteamInventoryItem("dog", 3001, 1) }, CompletedAt = ServerNow } } : Array.Empty<ShopReceipt>(),
		status, operationId = id, receivedItems = id.HasValue ? new[] { new SteamInventoryItem("dog", 3001, 1) } : Array.Empty<SteamInventoryItem>(),
	});
	private static BackendPetClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) => new(new HttpClient(new FixtureHandler(handle)) { BaseAddress = new Uri("https://fixture.test/") });
	private static HttpResponseMessage Default(HttpRequestMessage request) => Path(request) switch
	{
		"/v1/auth/steam" => Json(HttpStatusCode.OK, new { sessionToken = "fixture", steamId = "76561198000000000" }),
		"/v1/inventory" => Json(HttpStatusCode.OK, new { pets = 500, items = Array.Empty<SteamInventoryItem>() }),
		"/v2/shop" => Snapshot(),
		_ => throw new InvalidOperationException($"Unexpected test route {Path(request)}"),
	};
	private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
	private static string Path(HttpRequestMessage request) => request.RequestUri!.AbsolutePath;
	private static async Task<Guid> EventId(HttpRequestMessage request) => JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.GetProperty("clientEventId").GetGuid();
	private static async Task Idle(BackendPetClient client) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (client.IsOperationInFlight) await Task.Delay(5, timeout.Token); }
	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
	private static void Pass(string message) => Console.WriteLine($"PASS: {message}");
}
