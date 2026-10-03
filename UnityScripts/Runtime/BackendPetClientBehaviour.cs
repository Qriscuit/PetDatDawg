using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace PetDaDog.Unity
{
    [DisallowMultipleComponent]
    public sealed class BackendPetClientBehaviour : MonoBehaviour
    {
        private const string DefaultBackendUrl = "http://127.0.0.1:5155";
        private const double RetryDelaySeconds = 2.0;
        private const double AuthenticationRetrySeconds = 5.0;
        private const int RequestTimeoutSeconds = 15;

        [Serializable]
        private sealed class SteamAuthRequest
        {
            public string ticketHex;
            public string identity;
        }

        [Serializable]
        private sealed class SteamAuthResponse
        {
            public string sessionToken;
            public string steamId;
        }

        [Serializable]
        private sealed class GrantPetRequest
        {
            public string clientEventId;
        }

        [Serializable]
        private sealed class GrantPetResponse
        {
            public long pets;
            public int granted;
            public bool replayed;
        }

        [Serializable]
        private sealed class PetsResponse
        {
            public long pets;
        }

        private readonly Queue<Guid> _pendingGrantIds = new Queue<Guid>();
        private CancellationTokenSource _shutdown;
        private SteamIntegrationBehaviour _steam;
        private string _baseUrl;
        private string _sessionToken;
        private bool _initialized;
        private bool _authenticationInFlight;
        private bool _grantInFlight;
        private double _authenticationRetryTimer;
        private double _retryDelay;

        public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_sessionToken);
        public int PendingGrantCount => _pendingGrantIds.Count;
        public long? ConfirmedPets { get; private set; }
        public string Status { get; private set; } = "Backend is not authenticated.";

        public void Initialize(SteamIntegrationBehaviour steam)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _steam = steam;
            _baseUrl = ResolveBackendUrl();
            _shutdown = new CancellationTokenSource();
        }

        public void EnqueuePetGrant(Guid clientEventId)
        {
            _pendingGrantIds.Enqueue(clientEventId);
        }

        private void Update()
        {
            if (!_initialized || _shutdown == null || _shutdown.IsCancellationRequested || _steam == null || !_steam.IsInitialized)
            {
                return;
            }

            var delta = Time.unscaledDeltaTime;
            if (!IsAuthenticated)
            {
                _authenticationRetryTimer -= delta;
                if (!_authenticationInFlight && _authenticationRetryTimer <= 0.0)
                {
                    _authenticationRetryTimer = AuthenticationRetrySeconds;
                    _ = AuthenticateAsync(_shutdown.Token);
                }
            }

            if (_retryDelay > 0.0)
            {
                _retryDelay = Math.Max(0.0, _retryDelay - delta);
                return;
            }

            if (IsAuthenticated && !_grantInFlight && _pendingGrantIds.Count > 0)
            {
                _ = GrantNextPetAsync(_shutdown.Token);
            }
        }

        private async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            if (_authenticationInFlight || IsAuthenticated)
            {
                return;
            }

            _authenticationInFlight = true;
            try
            {
                var ticketHex = await _steam.RequestBackendTicketAsync(cancellationToken);
                var auth = await PostJsonAsync<SteamAuthRequest, SteamAuthResponse>(
                    "v1/auth/steam",
                    new SteamAuthRequest { ticketHex = ticketHex, identity = SteamIntegrationBehaviour.BackendIdentity },
                    null,
                    cancellationToken);
                if (auth == null || string.IsNullOrWhiteSpace(auth.sessionToken))
                {
                    throw new InvalidOperationException("Backend auth returned an empty session token.");
                }

                _sessionToken = auth.sessionToken;
                Status = $"Backend authenticated for Steam {auth.steamId}.";
                await RefreshPetsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Status = "Backend auth canceled.";
            }
            catch (Exception exception)
            {
                Status = $"Backend auth failed: {exception.Message}";
                Debug.LogWarning(Status);
            }
            finally
            {
                _authenticationInFlight = false;
            }
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
                var grant = await PostJsonAsync<GrantPetRequest, GrantPetResponse>(
                    "v1/pets/grant",
                    new GrantPetRequest { clientEventId = clientEventId.ToString("D") },
                    _sessionToken,
                    cancellationToken);
                if (grant == null)
                {
                    throw new InvalidOperationException("Backend grant returned an empty response.");
                }

                _pendingGrantIds.Dequeue();
                ConfirmedPets = grant.pets;
                Status = $"Pets synced: {ConfirmedPets.Value}.";
            }
            catch (OperationCanceledException)
            {
                Status = "Pet grant canceled.";
            }
            catch (Exception exception)
            {
                _retryDelay = RetryDelaySeconds;
                Status = $"Pet grant pending retry: {exception.Message}";
                Debug.LogWarning(Status);
            }
            finally
            {
                _grantInFlight = false;
            }
        }

        private async Task RefreshPetsAsync(CancellationToken cancellationToken)
        {
            var pets = await GetJsonAsync<PetsResponse>("v1/pets", _sessionToken, cancellationToken);
            if (pets == null)
            {
                throw new InvalidOperationException("Backend pets endpoint returned an empty response.");
            }

            ConfirmedPets = pets.pets;
        }

        private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(string path, TRequest payload, string token, CancellationToken cancellationToken)
        {
            var requestBody = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            using var request = new UnityWebRequest(new Uri(new Uri(_baseUrl), path), UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(requestBody),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = RequestTimeoutSeconds,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.SetRequestHeader("Authorization", $"Bearer {token}");
            }

            await request.SendAsync(cancellationToken);
            EnsureSuccessful(request);
            return JsonUtility.FromJson<TResponse>(request.downloadHandler.text);
        }

        private async Task<TResponse> GetJsonAsync<TResponse>(string path, string token, CancellationToken cancellationToken)
        {
            using var request = UnityWebRequest.Get(new Uri(new Uri(_baseUrl), path));
            request.timeout = RequestTimeoutSeconds;
            request.SetRequestHeader("Authorization", $"Bearer {token}");
            await request.SendAsync(cancellationToken);
            EnsureSuccessful(request);
            return JsonUtility.FromJson<TResponse>(request.downloadHandler.text);
        }

        private static void EnsureSuccessful(UnityWebRequest request)
        {
            if (request.result == UnityWebRequest.Result.Success)
            {
                return;
            }

            var detail = string.IsNullOrWhiteSpace(request.downloadHandler?.text)
                ? request.error
                : request.downloadHandler.text;
            throw new InvalidOperationException($"HTTP {request.responseCode}: {detail}");
        }

        private static string ResolveBackendUrl()
        {
            var configured = Environment.GetEnvironmentVariable("PDD_BACKEND_URL");
            var value = string.IsNullOrWhiteSpace(configured) ? DefaultBackendUrl : configured.Trim();
            return value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
        }

        private void OnDestroy()
        {
            _shutdown?.Cancel();
            _shutdown?.Dispose();
            _shutdown = null;
        }
    }
}
