using System.Net;
using System.Text;
using System.Text.Json;

const string key = "fake-publisher-key-not-for-output";
const string account = "76561198000000000";
const ulong requestId = ulong.MaxValue;
var configNames = new[] { "PDD_STEAM_APP_ID", "PDD_STEAM_PUBLISHER_KEY", "PDD_STEAM_PETS_ITEMDEF_ID", "PDD_BACKEND_SESSION_SECRET" };
var previous = configNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
var checks = 0;
try
{
	Environment.SetEnvironmentVariable(configNames[0], "4817200");
	Environment.SetEnvironmentVariable(configNames[1], key);
	Environment.SetEnvironmentVariable(configNames[2], "100");
	Environment.SetEnvironmentVariable(configNames[3], "fake-test-secret-of-at-least-thirty-two-bytes");
	var config = AppConfig.LoadFromEnvironment();
	var echo = $"{key} {account} https://example.test/?ticket=fake-ticket";
	object Row(string id = "18446744073709551615", int def = 100, object? quantity = null, string state = "") =>
		new { itemid = id, itemdefid = def, quantity = quantity ?? 1, state, appid = 4817200 };
	string Body(object[] rows, object? success = null, object? replayed = null)
	{
		var response = new Dictionary<string, object> { ["item_json"] = JsonSerializer.Serialize(rows) };
		if (success is not null) response["success"] = success;
		if (replayed is not null) response["replayed"] = replayed;
		return JsonSerializer.Serialize(new { response });
	}
	foreach (var (body, replayed) in new[] {
		(Body(new[] { Row() }), false),
		(Body(new[] { Row() }, true, true), true),
		(Body(new[] { Row(quantity: 0, state: "consumed") }, "1", "true"), true),
		("{\"response\":{\"item_json\":\"[{\\\"itemid\\\":\\\"18446744073709551615\\\",\\\"itemdefid\\\":100,\\\"state\\\":\\\"removed\\\"}]\",\"replayed\":1}}", true),
	})
	{
		using var handler = new FakeSteamHandler(body, key, account, requestId);
		using var http = new HttpClient(handler);
		var result = await new SteamWebApiClient(http, config).GrantPetAsync(account, requestId, CancellationToken.None);
		Check(result.Granted == 1 && result.Replayed == replayed, "Valid receipt contract changed.");
		Check(handler.Calls == 1, "A receipt validation must not resend a grant.");
	}
	var invalid = new[] {
		Body(Array.Empty<object>()), Body(new[] { Row(def: 3000) }), "{}", "[]", "{\"response\":{}}",
		"{\"response\":{\"item_json\":\"{}\"}}", echo,
		JsonSerializer.Serialize(new { response = new { success = false, error = echo, item_json = "[]" } }),
		JsonSerializer.Serialize(new { response = new { error = echo, item_json = JsonSerializer.Serialize(new[] { Row() }) } }),
		Body(new[] { Row() }, true, echo), Body(new[] { Row() }, 2),
		Body(new[] { Row(id: "0") }), Body(new[] { Row(), Row() }), Body(new[] { Row(quantity: -1) }),
		Body(new[] { Row(quantity: "18446744073709551616") }), Body(new[] { Row(state: echo) }),
		"{\"response\":{\"item_json\":\"[{\\\"itemid\\\":\\\"1\\\",\\\"itemdefid\\\":100}]\"}}",
		"{\"response\":{\"item_json\":\"[{\\\"itemid\\\":\\\"1\\\",\\\"itemdefid\\\":100,\\\"quantity\\\":1,\\\"appid\\\":480}]\"}}",
	};
	foreach (var body in invalid)
	foreach (var resultHeaders in new string[]?[] { null, new[] { "1" } })
	{
		using var handler = new FakeSteamHandler(body, key, account, requestId, resultHeaders);
		using var http = new HttpClient(handler);
		try
		{
			await new SteamWebApiClient(http, config).GrantPetAsync(account, requestId, CancellationToken.None);
			throw new Exception("An unconfirmed grant was reported as successful.");
		}
		catch (SteamWebApiException error)
		{
			Check(error.Message == "Steam did not confirm a Pets grant.", "Untrusted Steam response escaped into the error.");
			Check(error.InnerException is null, "Raw upstream data must not be attached to the error.");
		}
		Check(handler.Calls == 1, "Invalid receipts must not trigger automatic mutations.");
	}
	var rejectedHeaders = new (string[]? Results, string? Error)[] {
		(new[] { "2" }, null), (new[] { "26" }, null), (new[] { "0" }, null),
		(new[] { "" }, null), (new[] { "01" }, null), (new[] { echo }, null),
		(new[] { "1", "2" }, null), (new[] { "1, 2" }, null),
		(null, echo), (new[] { "1" }, echo),
	};
	foreach (var inventoryRead in new[] { false, true })
	foreach (var body in new[] { Body(Array.Empty<object>()), Body(new[] { Row(quantity: 41) }) })
	foreach (var (resultHeaders, errorHeader) in rejectedHeaders)
	{
		using var handler = new FakeSteamHandler(body, key, account, requestId, resultHeaders, errorHeader, inventoryRead);
		using var http = new HttpClient(handler);
		var steam = new SteamWebApiClient(http, config);
		GrantPetResult? confirmedGrant = null;
		long? confirmedBalance = null;
		try
		{
			if (!inventoryRead) confirmedGrant = await steam.GrantPetAsync(account, requestId, CancellationToken.None);
			confirmedBalance = await steam.GetPetsAsync(account, CancellationToken.None);
			throw new Exception("An unsuccessful Steam header was accepted as confirmed inventory data.");
		}
		catch (SteamWebApiException error)
		{
			var operation = inventoryRead ? "inventory read" : "inventory grant";
			Check(error.Message == $"Steam {operation} rejected the request.", "Raw Steam header data escaped into the error.");
			Check(error.InnerException is null, "Raw upstream header data must not be attached to the error.");
		}
		Check(confirmedGrant is null && confirmedBalance is null, "A failed header must not confirm a grant or balance.");
		Check(handler.Calls == 1, "Header rejection must not resend a mutation or read after an unconfirmed grant.");
		Check(handler.Mutations == (inventoryRead ? 0 : 1), "Inventory reads must not send mutations.");
	}
	// Headers are optional, but an explicit success must retain receipt checks.
	foreach (var inventoryRead in new[] { false, true })
	{
		using var handler = new FakeSteamHandler(Body(new[] { Row(quantity: 41) }), key, account, requestId, new[] { "1" }, "", inventoryRead);
		using var http = new HttpClient(handler);
		var steam = new SteamWebApiClient(http, config);
		if (inventoryRead)
			Check(await steam.GetPetsAsync(account, CancellationToken.None) == 41, "A successful inventory result was rejected.");
		else
			Check((await steam.GrantPetAsync(account, requestId, CancellationToken.None)).Granted == 1, "A successful grant result was rejected.");
		Check(handler.Calls == 1, "Successful result validation sent another request.");
	}
	foreach (var resultHeaders in new string[]?[] { null, new[] { "1" } })
	{
		using var handler = new FakeSteamHandler(Body(Array.Empty<object>()), key, account, requestId, resultHeaders, inventoryRead: true);
		using var http = new HttpClient(handler);
		Check(await new SteamWebApiClient(http, config).GetPetsAsync(account, CancellationToken.None) == 0,
			"A valid empty inventory must still confirm a zero balance.");
	}
	var expected = SteamRequestId.From(account, Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
	Check(expected == 5266704354957166947UL, "Production request ID differs from Worker vector.");
	Console.WriteLine($"Backend grant harness passed {checks} checks with fake Steam only.");
}
finally
{
	foreach (var name in configNames) Environment.SetEnvironmentVariable(name, previous[name]);
}
void Check(bool condition, string message)
{
	if (!condition) throw new Exception(message);
	checks++;
}

sealed class FakeSteamHandler(string response, string key, string account, ulong requestId,
	string[]? resultHeaders = null, string? errorHeader = null, bool inventoryRead = false) : HttpMessageHandler
{
	public int Calls { get; private set; }
	public int Mutations { get; private set; }
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		Calls++;
		Dictionary<string, string> DecodeForm(string body) => body.Split('&').Select(part => part.Split('=', 2))
			.ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
		if (inventoryRead)
		{
			if (request.Method != HttpMethod.Get || request.RequestUri?.GetLeftPart(UriPartial.Path) != "https://partner.steam-api.com/IInventoryService/GetInventory/v1/")
				throw new Exception("Unexpected Steam inventory endpoint.");
			var query = DecodeForm(request.RequestUri.Query.TrimStart('?'));
			if (query["key"] != key || query["steamid"] != account || query["appid"] != "4817200" || query["format"] != "json")
				throw new Exception("Inventory transport changed its fixed identifiers.");
		}
		else
		{
			if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://partner.steam-api.com/IInventoryService/AddItem/v1/")
				throw new Exception("Unexpected Steam grant endpoint.");
			Mutations++;
			var body = await request.Content!.ReadAsStringAsync(cancellationToken);
			var form = DecodeForm(body);
			if (form["key"] != key || form["steamid"] != account || form["appid"] != "4817200"
				|| form["requestid"] != requestId.ToString(System.Globalization.CultureInfo.InvariantCulture) || form["itemdefid[0]"] != "100")
				throw new Exception("Grant transport changed its fixed identifiers or request ID.");
		}
		var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
		if (resultHeaders is not null) message.Headers.TryAddWithoutValidation("x-eresult", resultHeaders);
		if (errorHeader is not null) message.Headers.TryAddWithoutValidation("x-error_message", errorHeader);
		return message;
	}
}
