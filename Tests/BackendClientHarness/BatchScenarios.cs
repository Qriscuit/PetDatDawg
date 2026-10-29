using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal sealed record BatchRequest(Guid ClientEventId, Guid RunId, long FirstSequence, int Count)
{
	public static async Task<BatchRequest> ReadAsync(HttpRequestMessage request)
	{
		using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
		var body = document.RootElement;
		var result = new BatchRequest(body.GetProperty("clientEventId").GetGuid(), body.GetProperty("runId").GetGuid(),
			body.GetProperty("firstSequence").GetInt64(), body.GetProperty("count").GetInt32());
		if (result.ClientEventId == Guid.Empty || result.RunId == Guid.Empty || result.FirstSequence < 1 || result.Count is < 1 or > 300)
			throw new InvalidOperationException("Batch identity, sequence and bounded quantity are required.");
		return result;
	}
}

internal static class BatchScenarios
{
	private static readonly CancellationToken Token = CancellationToken.None;
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
	private static readonly DateTimeOffset ServerNow = DateTimeOffset.Parse("2030-01-01T12:00:00Z");

	public static async Task RunAsync()
	{
		await CadenceAndBoundedQuantities();
		await AdvertisedBatchLimit();
		await ImmutableRetryAndRetryAfter();
		await HttpDateRetryAfter();
		await InvalidGrantResponsesStayPending();
		await UnsupportedBatchEndpointDoesNotFallback();
		await RenewalKeepsBatchAndRun();
		await AccountSwitchStartsFreshRun();
		await RestartDoesNotResubmitOldInputs();
		await ShopSyncCapturesFixedPrefix();
		await BuyingSettlesBeforeSpending();
		await BuyingQueuesBehindBackgroundBatch();
		await FailedSettlementCannotStartPurchase();
		await CancelledSettlementCannotStartPurchase();
		await CancellationBeforePurchaseSendClearsReservation();
		await CancellationAfterPurchaseSendPreservesRecovery();
	}

	private static async Task CadenceAndBoundedQuantities()
	{
		var batches = new List<BatchRequest>();
		long pets = 0;
		using var client = Client(async request =>
		{
			if (Path(request) != "/v1/pets/grant-batch") return Default(request, pets);
			var batch = await BatchRequest.ReadAsync(request); batches.Add(batch); pets += batch.Count;
			return Grant(pets, batch.Count);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		client.Tick(3600, Token); await Idle(client);
		Check(batches.Count == 0, "An empty interval cannot create a grant request.");
		Enqueue(client, 305);
		client.Tick(29, Token); await Idle(client);
		Check(batches.Count == 0 && client.ConfirmedPets == 0 && client.PendingGrantCount == 305, "A new nonempty queue waits for its thirty-second cadence without optimistic credit.");
		client.Tick(1, Token); await Idle(client);
		Check(batches.Count == 1 && batches[0].Count == 300 && batches[0].FirstSequence == 1 && client.PendingGrantCount == 5,
			"A background request must be bounded to 300 inputs and dequeue exactly that prefix.");
		client.Tick(0, Token); await Idle(client);
		Check(batches.Count == 1, "A size-bound remainder must not produce an immediate background request burst.");
		client.Tick(30, Token); await Idle(client);
		Check(batches.Count == 2 && batches[1].Count == 5 && batches[1].FirstSequence == 301 && batches[1].RunId == batches[0].RunId &&
			batches[1].ClientEventId != batches[0].ClientEventId && client.PendingGrantCount == 0 && client.ConfirmedPets == 305,
			"The next batch must cover the remaining range once in the same run.");
		client.Tick(3600, Token); await Idle(client);
		Check(batches.Count == 2, "A drained queue must stop issuing background grants.");
		Pass("thirty-second nonempty cadence bounds batch size and retains contiguous accounting");
	}

	private static async Task AdvertisedBatchLimit()
	{
		var batches = new List<BatchRequest>();
		long pets = 0;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v1/auth/steam") return Json(HttpStatusCode.OK, new { sessionToken = "fixture", steamId = "76561198000000000", maxGrantBatchSize = 40 });
			if (Path(request) != "/v1/pets/grant-batch") return Default(request, pets);
			var batch = await BatchRequest.ReadAsync(request); batches.Add(batch); pets += batch.Count;
			return Grant(pets, batch.Count);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 45); client.Tick(30, Token); await Idle(client);
		Check(batches.Single().Count == 40 && client.PendingGrantCount == 5, "The authenticated server's smaller allowed batch size must be honored.");
		client.Tick(30, Token); await Idle(client);
		Check(batches[1].FirstSequence == 41 && batches[1].Count == 5 && client.PendingGrantCount == 0, "Server-size splitting must preserve exact input accounting.");
		Pass("server-advertised batch-size limits preserve contiguous exact counts");
	}

	private static async Task ImmutableRetryAndRetryAfter()
	{
		var attempts = new List<BatchRequest>();
		using var client = Client(async request =>
		{
			if (Path(request) != "/v1/pets/grant-batch") return Default(request, 100);
			var batch = await BatchRequest.ReadAsync(request); attempts.Add(batch);
			if (attempts.Count == 1) throw new HttpRequestException("The grant may have succeeded before its response was lost.");
			if (attempts.Count == 2)
			{
				var limited = Json(HttpStatusCode.TooManyRequests, new { code = "rate_limited", error = "Wait before retrying." });
				limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
				return limited;
			}
			return Grant(102, batch.Count, replayed: true);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 2); client.Tick(30, Token); await Idle(client);
		Enqueue(client, 3);
		Check(client.ConfirmedPets == 100 && client.PendingGrantCount == 5, "A lost batch response cannot remove inputs or change the confirmed total.");
		client.Tick(300, Token); await Idle(client);
		Check(attempts.Count == 2 && attempts[0] == attempts[1], "A retry must not absorb newly arriving inputs.");
		client.Tick(119, Token); await Idle(client);
		Check(attempts.Count == 2, "Retry-After is a minimum delay for the original frozen batch.");
		Check(!await client.SyncShopAsync(Token) && attempts.Count == 2, "Opening Shop cannot bypass a server Retry-After delay.");
		client.Tick(4, Token); await Idle(client);
		Check(attempts.Count == 3 && attempts.All(batch => batch == attempts[0]) && client.PendingGrantCount == 3 && client.ConfirmedPets == 102,
			"The confirmed replay removes only the original frozen prefix.");
		Pass("lost-response retries retain the frozen range while Retry-After also gates foreground synchronization");
	}

	private static async Task HttpDateRetryAfter()
	{
		var calls = 0;
		using var client = Client(request =>
		{
			if (Path(request) != "/v1/pets/grant-batch") return Task.FromResult(Default(request, 20));
			if (++calls > 1) return Task.FromResult(Grant(21, 1));
			var unavailable = Json(HttpStatusCode.ServiceUnavailable, new { code = "storage_quota_exceeded", error = "Storage quota exhausted." });
			unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(ServerNow.AddMinutes(10));
			return Task.FromResult(unavailable);
		}, new FixedTimeProvider(ServerNow));
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 1); client.Tick(30, Token); await Idle(client);
		client.Tick(599, Token); await Idle(client);
		Check(calls == 1 && client.PendingGrantCount == 1, "An HTTP-date Retry-After must use the injected current clock and retain the pending batch.");
		client.Tick(4, Token); await Idle(client);
		Check(calls == 2 && client.PendingGrantCount == 0 && client.ConfirmedPets == 21, "The same run may retry after the HTTP-date delay elapses.");
		Pass("HTTP-date quota retry delays use the injected clock without a real-time wait");
	}

	private static async Task InvalidGrantResponsesStayPending()
	{
		foreach (var invalidBody in new object[]
		{
			new { pets = 999, granted = 1, replayed = false, items = Array.Empty<SteamInventoryItem>() },
			new { pets = 999, granted = 3, replayed = false, items = Array.Empty<SteamInventoryItem>() },
			new { pets = 999, granted = 2, replayed = false },
			new { granted = 2, replayed = false, items = Array.Empty<SteamInventoryItem>() },
			new { pets = -1, granted = 2, replayed = false, items = Array.Empty<SteamInventoryItem>() },
			new { pets = 999, granted = 2, items = Array.Empty<SteamInventoryItem>() },
			new { pets = 999, granted = 2, replayed = "not-a-boolean", items = Array.Empty<SteamInventoryItem>() },
			new { pets = 999, granted = 2, replayed = false, items = new[] { new SteamInventoryItem("bad", 101, -1) } },
			new { pets = 999, granted = 2, replayed = false, items = new[] { new SteamInventoryItem("large", 101, long.MaxValue), new SteamInventoryItem("overflow", 101, 1) } },
		})
		{
			var batches = new List<BatchRequest>();
			using var client = Client(async request =>
			{
				if (Path(request) != "/v1/pets/grant-batch") return Default(request, 40);
				batches.Add(await BatchRequest.ReadAsync(request));
				return batches.Count == 1 ? Json(HttpStatusCode.OK, invalidBody) : Grant(42, 2, true);
			});
			await client.AuthenticateAsync(new SteamIntegration(), Token);
			var confirmedInventory = client.InventoryItems;
			Enqueue(client, 2); client.Tick(30, Token); await Idle(client);
			Check(client.ConfirmedPets == 40 && client.ConfirmedGoldenBones == 100 && client.PendingGrantCount == 2 && ReferenceEquals(confirmedInventory, client.InventoryItems),
				"Wrong quantities, malformed receipts and invalid inventory must leave the entire confirmed snapshot and pending batch unchanged.");
			client.Tick(300, Token); await Idle(client);
			Check(batches.Count == 2 && batches[0] == batches[1] && client.PendingGrantCount == 0 && client.ConfirmedPets == 42,
				"Invalid responses must retry the exact same batch, not mint a replacement ID.");
		}
		Pass("batch quantity and inventory validation fail closed before dequeue or balance changes");
	}

	private static async Task UnsupportedBatchEndpointDoesNotFallback()
	{
		var attempts = new List<BatchRequest>(); var oldEndpointCalls = 0;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v1/pets/grant") { oldEndpointCalls++; return Grant(999, 1); }
			if (Path(request) != "/v1/pets/grant-batch") return Default(request, 10);
			attempts.Add(await BatchRequest.ReadAsync(request));
			return Json(HttpStatusCode.NotFound, new { code = "not_found", error = "This server has not enabled batched earning." });
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 2); client.Tick(30, Token); await Idle(client);
		client.Tick(300, Token); await Idle(client);
		Check(attempts.Count == 2 && attempts[0] == attempts[1] && oldEndpointCalls == 0 && client.PendingGrantCount == 2 && client.ConfirmedPets == 10,
			"An unavailable batch endpoint cannot split an uncertain batch into fresh legacy grant IDs or discard its inputs.");
		Pass("unsupported batch endpoints retain immutable pending work without legacy fallback");
	}

	private static async Task RenewalKeepsBatchAndRun()
	{
		var attempts = new List<BatchRequest>();
		using var client = Client(async request =>
		{
			if (Path(request) != "/v1/pets/grant-batch") return Default(request, 100);
			attempts.Add(await BatchRequest.ReadAsync(request));
			return attempts.Count == 1 ? Json(HttpStatusCode.Unauthorized, new { code = "session_expired", error = "Renew the session." }) : Grant(102, 2, true);
		});
		var steam = new SteamIntegration();
		await client.AuthenticateAsync(steam, Token);
		Enqueue(client, 2); client.Tick(30, Token); await Idle(client);
		Enqueue(client, 3);
		await client.AuthenticateAsync(steam, Token);
		client.Tick(300, Token); await Idle(client);
		Check(attempts.Count == 2 && attempts[0] == attempts[1] && steam.TicketRequests == 2 && client.PendingGrantCount == 3,
			"Same-account reauthentication preserves the in-flight run and batch while later input remains separate.");
		Pass("authentication renewal preserves the batch identity and excludes later inputs");
	}

	private static async Task AccountSwitchStartsFreshRun()
	{
		var authCalls = 0;
		var batches = new List<BatchRequest>();
		using var client = Client(async request =>
		{
			if (Path(request) == "/v1/auth/steam") return Json(HttpStatusCode.OK, new
			{
				sessionToken = "session-" + ++authCalls,
				steamId = authCalls == 1 ? "76561198000000000" : "76561198000000001",
			});
			if (Path(request) != "/v1/pets/grant-batch") return Default(request, 0);
			batches.Add(await BatchRequest.ReadAsync(request));
			return batches.Count == 1 ? Json(HttpStatusCode.Unauthorized, new { code = "session_expired", error = "Steam account changed." }) : Grant(1, 1);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 2); client.Tick(30, Token); await Idle(client);
		Enqueue(client, 3);
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Check(client.PendingGrantCount == 0, "Switching Steam identities discards both the frozen and later input queues.");
		Enqueue(client, 1); client.Tick(30, Token); await Idle(client);
		Check(batches.Count == 2 && batches[0].RunId != batches[1].RunId && batches[1].FirstSequence == 1 && batches[1].Count == 1,
			"The new Steam account must start its own run and sequence, with none of the old account's inputs.");
		Pass("Steam account changes clear pending work and start a new run sequence");
	}

	private static async Task RestartDoesNotResubmitOldInputs()
	{
		var batches = new List<BatchRequest>();
		async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
		{
			if (Path(request) != "/v1/pets/grant-batch") return Default(request, 0);
			batches.Add(await BatchRequest.ReadAsync(request));
			if (batches.Count == 1) throw new HttpRequestException("An old run ended while confirmation was unavailable.");
			return Grant(1, 1);
		}
		using (var oldRun = Client(Handle))
		{
			await oldRun.AuthenticateAsync(new SteamIntegration(), Token);
			Enqueue(oldRun, 3); oldRun.Tick(30, Token); await Idle(oldRun);
			Check(oldRun.PendingGrantCount == 3, "The old run retains its uncertain inputs until it closes.");
		}
		using var newRun = Client(Handle);
		await newRun.AuthenticateAsync(new SteamIntegration(), Token);
		newRun.Tick(3600, Token); await Idle(newRun);
		Check(batches.Count == 1 && newRun.PendingGrantCount == 0, "A fresh client must not reconstruct or automatically retry the previous run's input queue.");
		Enqueue(newRun, 1); newRun.Tick(30, Token); await Idle(newRun);
		Check(batches.Count == 2 && batches[1].RunId != batches[0].RunId && batches[1].FirstSequence == 1 && batches[1].Count == 1,
			"A restarted client grants only newly observed input under a fresh run identity.");
		Pass("client restart preserves current-run-only pending input semantics");
	}

	private static async Task ShopSyncCapturesFixedPrefix()
	{
		var started = Signal(); var release = Signal();
		var batches = new List<BatchRequest>();
		var paths = new List<string>();
		long pets = 490;
		using var client = Client(async request =>
		{
			paths.Add(Path(request));
			if (Path(request) != "/v1/pets/grant-batch") return Default(request, pets);
			var batch = await BatchRequest.ReadAsync(request); batches.Add(batch);
			started.TrySetResult(); await release.Task;
			pets += batch.Count; return Grant(pets, batch.Count);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token); paths.Clear();
		Enqueue(client, 305);
		var syncing = client.SyncShopAsync(Token);
		await started.Task.WaitAsync(Timeout);
		Enqueue(client, 2);
		Check(!await client.SyncShopAsync(Token), "Repeated Shop opens must not create another concurrent foreground synchronization.");
		release.TrySetResult();
		Check(await syncing.WaitAsync(Timeout), "Opening Shop should settle its captured input prefix and refresh its state.");
		Check(batches.Count == 2 && batches[0].Count == 300 && batches[1].Count == 5 && batches[1].FirstSequence == 301 &&
			batches[0].RunId == batches[1].RunId && client.PendingGrantCount == 2 && client.ConfirmedPets == 795 &&
			paths.SequenceEqual(new[] { "/v1/pets/grant-batch", "/v1/pets/grant-batch", "/v2/shop" }),
			"Shop refresh follows the captured batch, leaving newly arriving input outside that foreground operation.");
		Pass("Shop opening flushes a fixed prefix before its inventory refresh");
	}

	private static async Task BuyingSettlesBeforeSpending()
	{
		var started = Signal(); var release = Signal();
		var paths = new List<string>();
		BatchRequest? batch = null; Guid purchaseId = default;
		long pets = 475;
		using var client = Client(async request =>
		{
			paths.Add(Path(request));
			if (Path(request) == "/v1/pets/grant-batch")
			{
				batch = await BatchRequest.ReadAsync(request); started.TrySetResult(); await release.Task;
				pets += batch.Count; return Grant(pets, batch.Count);
			}
			if (Path(request) == "/v2/shop/purchase")
			{
				Check(pets == 500, "The purchase must follow Steam-confirmed settlement, never a predicted balance.");
				purchaseId = await EventId(request); pets = 0;
				return Shop(pets, purchaseId);
			}
			return Default(request, pets);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token); paths.Clear();
		Enqueue(client, 25);
		var purchase = client.PurchaseOfferAsync("dig", Token);
		await started.Task.WaitAsync(Timeout);
		Check(client.ConfirmedPets == 475 && client.IsShopOperationInFlight, "Foreground settlement reserves the action without displaying the pending 25 as credit.");
		Check(!await client.PurchaseOfferAsync("dig", Token), "A repeated click must not create a second purchase while its grant prerequisite is running.");
		Enqueue(client, 1); release.TrySetResult();
		Check(await purchase.WaitAsync(Timeout), "A purchase below the stale displayed balance must succeed after settling the current prefix.");
		Check(batch?.Count == 25 && purchaseId != Guid.Empty && purchaseId != batch.ClientEventId && client.PendingGrantCount == 1 &&
			client.ConfirmedPets == 0 && paths.SequenceEqual(new[] { "/v1/pets/grant-batch", "/v2/shop/purchase" }),
			"The grant and purchase have separate IDs, ordered responses, and one purchase per click action.");
		Pass("buying settles eligible inputs first with separate identities and duplicate-click protection");
	}

	private static async Task BuyingQueuesBehindBackgroundBatch()
	{
		var started = Signal(); var release = Signal();
		var batches = new List<BatchRequest>(); var purchases = 0;
		long pets = 495;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v1/pets/grant-batch")
			{
				var batch = await BatchRequest.ReadAsync(request); batches.Add(batch);
				if (batches.Count == 1) { started.TrySetResult(); await release.Task; }
				pets += batch.Count; return Grant(pets, batch.Count);
			}
			if (Path(request) == "/v2/shop/purchase")
			{
				Check(pets == 500, "Purchase must wait for both the already-running batch and its remaining fixed prefix.");
				purchases++; pets = 0; return Shop(pets, await EventId(request));
			}
			return Default(request, pets);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 2); client.Tick(30, Token);
		await started.Task.WaitAsync(Timeout);
		Check(!client.IsShopOperationInFlight, "Background settlement must not make the Shop foreground-busy.");
		Enqueue(client, 3);
		var purchase = client.PurchaseOfferAsync("dig", Token);
		Enqueue(client, 4);
		Check(client.IsShopOperationInFlight && purchases == 0, "The accepted Shop action reserves itself while the background batch completes.");
		release.TrySetResult();
		Check(await purchase.WaitAsync(Timeout), "Foreground settlement must acquire the shared request gate without deadlocking behind the background batch.");
		Check(batches.Count == 2 && batches[0].Count == 2 && batches[1].Count == 3 && batches[1].FirstSequence == 3 &&
			batches[0].RunId == batches[1].RunId && purchases == 1 && client.PendingGrantCount == 4,
			"The foreground prefix includes earlier queued input only, then yields to later earning.");
		Pass("Shop requests queue behind a background batch and settle only their captured prefix without deadlock");
	}

	private static async Task FailedSettlementCannotStartPurchase()
	{
		var batches = new List<BatchRequest>(); var purchases = 0;
		long pets = 499;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v1/pets/grant-batch")
			{
				var batch = await BatchRequest.ReadAsync(request); batches.Add(batch);
				if (batches.Count == 1)
				{
					var unavailable = Json(HttpStatusCode.ServiceUnavailable, new { code = "storage_quota_exceeded", error = "Try after reset." });
					unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)); return unavailable;
				}
				pets += batch.Count; return Grant(pets, batch.Count);
			}
			if (Path(request) == "/v2/shop/purchase") { purchases++; pets = 0; return Shop(pets, await EventId(request)); }
			return Default(request, pets);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 1);
		Check(!await client.PurchaseOfferAsync("dig", Token) && purchases == 0 && client.PendingGrantCount == 1 && client.ConfirmedPets == 499 &&
			!client.IsShopOperationInFlight && !client.HasPendingShopOperation,
			"Failed prerequisite settlement must leave no accepted purchase or sticky foreground reservation.");
		Check(!await client.PurchaseOfferAsync("dig", Token) && batches.Count == 1 && purchases == 0,
			"Repeated Buy cannot bypass the settlement retry deadline.");
		client.Tick(122, Token); await Idle(client);
		Check(batches.Count == 2 && batches[0] == batches[1] && purchases == 0 && client.ConfirmedPets == 500,
			"Background recovery may confirm the grant, but must not unexpectedly perform the previously failed purchase.");
		Check(await client.PurchaseOfferAsync("dig", Token) && purchases == 1, "A new explicit Buy can proceed once the grant is confirmed.");
		Pass("failed prerequisite batches cannot create or later auto-execute a purchase");
	}

	private static async Task CancelledSettlementCannotStartPurchase()
	{
		using var cancellation = new CancellationTokenSource();
		var started = Signal(); var attempts = new List<BatchRequest>(); var purchases = 0;
		long pets = 498;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v1/pets/grant-batch")
			{
				var batch = await BatchRequest.ReadAsync(request); attempts.Add(batch);
				if (attempts.Count == 1)
				{
					started.TrySetResult();
					await Task.Delay(System.Threading.Timeout.Infinite, cancellation.Token);
				}
				pets += batch.Count; return Grant(pets, batch.Count, replayed: true);
			}
			if (Path(request) == "/v2/shop/purchase") { purchases++; return Shop(0, await EventId(request)); }
			return Default(request, pets);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 2);
		var purchase = client.PurchaseOfferAsync("dig", cancellation.Token);
		await started.Task.WaitAsync(Timeout);
		cancellation.Cancel();
		Check(!await purchase.WaitAsync(Timeout) && purchases == 0 && client.PendingGrantCount == 2 && client.ConfirmedPets == 498 &&
			!client.IsOperationInFlight && !client.HasPendingShopOperation,
			"Cancellation during prerequisite HTTP must release foreground state without dropping inputs or submitting a purchase.");
		Enqueue(client, 1); client.Tick(30, Token); await Idle(client);
		Check(attempts.Count == 2 && attempts[0] == attempts[1] && client.PendingGrantCount == 1 && client.ConfirmedPets == 500 && purchases == 0,
			"An uncertain cancellation retries the same frozen batch under a fresh token and never auto-executes the cancelled purchase.");
		Pass("cancelled foreground settlement preserves its uncertain batch and never submits a purchase");
	}

	private static async Task CancellationBeforePurchaseSendClearsReservation()
	{
		foreach (var legacy in new[] { false, true })
		{
		using var cancellation = new CancellationTokenSource();
		var grantStarted = Signal(); var grantRelease = Signal(); var readStarted = Signal(); var readRelease = Signal();
		var inventoryCalls = 0; var purchases = 0; var polls = 0;
		long pets = 498;
		using var client = Client(async request =>
		{
			if (Path(request) == "/v1/pets/grant-batch")
			{
				var batch = await BatchRequest.ReadAsync(request); grantStarted.TrySetResult(); await grantRelease.Task;
				pets += batch.Count; return Grant(pets, batch.Count);
			}
			if (Path(request) == "/v1/inventory" && ++inventoryCalls > 1)
			{
				readStarted.TrySetResult(); await readRelease.Task;
			}
			if (Path(request).StartsWith("/v2/shop/operations/", StringComparison.Ordinal))
			{
				polls++; return Json(HttpStatusCode.OK, new { status = "not_found" });
			}
			if (Path(request) is "/v2/shop/purchase" or "/v1/boxes/buy") { purchases++; return Shop(0, await EventId(request)); }
			return Default(request, pets);
		});
		await client.AuthenticateAsync(new SteamIntegration(), Token);
		Enqueue(client, 2);
		var purchase = legacy ? client.BuyBoxAsync("dog", cancellation.Token) : client.PurchaseOfferAsync("dig", cancellation.Token);
		await grantStarted.Task.WaitAsync(Timeout);
		// This read waits first, so it owns the request gate between the grant
		// prerequisite and the purchase POST. Cancel in that observable window.
		var read = client.RefreshInventoryAsync(Token);
		grantRelease.TrySetResult();
		await readStarted.Task.WaitAsync(Timeout);
		cancellation.Cancel();
		Check(!await purchase.WaitAsync(Timeout), "Cancellation before sending the purchase must not report success.");
		readRelease.TrySetResult();
		Check(await read.WaitAsync(Timeout), "The independent inventory refresh remains valid.");
		client.Tick(30, Token); await Idle(client);
		Check(purchases == 0 && polls == 0 && !client.HasPendingExchange && client.ConfirmedPets == 500,
			"A purchase cancelled before any POST must release its reservation, not later poll not_found and automatically spend.");
		}
		Pass("cancellation before a Shop or legacy purchase POST clears its unaccepted reservation");
	}

	private static async Task CancellationAfterPurchaseSendPreservesRecovery()
	{
		foreach (var legacy in new[] { false, true })
		{
			using var cancellation = new CancellationTokenSource();
			var sent = Signal(); var ids = new List<Guid>(); var polls = 0;
			using var client = Client(async request =>
			{
				if (Path(request) is "/v2/shop/purchase" or "/v1/boxes/buy")
				{
					ids.Add(await EventId(request));
					if (ids.Count == 1) { sent.TrySetResult(); await Task.Delay(System.Threading.Timeout.Infinite, cancellation.Token); }
					return Shop(0, ids[0]);
				}
				if (Path(request).StartsWith("/v2/shop/operations/", StringComparison.Ordinal))
				{
					polls++; return Shop(0, ids[0]);
				}
				return Default(request, 500);
			});
			await client.AuthenticateAsync(new SteamIntegration(), Token);
			var purchase = legacy ? client.BuyBoxAsync("dog", cancellation.Token) : client.PurchaseOfferAsync("dig", cancellation.Token);
			await sent.Task.WaitAsync(Timeout);
			cancellation.Cancel();
			Check(!await purchase.WaitAsync(Timeout) && client.HasPendingExchange && client.ConfirmedPets == 500,
				"Cancellation after POST may follow a real spend, so it must retain the operation and previous confirmed inventory.");
			client.Tick(3, Token); await Idle(client);
			Check(!client.HasPendingExchange && client.ConfirmedPets == 0 && ids.All(id => id == ids[0]) &&
				(legacy ? ids.Count == 2 && polls == 0 : ids.Count == 1 && polls == 1),
				"Uncertain cancellation preserves existing recovery: same legacy ID, or Shop status lookup without reposting.");
		}
		Pass("cancellation after a POST preserves uncertain Shop and legacy exchange recovery");
	}

	private static BackendPetClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler, TimeProvider? timeProvider = null) =>
		new(new HttpClient(new FixtureHandler(handler)) { BaseAddress = new Uri("https://batch.fixture/") }, timeProvider);
	private static HttpResponseMessage Default(HttpRequestMessage request, long pets) => Path(request) switch
	{
		"/v1/auth/steam" => Json(HttpStatusCode.OK, new { sessionToken = "fixture", steamId = "76561198000000000" }),
		"/v1/inventory" => Json(HttpStatusCode.OK, new { pets, items = Items(pets) }),
		"/v2/shop" => Shop(pets),
		_ => throw new InvalidOperationException("Unexpected batching fixture route: " + Path(request)),
	};
	private static HttpResponseMessage Grant(long pets, int granted, bool replayed = false) => Json(HttpStatusCode.OK, new { pets, goldenBones = 100, granted, replayed, items = Items(pets) });
	private static SteamInventoryItem[] Items(long pets) => new[] { new SteamInventoryItem("pets", 100, pets), new SteamInventoryItem("bones", 101, 100) };
	private static HttpResponseMessage Shop(long pets, Guid? operationId = null) => Json(HttpStatusCode.OK, new
	{
		pets, goldenBones = 100, items = Items(pets), economyVersion = "golden-bones-v2", serverTime = ServerNow,
		catalog = new { offers = new[] { new ShopOffer { Id = "dig", Kind = "box", CurrencyItemDefId = 100, Price = 500, RewardItemDefIds = new[] { 3000 }, CooldownSeconds = 1200 } },
			packs = Array.Empty<ShopCurrencyPack>(), recycleValue = 5, maxRecycleQuantity = 1 },
		capabilities = new { shopEnabled = true, cashPurchasesEnabled = false, marketEnabled = false, developerControls = false },
		status = operationId.HasValue ? "complete" : null, operationId,
		receipts = operationId.HasValue ? new[] { new ShopReceipt { ClientEventId = operationId.Value, Kind = "purchase", OfferId = "dig", CompletedAt = ServerNow,
			ReceivedItems = new[] { new SteamInventoryItem("reward", 3000, 1) } } } : Array.Empty<ShopReceipt>(),
		receivedItems = operationId.HasValue ? new[] { new SteamInventoryItem("reward", 3000, 1) } : Array.Empty<SteamInventoryItem>(),
	});
	private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
	private static string Path(HttpRequestMessage request) => request.RequestUri!.AbsolutePath;
	private static async Task<Guid> EventId(HttpRequestMessage request)
	{
		using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
		return document.RootElement.GetProperty("clientEventId").GetGuid();
	}
	private static void Enqueue(BackendPetClient client, int count) { for (var index = 0; index < count; index++) client.EnqueuePetGrant(Guid.NewGuid()); }
	private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
	private static async Task Idle(BackendPetClient client)
	{
		using var timeout = new CancellationTokenSource(Timeout);
		while (client.IsOperationInFlight) await Task.Delay(5, timeout.Token);
	}
	private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
	private static void Pass(string message) => Console.WriteLine("PASS: " + message);
	private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
