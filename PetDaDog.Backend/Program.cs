using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:5155");
builder.Services.AddSingleton(AppConfig.LoadFromEnvironment());
builder.Services.AddSingleton<SessionTokenService>();
builder.Services.AddHttpClient<SteamWebApiClient>(client => client.Timeout = TimeSpan.FromSeconds(15));

var app = builder.Build();

app.MapGet("/health", (AppConfig config) =>
	Results.Ok(new
	{
		status = "ok",
		appId = config.AppId,
		petsItemDefId = config.PetsItemDefId.ToString(CultureInfo.InvariantCulture),
	})
);

app.MapPost("/v1/auth/steam", async (
	SteamAuthRequest request,
	SteamWebApiClient steam,
	SessionTokenService sessions,
	CancellationToken cancellationToken
) =>
{
	if (string.IsNullOrWhiteSpace(request.TicketHex))
	{
		return Results.BadRequest(new ErrorResponse("ticketHex is required."));
	}

	if (!string.Equals(request.Identity, "petdadog-backend", StringComparison.Ordinal))
	{
		return Results.BadRequest(new ErrorResponse("identity must be petdadog-backend."));
	}

	try
	{
		var steamId = await steam.AuthenticateUserTicketAsync(request.TicketHex, request.Identity, cancellationToken);
		return Results.Ok(new SteamAuthResponse(sessions.Create(steamId), steamId));
	}
	catch (SteamWebApiException)
	{
		return Results.Unauthorized();
	}
});

app.MapGet("/v1/pets", async (
	HttpContext context,
	SessionTokenService sessions,
	SteamWebApiClient steam,
	CancellationToken cancellationToken
) =>
{
	if (!TryGetSessionSteamId(context, sessions, out var steamId))
	{
		return Results.Unauthorized();
	}

	var pets = await steam.GetPetsAsync(steamId, cancellationToken);
	return Results.Ok(new PetsResponse(pets));
});

app.MapPost("/v1/pets/grant", async (
	HttpContext context,
	GrantPetRequest request,
	SessionTokenService sessions,
	SteamWebApiClient steam,
	CancellationToken cancellationToken
) =>
{
	if (!TryGetSessionSteamId(context, sessions, out var steamId))
	{
		return Results.Unauthorized();
	}

	if (request.ClientEventId == Guid.Empty)
	{
		return Results.BadRequest(new ErrorResponse("clientEventId is required."));
	}

	var requestId = SteamRequestId.From(steamId, request.ClientEventId);
	var grant = await steam.GrantPetAsync(steamId, requestId, cancellationToken);
	var pets = await steam.GetPetsAsync(steamId, cancellationToken);
	return Results.Ok(new GrantPetResponse(pets, grant.Granted, grant.Replayed));
});

app.Run();

static bool TryGetSessionSteamId(HttpContext context, SessionTokenService sessions, out string steamId)
{
	steamId = string.Empty;
	var authorization = context.Request.Headers.Authorization.ToString();
	if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
	{
		return false;
	}

	return sessions.TryValidate(authorization["Bearer ".Length..].Trim(), out steamId);
}

public sealed record SteamAuthRequest(string TicketHex, string Identity);
public sealed record SteamAuthResponse(string SessionToken, string SteamId);
public sealed record PetsResponse(long Pets);
public sealed record GrantPetRequest(Guid ClientEventId);
public sealed record GrantPetResponse(long Pets, int Granted, bool Replayed);
public sealed record ErrorResponse(string Error);

public sealed class AppConfig
{
	private AppConfig(uint appId, string publisherKey, ulong petsItemDefId, byte[] sessionSecret)
	{
		AppId = appId;
		PublisherKey = publisherKey;
		PetsItemDefId = petsItemDefId;
		SessionSecret = sessionSecret;
	}

	public uint AppId { get; }
	public string PublisherKey { get; }
	public ulong PetsItemDefId { get; }
	public byte[] SessionSecret { get; }

	public static AppConfig LoadFromEnvironment()
	{
		var missing = new List<string>();
		var appIdText = Require("PDD_STEAM_APP_ID", missing);
		var publisherKey = Require("PDD_STEAM_PUBLISHER_KEY", missing);
		var petsItemDefIdText = Require("PDD_STEAM_PETS_ITEMDEF_ID", missing);
		var sessionSecret = Require("PDD_BACKEND_SESSION_SECRET", missing);

		if (missing.Count > 0)
		{
			throw new InvalidOperationException($"Missing required backend config: {string.Join(", ", missing)}.");
		}

		if (!uint.TryParse(appIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
		{
			throw new InvalidOperationException("PDD_STEAM_APP_ID must be an unsigned integer.");
		}

		if (!ulong.TryParse(petsItemDefIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var petsItemDefId))
		{
			throw new InvalidOperationException("PDD_STEAM_PETS_ITEMDEF_ID must be an unsigned integer.");
		}

		var secretBytes = Encoding.UTF8.GetBytes(sessionSecret);
		if (secretBytes.Length < 32)
		{
			throw new InvalidOperationException("PDD_BACKEND_SESSION_SECRET must be at least 32 UTF-8 bytes.");
		}

		return new AppConfig(appId, publisherKey, petsItemDefId, secretBytes);
	}

	private static string Require(string name, ICollection<string> missing)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}

		missing.Add(name);
		return string.Empty;
	}
}

public sealed class SteamWebApiClient
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
			if (!MatchesPetsItemDef(item))
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

	private bool MatchesPetsItemDef(JsonElement item)
	{
		return ReadUInt64Property(item, "itemdefid") == _config.PetsItemDefId;
	}

	private static string Query(string baseUrl, IReadOnlyDictionary<string, string> values)
	{
		var builder = new StringBuilder(baseUrl);
		builder.Append('?');
		var first = true;
		foreach (var (key, value) in values)
		{
			if (!first)
			{
				builder.Append('&');
			}

			first = false;
			builder.Append(Uri.EscapeDataString(key));
			builder.Append('=');
			builder.Append(Uri.EscapeDataString(value));
		}

		return builder.ToString();
	}

	private static bool ReadBool(JsonElement element)
	{
		return element.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Number => element.TryGetInt32(out var number) && number != 0,
			JsonValueKind.String => bool.TryParse(element.GetString(), out var boolean)
				? boolean
				: element.GetString() == "1",
			_ => false,
		};
	}

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

public sealed class SessionTokenService
{
	private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly AppConfig _config;

	public SessionTokenService(AppConfig config)
	{
		_config = config;
	}

	public string Create(string steamId)
	{
		var payload = new SessionTokenPayload(
			steamId,
			DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds()
		);
		var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
		var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
		var payloadEncoded = Base64Url.Encode(payloadBytes);
		var signature = Sign(payloadEncoded);
		return $"{payloadEncoded}.{signature}";
	}

	public bool TryValidate(string token, out string steamId)
	{
		steamId = string.Empty;
		var parts = token.Split('.', 2);
		if (parts.Length != 2)
		{
			return false;
		}

		var expectedSignature = Sign(parts[0]);
		if (!CryptographicOperations.FixedTimeEquals(
			Encoding.ASCII.GetBytes(expectedSignature),
			Encoding.ASCII.GetBytes(parts[1])
		))
		{
			return false;
		}

		SessionTokenPayload? payload;
		try
		{
			var payloadBytes = Base64Url.Decode(parts[0]);
			payload = JsonSerializer.Deserialize<SessionTokenPayload>(payloadBytes, JsonOptions);
		}
		catch
		{
			return false;
		}
		if (payload == null || payload.ExpiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
		{
			return false;
		}

		steamId = payload.SteamId;
		return !string.IsNullOrWhiteSpace(steamId);
	}

	private string Sign(string payload)
	{
		using var hmac = new HMACSHA256(_config.SessionSecret);
		return Base64Url.Encode(hmac.ComputeHash(Encoding.ASCII.GetBytes(payload)));
	}
}

public static class SteamRequestId
{
	public static ulong From(string steamId, Guid clientEventId)
	{
		var input = Encoding.UTF8.GetBytes($"{steamId}:{clientEventId:D}");
		var hash = SHA256.HashData(input);
		return BitConverter.ToUInt64(hash, 0);
	}
}

public static class Base64Url
{
	public static string Encode(byte[] bytes)
	{
		return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	public static byte[] Decode(string value)
	{
		var base64 = value.Replace('-', '+').Replace('_', '/');
		var padding = base64.Length % 4;
		if (padding > 0)
		{
			base64 = base64.PadRight(base64.Length + 4 - padding, '=');
		}

		return Convert.FromBase64String(base64);
	}
}

public sealed record GrantPetResult(int Granted, bool Replayed);
public sealed record SessionTokenPayload(string SteamId, long ExpiresAtUnixSeconds);

public sealed class SteamWebApiException : Exception
{
	public SteamWebApiException(string message)
		: base(message)
	{
	}
}
