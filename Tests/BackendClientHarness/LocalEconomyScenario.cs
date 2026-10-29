#if DEBUG
internal static class LocalEconomyScenario
{
	// Explicit opt-in integration test. It can only reach the loopback fake-Steam
	// Worker started by scripts/Run-TestEconomy.ps1; default harness runs stay offline.
	public static async Task RunAsync()
	{
		if (Environment.GetEnvironmentVariable("PDD_DEV_ECONOMY") != "1" || Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") != "1")
			throw new InvalidOperationException("Start this check with scripts/Run-TestEconomy.ps1 -TransportSmoke.");
		var configured = Environment.GetEnvironmentVariable("PDD_BACKEND_URL");
		if (!Uri.TryCreate(configured, UriKind.Absolute, out var url) || !url.IsLoopback || url.Scheme != "http")
			throw new InvalidOperationException("Only the isolated HTTP loopback test economy is allowed.");
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		var token = cancellation.Token;
		var checks = 0;
		void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
		BackendPetClient NewClient() => new(new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(15) });
		using var client = NewClient();
		Check(await client.AuthenticateTestEconomyAsync(token), $"Developer bootstrap failed: {client.ShopStatus}");
		Check(client.ShopCapabilities.DeveloperControls && client.ShopCapabilities.ShopEnabled && !client.ShopCapabilities.CashPurchasesEnabled && !client.ShopCapabilities.MarketEnabled,
			"Only fake economy capabilities may be enabled.");
		Check(client.ShopCatalog.Offers.Length == 143 && client.ShopCatalog.Packs.Length == 3 && client.ShopCatalog.MaxRecycleQuantity == 1,
			"Actual Worker catalog must deserialize all offers and packs.");
		Check(client.ConfirmedPets == 0 && client.ConfirmedGoldenBones == 0 && client.UnacknowledgedReceipts.Count == 0,
			"This test requires fresh isolated persistence and a fresh fake-Steam process.");
		Check(await client.TestSeedCurrenciesAsync(2000, 1000, token) && client.ConfirmedPets == 2000 && client.ConfirmedGoldenBones == 1000, $"Fake currency seed failed: {client.ShopStatus}");
		for (var index = 0; index < 70; index++) client.EnqueuePetGrant(Guid.NewGuid());
		Check(client.PendingGrantCount == 70 && client.ConfirmedPets == 2000, "Unsettled inputs must not change the displayed Steam-confirmed balance.");
		Check(await client.PurchaseOfferAsync("dig", token), $"Dig purchase failed: {client.ShopStatus}");
		Check(client.ConfirmedPets == 1570 && client.PendingGrantCount == 0 && client.DigCooldownRemaining.TotalMinutes > 19 && client.UnacknowledgedReceipts.Count == 1,
			"Buying must settle all 70 inputs before the permit-backed Dig Box confirms spending, cooldown, and its receipt.");
		Check(!await client.PurchaseOfferAsync("dig", token) && client.ConfirmedPets == 1570, "Cooldown must reject another immediate Dig Box.");
		Check(await client.PurchaseOfferAsync("dog:3076", token) && await client.PurchaseOfferAsync("dog:3076", token), $"Repeat direct dog purchase failed: {client.ShopStatus}");
		Check(client.ConfirmedGoldenBones == 600 && client.InventoryItems.Where(item => item.ItemDefId == 3076).Sum(item => item.Quantity) == 2 && client.UnacknowledgedReceipts.Last().IsDuplicate,
			"Direct purchases must permit duplicates and decode duplicate receipt metadata.");
		Check(await client.RecycleAsync(3076, 1, 0, token) && client.ConfirmedGoldenBones == 605, $"Recycle failed: {client.ShopStatus}");
		Check(!await client.RecycleAsync(3076, 1, 0, token), "Recycling must protect the remaining copy.");
		foreach (var receipt in client.UnacknowledgedReceipts.ToArray()) Check(await client.AcknowledgeReceiptAsync(receipt.ClientEventId, token), "A durable receipt acknowledgment failed.");
		Check(client.UnacknowledgedReceipts.Count == 0, "All acknowledged receipts must disappear from the actual Worker response.");
		Check(await client.TestResetCooldownAsync(token) && client.NextDigAt == null && await client.PurchaseOfferAsync("dig", token),
			"Isolated cooldown reset must allow another normal permit-backed purchase.");
		using var recovered = NewClient();
		Check(await recovered.AuthenticateTestEconomyAsync(token) && recovered.ConfirmedPets == 1070 && recovered.ConfirmedGoldenBones == 605 && recovered.UnacknowledgedReceipts.Count == 1,
			"A restarted C# client must recover the confirmed wallet and unacknowledged receipt.");
		recovered.EnqueuePetGrant(Guid.NewGuid()); recovered.Tick(30, token);
		while (recovered.IsOperationInFlight) await Task.Delay(10, token);
		Check(recovered.PendingGrantCount == 0 && recovered.ConfirmedPets == 1071 && recovered.ConfirmedGoldenBones == 605,
			"Background earning must preserve both actual Worker currency balances.");
		for (var index = 0; index < 5; index++) recovered.EnqueuePetGrant(Guid.NewGuid());
		Check(await recovered.SyncShopAsync(token) && recovered.PendingGrantCount == 0 && recovered.ConfirmedPets == 1076 && recovered.ConfirmedGoldenBones == 605,
			"Opening Shop must settle its captured input prefix and then read the actual Worker inventory.");
		Check(!await recovered.BuyBoxAsync("dog", token) && !recovered.HasPendingExchange && recovered.ShopCatalog.Offers.Length == 143,
			"A retired legacy purchase must not wedge the updated client.");
		Console.WriteLine($"LOCAL_ECONOMY_CLIENT_PASS: {checks} checks; actual C# client -> local Wrangler -> SQLite -> fake Steam; no real Steam or purchases.");
	}
}
#endif
