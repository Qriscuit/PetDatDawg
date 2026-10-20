using System;
using System.Collections.Generic;

// These are confirmed Worker/Steam snapshots, never a locally editable wallet.
public sealed record ShopOffer
{
	public string Id { get; init; } = "";
	public string Name { get; init; } = "";
	public string Kind { get; init; } = "";
	public int CurrencyItemDefId { get; init; }
	public long Price { get; init; }
	public int[] RewardItemDefIds { get; init; } = Array.Empty<int>();
	public int CooldownSeconds { get; init; }
}

public sealed record ShopCurrencyPack
{
	public int ItemDefId { get; init; }
	public long Quantity { get; init; }
	public decimal TestPriceUsd { get; init; }
}

public sealed record ShopCatalog
{
	public ShopOffer[] Offers { get; init; } = Array.Empty<ShopOffer>();
	public ShopCurrencyPack[] Packs { get; init; } = Array.Empty<ShopCurrencyPack>();
	public long RecycleValue { get; init; }
	public int MaxRecycleQuantity { get; init; } = 1;
}

public sealed record ShopCapabilities
{
	public bool ShopEnabled { get; init; }
	public bool CashPurchasesEnabled { get; init; }
	public bool MarketEnabled { get; init; }
	public bool DeveloperControls { get; init; }
}

public sealed record ShopReceipt
{
	public Guid ClientEventId { get; init; }
	public string Kind { get; init; } = "";
	public string? OfferId { get; init; }
	public SteamInventoryItem[] ReceivedItems { get; init; } = Array.Empty<SteamInventoryItem>();
	public DateTimeOffset CompletedAt { get; init; }
	public bool IsDuplicate { get; init; }
}

public sealed record ShopPendingOperation
{
	public string ClientEventId { get; init; } = "";
	public string Kind { get; init; } = "";
	public string? OfferId { get; init; }
}

internal sealed record ShopResponse
{
	public long? Pets { get; init; }
	public long? GoldenBones { get; init; }
	public SteamInventoryItem[]? Items { get; init; }
	public string? EconomyVersion { get; init; }
	public DateTimeOffset ServerTime { get; init; }
	public DateTimeOffset? NextDigAt { get; init; }
	public ShopCatalog? Catalog { get; init; }
	public ShopCapabilities? Capabilities { get; init; }
	public ShopPendingOperation? PendingOperation { get; init; }
	public ShopReceipt[]? Receipts { get; init; }
	public string? Status { get; init; }
	public Guid? OperationId { get; init; }
	public SteamInventoryItem[]? ReceivedItems { get; init; }
}
