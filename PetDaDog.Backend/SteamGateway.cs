using System.Globalization;
using System.Text;
using System.Text.Json;

public interface ISteamGateway
{
	Task<string> AuthenticateUserTicketAsync(string ticketHex, string identity, CancellationToken cancellationToken);
	Task<GrantPetResult> GrantPetAsync(string steamId, ulong requestId, CancellationToken cancellationToken);
	Task<long> GetPetsAsync(string steamId, CancellationToken cancellationToken);
}

public sealed class SteamWebApiClient : ISteamGateway
{
	private readonly HttpClient _httpClient;
	private readonly AppConfig _config;

	public SteamWebApiClient(HttpClient httpClient, AppConfig config)
	{
		_httpClient = httpClient;
		_config = config;
	}

	public async Task<string> AuthenticateUserTicketAsync(string ticketHex, string identity, CancellationToken cancellationToken)
	{
		var url = Query(
			"https://partner.steam-api.com/ISteamUserAuth/AuthenticateUserTicket/v1/",
			new Dictionary<string, string>
			{
				["key"] = _config.PublisherKey,
				["appid"] = _config.AppId.ToString(CultureInfo.InvariantCulture),
				["ticket"] = ticketHex,
				["identity"] = identity,
				["format"] = "json",
			}
		);

		using var response = await _httpClient.GetAsync(url, cancellationToken);
		var body = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new SteamWebApiException($"Steam auth failed with HTTP {(int)response.StatusCode}.");
		}

		using var document = JsonDocument.Parse(body);
		var parameters = document.RootElement.GetProperty("response").GetProperty("params");
		var result = parameters.TryGetProperty("result", out var resultElement) ? resultElement.GetString() : null;
		if (!string.Equals(result, "OK", StringComparison.OrdinalIgnoreCase))
		{
			throw new SteamWebApiException($"Steam auth returned {result ?? "unknown result"}.");
		}

		return parameters.GetProperty("steamid").GetString()
			?? throw new SteamWebApiException("Steam auth response did not include steamid.");
	}

	public async Task<GrantPetResult> GrantPetAsync(string steamId, ulong requestId, CancellationToken cancellationToken)
	{
		var form = new Dictionary<string, string>
		{
			["key"] = _config.PublisherKey,
			["appid"] = _config.AppId.ToString(CultureInfo.InvariantCulture),
			["steamid"] = steamId,
			["itemdefid[0]"] = _config.PetsItemDefId.ToString(CultureInfo.InvariantCulture),
			["itempropsjson"] = "{}",
			["notify"] = "0",
			["requestid"] = requestId.ToString(CultureInfo.InvariantCulture),
		};

		using var response = await _httpClient.PostAsync(
			"https://partner.steam-api.com/IInventoryService/AddItem/v1/",
			new FormUrlEncodedContent(form),
			cancellationToken
		);
		var body = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new SteamWebApiException($"Steam inventory grant failed with HTTP {(int)response.StatusCode}.");
		}

		using var document = JsonDocument.Parse(body);
		var responseElement = document.RootElement.GetProperty("response");
		if (responseElement.TryGetProperty("success", out var successElement) && !ReadBool(successElement))
		{
			var error = responseElement.TryGetProperty("error", out var errorElement)
				? errorElement.GetString()
				: "unknown inventory error";
			throw new SteamWebApiException(error ?? "unknown inventory error");
		}

		var replayed = responseElement.TryGetProperty("replayed", out var replayedElement) && ReadBool(replayedElement);
		return new GrantPetResult(1, replayed);
	}

	public async Task<long> GetPetsAsync(string steamId, CancellationToken cancellationToken)
	{
		var url = Query(
			"https://partner.steam-api.com/IInventoryService/GetInventory/v1/",
			new Dictionary<string, string>
			{
				["key"] = _config.PublisherKey,
				["appid"] = _config.AppId.ToString(CultureInfo.InvariantCulture),
				["steamid"] = steamId,
				["format"] = "json",
			}
		);

		using var response = await _httpClient.GetAsync(url, cancellationToken);
		var body = await response.Content.ReadAsStringAsync(cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			throw new SteamWebApiException($"Steam inventory read failed with HTTP {(int)response.StatusCode}.");
		}

		using var document = JsonDocument.Parse(body);
		var responseElement = document.RootElement.GetProperty("response");
		if (!responseElement.TryGetProperty("item_json", out var itemJsonElement))
		{
			return 0;
		}

		var itemJson = itemJsonElement.GetString();
		if (string.IsNullOrWhiteSpace(itemJson))
		{
			return 0;
		}

		using var itemDocument = JsonDocument.Parse(itemJson);
		var total = 0L;
		foreach (var item in itemDocument.RootElement.EnumerateArray())
		{
			if (ReadUInt64Property(item, "itemdefid") != _config.PetsItemDefId)
			{
				continue;
			}

			if (item.TryGetProperty("state", out var stateElement)
				&& string.Equals(stateElement.GetString(), "removed", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			total += ReadInt64Property(item, "quantity") ?? 1;
		}

		return total;
	}

	private static string Query(string baseUrl, IReadOnlyDictionary<string, string> values)
	{
		var builder = new StringBuilder(baseUrl).Append('?');
		var first = true;
		foreach (var (key, value) in values)
		{
			if (!first)
			{
				builder.Append('&');
			}

			first = false;
			builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
		}

		return builder.ToString();
	}

	private static bool ReadBool(JsonElement element) => element.ValueKind switch
	{
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		JsonValueKind.Number => element.TryGetInt32(out var number) && number != 0,
		JsonValueKind.String => bool.TryParse(element.GetString(), out var boolean) ? boolean : element.GetString() == "1",
		_ => false,
	};

	private static ulong? ReadUInt64Property(JsonElement item, string name)
	{
		if (!item.TryGetProperty(name, out var element))
		{
			return null;
		}

		return element.ValueKind switch
		{
			JsonValueKind.Number when element.TryGetUInt64(out var value) => value,
			JsonValueKind.String when ulong.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) => value,
			_ => null,
		};
	}

	private static long? ReadInt64Property(JsonElement item, string name)
	{
		if (!item.TryGetProperty(name, out var element))
		{
			return null;
		}

		return element.ValueKind switch
		{
			JsonValueKind.Number when element.TryGetInt64(out var value) => value,
			JsonValueKind.String when long.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) => value,
			_ => null,
		};
	}
}

public sealed record GrantPetResult(int Granted, bool Replayed);

public sealed class SteamWebApiException : Exception
{
	public SteamWebApiException(string message)
		: base(message)
	{
	}
}
