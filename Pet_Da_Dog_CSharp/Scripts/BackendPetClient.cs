using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public sealed class BackendPetClient : IDisposable
{
	private const string DefaultBackendUrl = "http://127.0.0.1:5155";
	private const double RetryDelaySeconds = 2.0;

	private readonly System.Net.Http.HttpClient _httpClient;
	private readonly Queue<Guid> _pendingGrantIds = new();

	private string? _sessionToken;
	private bool _authInFlight;
	private bool _grantInFlight;
	private double _retryDelay;

	public BackendPetClient()
	{
		_httpClient = new System.Net.Http.HttpClient
		{
			BaseAddress = new Uri(GetBackendUrl()),
			Timeout = TimeSpan.FromSeconds(15.0),
		};
	}

	public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_sessionToken);
	public int PendingGrantCount => _pendingGrantIds.Count;
	public long? ConfirmedPets { get; private set; }
	public string Status { get; private set; } = "Backend is not authenticated.";

	public void EnqueuePetGrant(Guid clientEventId)
	{
		_pendingGrantIds.Enqueue(clientEventId);
	}

	public void Tick(double delta, CancellationToken cancellationToken)
	{
		if (_retryDelay > 0.0)
		{
			_retryDelay = Math.Max(0.0, _retryDelay - delta);
			return;
		}

		if (cancellationToken.IsCancellationRequested || !IsAuthenticated || _grantInFlight || _pendingGrantIds.Count == 0)
		{
			return;
		}

		_ = GrantNextPetAsync(cancellationToken);
	}

	public async Task AuthenticateAsync(SteamIntegration steam, CancellationToken cancellationToken)
	{
		if (_authInFlight || IsAuthenticated)
		{
			return;
		}

		_authInFlight = true;
		try
		{
			var ticketHex = await steam.RequestBackendTicketAsync(cancellationToken);
			using var response = await _httpClient.PostAsJsonAsync(
				"v1/auth/steam",
				new SteamAuthRequest(ticketHex, SteamIntegration.BackendIdentity),
				cancellationToken
			);
			response.EnsureSuccessStatusCode();

			var auth = await response.Content.ReadFromJsonAsync<SteamAuthResponse>(cancellationToken: cancellationToken);
			if (auth == null || string.IsNullOrWhiteSpace(auth.SessionToken))
			{
				throw new InvalidOperationException("Backend auth returned an empty session token.");
			}

			_sessionToken = auth.SessionToken;
			_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _sessionToken);
			Status = $"Backend authenticated for Steam {auth.SteamId}.";
			await RefreshPetsAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			Status = "Backend auth canceled.";
		}
		catch (Exception exception)
		{
			Status = $"Backend auth failed: {exception.Message}";
			GD.PushWarning(Status);
		}
		finally
		{
			_authInFlight = false;
		}
	}

	public void Dispose()
	{
		_httpClient.Dispose();
	}

	private async Task GrantNextPetAsync(CancellationToken cancellationToken)
	{
		if (_pendingGrantIds.Count == 0)
		{
			return;
		}

		_grantInFlight = true;
		var clientEventId = _pendingGrantIds.Peek();

		try
		{
			using var response = await _httpClient.PostAsJsonAsync(
				"v1/pets/grant",
				new GrantPetRequest(clientEventId),
				cancellationToken
			);
			response.EnsureSuccessStatusCode();

			var grant = await response.Content.ReadFromJsonAsync<GrantPetResponse>(cancellationToken: cancellationToken);
			if (grant == null)
			{
				throw new InvalidOperationException("Backend grant returned an empty response.");
			}

			_pendingGrantIds.Dequeue();
			ConfirmedPets = grant.Pets;
			Status = $"Pets synced: {ConfirmedPets}.";
		}
		catch (OperationCanceledException)
		{
			Status = "Pet grant canceled.";
		}
		catch (Exception exception)
		{
			_retryDelay = RetryDelaySeconds;
			Status = $"Pet grant pending retry: {exception.Message}";
			GD.PushWarning(Status);
		}
		finally
		{
			_grantInFlight = false;
		}
	}

	private async Task RefreshPetsAsync(CancellationToken cancellationToken)
	{
		using var response = await _httpClient.GetAsync("v1/pets", cancellationToken);
		response.EnsureSuccessStatusCode();

		var pets = await response.Content.ReadFromJsonAsync<PetsResponse>(cancellationToken: cancellationToken);
		if (pets == null)
		{
			throw new InvalidOperationException("Backend pets endpoint returned an empty response.");
		}

		ConfirmedPets = pets.Pets;
	}

	private static string GetBackendUrl()
	{
		var configured = System.Environment.GetEnvironmentVariable("PDD_BACKEND_URL");
		var url = string.IsNullOrWhiteSpace(configured) ? DefaultBackendUrl : configured.Trim();
		return url.EndsWith("/", StringComparison.Ordinal) ? url : $"{url}/";
	}

	private sealed record SteamAuthRequest(string TicketHex, string Identity);
	private sealed record SteamAuthResponse(string SessionToken, string SteamId);
	private sealed record GrantPetRequest(Guid ClientEventId);
	private sealed record GrantPetResponse(long Pets, int Granted, bool Replayed);
	private sealed record PetsResponse(long Pets);
}
