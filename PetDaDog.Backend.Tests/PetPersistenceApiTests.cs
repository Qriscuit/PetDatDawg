using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class PetPersistenceApiTests : IClassFixture<PetBackendFactory>
{
	private readonly PetBackendFactory _factory;

	public PetPersistenceApiTests(PetBackendFactory factory)
	{
		_factory = factory;
	}

	[Fact]
	public async Task ValidGrantAddsOnePetAndReadReturnsTheAuthenticatedUsersTotal()
	{
		_factory.Gateway.Reset();
		_factory.Gateway.SetPets(FakeSteamGateway.DefaultSteamId, 7);
		using var client = _factory.CreateClient();
		await AuthenticateAsync(client);

		var grant = await client.PostAsJsonAsync("/v1/pets/grant", new GrantPetRequest(Guid.NewGuid()));
		Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
		Assert.Equal(8, await ReadPetsAsync(grant));

		var pets = await client.GetAsync("/v1/pets");
		Assert.Equal(HttpStatusCode.OK, pets.StatusCode);
		Assert.Equal(8, await ReadPetsAsync(pets));
	}

	[Fact]
	public async Task ReplayingTheSameClientEventDoesNotGrantTwice()
	{
		_factory.Gateway.Reset();
		using var client = _factory.CreateClient();
		await AuthenticateAsync(client);
		var eventId = Guid.NewGuid();

		var first = await client.PostAsJsonAsync("/v1/pets/grant", new GrantPetRequest(eventId));
		var second = await client.PostAsJsonAsync("/v1/pets/grant", new GrantPetRequest(eventId));

		Assert.Equal(HttpStatusCode.OK, first.StatusCode);
		Assert.Equal(HttpStatusCode.OK, second.StatusCode);
		Assert.Equal(1, await ReadPetsAsync(second));
		Assert.True(await ReadReplayedAsync(second));
	}

	[Fact]
	public async Task InvalidOrMissingAuthenticationCannotReadOrGrantPets()
	{
		_factory.Gateway.Reset();
		using var client = _factory.CreateClient();

		Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/pets")).StatusCode);
		Assert.Equal(
			HttpStatusCode.Unauthorized,
			(await client.PostAsJsonAsync("/v1/pets/grant", new GrantPetRequest(Guid.NewGuid()))).StatusCode
		);

		var auth = await client.PostAsJsonAsync("/v1/auth/steam", new SteamAuthRequest("invalid-ticket", "petdadog-backend"));
		Assert.Equal(HttpStatusCode.Unauthorized, auth.StatusCode);
	}

	[Fact]
	public async Task EmptyClientEventIsRejectedWithoutChangingInventory()
	{
		_factory.Gateway.Reset();
		_factory.Gateway.SetPets(FakeSteamGateway.DefaultSteamId, 4);
		using var client = _factory.CreateClient();
		await AuthenticateAsync(client);

		var response = await client.PostAsJsonAsync("/v1/pets/grant", new GrantPetRequest(Guid.Empty));

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal(4, _factory.Gateway.GetPets(FakeSteamGateway.DefaultSteamId));
	}

	[Fact]
	public async Task SteamGatewayFailuresAreReportedWithoutChangingInventory()
	{
		_factory.Gateway.Reset();
		_factory.Gateway.FailGrants = true;
		using var client = _factory.CreateClient();
		await AuthenticateAsync(client);

		var response = await client.PostAsJsonAsync("/v1/pets/grant", new GrantPetRequest(Guid.NewGuid()));

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		Assert.Equal(0, _factory.Gateway.GetPets(FakeSteamGateway.DefaultSteamId));
	}

	private static async Task AuthenticateAsync(HttpClient client)
	{
		var response = await client.PostAsJsonAsync("/v1/auth/steam", new SteamAuthRequest("valid-ticket", "petdadog-backend"));
		response.EnsureSuccessStatusCode();
		using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var token = document.RootElement.GetProperty("sessionToken").GetString();
		Assert.False(string.IsNullOrWhiteSpace(token));
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
	}

	private static async Task<long> ReadPetsAsync(HttpResponseMessage response)
	{
		using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("pets").GetInt64();
	}

	private static async Task<bool> ReadReplayedAsync(HttpResponseMessage response)
	{
		using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return document.RootElement.GetProperty("replayed").GetBoolean();
	}
}

public sealed class PetBackendFactory : WebApplicationFactory<Program>
{
	public FakeSteamGateway Gateway { get; } = new();

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		Environment.SetEnvironmentVariable("PDD_STEAM_APP_ID", "4817200");
		Environment.SetEnvironmentVariable("PDD_STEAM_PUBLISHER_KEY", "test-publisher-key");
		Environment.SetEnvironmentVariable("PDD_STEAM_PETS_ITEMDEF_ID", "900001");
		Environment.SetEnvironmentVariable("PDD_BACKEND_SESSION_SECRET", "test-session-secret-must-be-at-least-thirty-two-bytes");

		builder.UseEnvironment("Testing");
		builder.ConfigureServices(services =>
		{
			services.RemoveAll<ISteamGateway>();
			services.AddSingleton<ISteamGateway>(Gateway);
		});
	}
}

public sealed class FakeSteamGateway : ISteamGateway
{
	public const string DefaultSteamId = "76561198000000001";

	private readonly ConcurrentDictionary<string, long> _petsBySteamId = new();
	private readonly ConcurrentDictionary<ulong, byte> _fulfilledRequestIds = new();

	public bool FailGrants { get; set; }

	public Task<string> AuthenticateUserTicketAsync(string ticketHex, string identity, CancellationToken cancellationToken)
	{
		if (!string.Equals(ticketHex, "valid-ticket", StringComparison.Ordinal)
			|| !string.Equals(identity, "petdadog-backend", StringComparison.Ordinal))
		{
			throw new SteamWebApiException("Invalid test ticket.");
		}

		return Task.FromResult(DefaultSteamId);
	}

	public Task<GrantPetResult> GrantPetAsync(string steamId, ulong requestId, CancellationToken cancellationToken)
	{
		if (FailGrants)
		{
			throw new SteamWebApiException("Configured test grant failure.");
		}

		if (!_fulfilledRequestIds.TryAdd(requestId, 0))
		{
			return Task.FromResult(new GrantPetResult(1, true));
		}

		_petsBySteamId.AddOrUpdate(steamId, 1, (_, pets) => pets + 1);
		return Task.FromResult(new GrantPetResult(1, false));
	}

	public Task<long> GetPetsAsync(string steamId, CancellationToken cancellationToken)
	{
		return Task.FromResult(GetPets(steamId));
	}

	public void SetPets(string steamId, long pets) => _petsBySteamId[steamId] = pets;

	public long GetPets(string steamId) => _petsBySteamId.TryGetValue(steamId, out var pets) ? pets : 0;

	public void Reset()
	{
		_petsBySteamId.Clear();
		_fulfilledRequestIds.Clear();
		FailGrants = false;
	}
}
