using Steamworks;

internal static class CommerceScenarios
{
	public static async Task RunAsync()
	{
		var checks = 0;
		void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
		using var steam = new SteamIntegration();
		Check(steam.Initialize(), "Commerce fixtures initialize through the stub SDK.");
		var inventoryNotifications = 0;
		steam.InventoryChanged += () => inventoryNotifications++;
		Check(!await steam.StartCurrencyPurchaseAsync(1200, CancellationToken.None) && SteamInventory.PurchaseStarts == 0,
			"Public purchases fail closed before Worker capabilities.");
		steam.ConfigureCommerce(false, false, new[] { 1200, 1201, 1202 });
		SteamInventory.Prices[1200] = (99, 99);
		SteamInventory.Prices[1201] = (499, 499);
		SteamInventory.Prices[7777] = (1, 1);
		var prices = steam.RequestPricesAsync(new[] { 1200, 1201, 1202, 7777 }, CancellationToken.None);
		Check(steam.IsPriceRequestInFlight && steam.CurrencyPrices.Count == 0, "Prices remain unavailable until the callback.");
		CallResult<SteamInventoryRequestPricesResult_t>.Dispatch(new() { m_result = EResult.k_EResultOK, m_rgchCurrency = "USD" });
		Check(await prices && steam.CurrencyPrices.Count == 2 && !steam.CurrencyPrices.ContainsKey(7777), "Only authorized packs with real Steam prices are displayed.");
		Check(steam.CurrencyPrices[1200].CurrentPrice == 99 && steam.CurrencyPrices[1200].FormattedPrice.EndsWith("USD"), "Prices preserve Steam amounts and currency.");
		Check(new SteamCurrencyPrice(1200, "JPY", 15000, 15000).FormattedPrice.StartsWith(150m.ToString("N2")), "JPY uses Steam hundredths, not ISO minor-unit scaling.");
		Check(!await steam.StartCurrencyPurchaseAsync(1200, CancellationToken.None), "Loading prices never enables a disabled checkout.");
		steam.ConfigureCommerce(true, false, new[] { 1200, 1201, 1202 });
		Check(!await steam.StartCurrencyPurchaseAsync(7777, CancellationToken.None), "An arbitrary item ID cannot reach checkout.");
		SteamUtils.OverlayEnabled = false;
		Check(!await steam.StartCurrencyPurchaseAsync(1200, CancellationToken.None) && SteamInventory.PurchaseStarts == 0, "Unavailable overlay prevents a nonworking checkout.");
		SteamUtils.OverlayEnabled = true;
		var checkout = steam.StartCurrencyPurchaseAsync(1200, CancellationToken.None);
		Check(steam.IsCheckoutInFlight && SteamInventory.PurchaseStarts == 1 && SteamInventory.LastPurchasedItem == 1200, "One pack starts the Steam checkout.");
		Check(!await steam.StartCurrencyPurchaseAsync(1201, CancellationToken.None) && SteamInventory.PurchaseStarts == 1, "Rapid clicks cannot start multiple checkouts.");
		CallResult<SteamInventoryStartPurchaseResult_t>.Dispatch(new() { m_result = EResult.k_EResultOK, m_ulOrderID = 101, m_ulTransID = 102 });
		Check(await checkout && inventoryNotifications == 0 && steam.IsCheckoutInFlight && steam.CommerceStatus.Contains("after Steam confirms"), "StartPurchase success never credits or announces inventory completion.");
		Callback<GameOverlayActivated_t>.Dispatch(new() { m_bActive = 0 });
		Check(!steam.IsCheckoutInFlight && inventoryNotifications == 1, "Closing checkout, including cancellation, requests authoritative inventory refresh.");
		var purchaseResult = new SteamInventoryResult_t(45);
		Callback<SteamInventoryResultReady_t>.Dispatch(new() { m_result = EResult.k_EResultOK, m_handle = purchaseResult });
		Check(inventoryNotifications == 2, "A delayed inventory result only signals a backend refresh.");
		Check(SteamInventory.ReleasedResults.Contains(purchaseResult), "Unsolicited purchase result handles are released after notification.");
		steam.NotifyApplicationFocusReturned();
		Check(inventoryNotifications == 3, "Returning from external Steam pages refreshes inventory.");
		var failedCheckout = steam.StartCurrencyPurchaseAsync(1201, CancellationToken.None);
		CallResult<SteamInventoryStartPurchaseResult_t>.Dispatch(new() { m_result = EResult.k_EResultFail });
		Check(!await failedCheckout && !steam.IsCheckoutInFlight, "A rejected checkout releases its UI lock without a credit.");
		Check(!steam.OpenMarket() && SteamFriends.LastOpenedUrl == null, "Market is gated independently of cash purchases.");
		steam.ConfigureCommerce(false, true, new[] { 1200, 1201, 1202 });
		Check(steam.OpenMarket() && SteamFriends.LastOpenedUrl == "https://steamcommunity.com/market/search?appid=4817200", "Market uses the official Steam page scoped to this app.");
		SteamUtils.OverlayEnabled = false;
		Check(steam.OpenSteamInventory() && Godot.OS.LastOpenedUrl == "https://steamcommunity.com/profiles/76561198000000000/inventory/#4817200_2", "Inventory falls back to the regular browser, not the in-app browser.");
		Check(steam.TryRequestInventorySnapshot(out var explicitResult), "Explicit read-only native inventory requests register their result ownership.");
		Callback<SteamInventoryResultReady_t>.Dispatch(new() { m_result = EResult.k_EResultOK, m_handle = explicitResult });
		Check(!SteamInventory.ReleasedResults.Contains(explicitResult), "Commerce callbacks must not invalidate another inventory reader's result.");
		steam.ReleaseInventorySnapshot(explicitResult);
		steam.ReleaseInventorySnapshot(explicitResult);
		Check(SteamInventory.ReleasedResults.Count(handle => handle == explicitResult) == 1, "Explicit inventory readers release their handle exactly once.");
		Console.WriteLine($"STEAM_COMMERCE_HARNESS_PASS: {checks} checks; checkout and inventory callbacks are isolated stubs.");
	}
}
