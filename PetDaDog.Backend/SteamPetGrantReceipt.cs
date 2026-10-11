using System.Globalization;
using System.Text.Json;

// A successful HTTP status is insufficient: Steam must identify an affected
// Pets item. Keep unconfirmed clicks queued with their original request ID.
internal static class SteamPetGrantReceipt
{
	public static GrantPetResult Parse(string body, uint appId, ulong petsItemDefId)
	{
		try
		{
			using var document = JsonDocument.Parse(body);
			if (document.RootElement.ValueKind != JsonValueKind.Object
				|| !document.RootElement.TryGetProperty("response", out var response)
				|| response.ValueKind != JsonValueKind.Object)
				throw Unconfirmed();
			if (response.TryGetProperty("error", out var error)
				&& error.ValueKind != JsonValueKind.Null
				&& error.ValueKind != JsonValueKind.False
				&& !(error.ValueKind == JsonValueKind.String && error.GetString() == ""))
				throw Unconfirmed();
			if (response.TryGetProperty("success", out var success) && !ReadFlag(success))
				throw Unconfirmed();
			if (!response.TryGetProperty("item_json", out var itemJson) || itemJson.ValueKind != JsonValueKind.String)
				throw Unconfirmed();
			using var itemsDocument = JsonDocument.Parse(itemJson.GetString()!);
			if (itemsDocument.RootElement.ValueKind != JsonValueKind.Array)
				throw Unconfirmed();

			var seen = new HashSet<ulong>();
			var hasPets = false;
			foreach (var item in itemsDocument.RootElement.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
					throw Unconfirmed();
				if (item.TryGetProperty("appid", out var itemApp) && ReadUInt64(itemApp) != appId)
					throw Unconfirmed();
				if (!item.TryGetProperty("itemid", out var itemIdValue) || !item.TryGetProperty("itemdefid", out var itemDefValue))
					throw Unconfirmed();
				var itemId = ReadUInt64(itemIdValue);
				var itemDef = ReadUInt64(itemDefValue);
				if (itemId == 0 || itemDef == 0 || itemDef > uint.MaxValue || !seen.Add(itemId))
					throw Unconfirmed();
				var state = "";
				if (item.TryGetProperty("state", out var stateValue))
				{
					if (stateValue.ValueKind != JsonValueKind.String) throw Unconfirmed();
					state = stateValue.GetString()!.ToLowerInvariant();
				}
				if (state is not ("" or "removed" or "consumed")) throw Unconfirmed();
				if (item.TryGetProperty("quantity", out var quantity))
				{
					if (ReadUInt64(quantity) > uint.MaxValue) throw Unconfirmed();
				}
				else if (state == "") throw Unconfirmed();
				// Replayed receipts may refer to a Pets stack that was later spent.
				hasPets |= itemDef == petsItemDefId;
			}
			if (!hasPets) throw Unconfirmed();
			var replayed = response.TryGetProperty("replayed", out var replayedValue) && ReadFlag(replayedValue);
			return new GrantPetResult(1, replayed);
		}
		catch (JsonException)
		{
			throw Unconfirmed();
		}
	}

	private static ulong ReadUInt64(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)) return number;
		if (value.ValueKind == JsonValueKind.String
			&& ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) return number;
		throw Unconfirmed();
	}

	private static bool ReadFlag(JsonElement value)
	{
		if (value.ValueKind == JsonValueKind.True) return true;
		if (value.ValueKind == JsonValueKind.False) return false;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is 0 or 1) return number == 1;
		if (value.ValueKind == JsonValueKind.String)
		{
			if (value.GetString() is "true" or "1") return true;
			if (value.GetString() is "false" or "0") return false;
		}
		throw Unconfirmed();
	}

	// Steam error text can contain request data. Do not expose or log it.
	private static SteamWebApiException Unconfirmed() => new("Steam did not confirm a Pets grant.");
}
