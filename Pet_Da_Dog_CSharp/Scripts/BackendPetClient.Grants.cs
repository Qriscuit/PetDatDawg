using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public sealed partial class BackendPetClient
{
	private const double GrantFlushIntervalSeconds = 30;
	private Guid _grantRunId = Guid.NewGuid();
	private long _lastEnqueuedGrantSequence;
	private long _confirmedGrantSequence;
	private int _maxGrantBatchSize = 300;
	private GrantBatchRequest? _pendingGrantBatch;
	private bool _grantRequestInFlight;
	private double _grantFlushDelay = GrantFlushIntervalSeconds;
	private double _grantRetryDelay;
	private int _grantFailureCount;

	private readonly record struct GrantPrefix(Guid RunId, long LastSequence);
	private sealed record GrantBatchRequest(Guid ClientEventId, Guid RunId, long FirstSequence, int Count);
	private sealed record GrantBatchResponse(long? Pets, int Granted, bool? Replayed, SteamInventoryItem[]? Items);

	private GrantPrefix CaptureGrantPrefix() => new(_grantRunId, _lastEnqueuedGrantSequence);

	private async Task FlushBackgroundGrantBatchAsync(CancellationToken cancellationToken)
	{
		if (_grantRequestInFlight) return;
		_grantRequestInFlight = true;
		var acquired = false;
		try
		{
			await _requests.WaitAsync(cancellationToken);
			acquired = true; _requestInFlight = true;
			if (!_disposed && IsAuthenticated && !HasPendingExchange && !_shopForegroundReserved)
				await SendGrantBatchUnderLockAsync(CaptureGrantPrefix(), cancellationToken);
		}
		catch (OperationCanceledException) { }
		finally
		{
			if (acquired) { _requestInFlight = false; _requests.Release(); }
			_grantRequestInFlight = false;
		}
	}

	// The caller owns _requests. New input can append to the queue while HTTP is
	// pending, but this captured boundary can never grow with it.
	private async Task<bool> SettleGrantPrefixUnderLockAsync(GrantPrefix prefix, CancellationToken cancellationToken)
	{
		if (_disposed || !IsAuthenticated || prefix.RunId != _grantRunId) return false;
		while (_confirmedGrantSequence < prefix.LastSequence)
		{
			if (HasPendingExchange || !await SendGrantBatchUnderLockAsync(prefix, cancellationToken)) return false;
		}
		return true;
	}

	private async Task<bool> SendGrantBatchUnderLockAsync(GrantPrefix prefix, CancellationToken cancellationToken)
	{
		if (_disposed || !IsAuthenticated || HasPendingExchange || prefix.RunId != _grantRunId) return false;
		if (_grantRetryDelay > 0)
		{
			Status = "Pet syncing is waiting before retrying.";
			return false;
		}
		if (_confirmedGrantSequence >= prefix.LastSequence) return true;
		_pendingGrantBatch ??= new GrantBatchRequest(Guid.NewGuid(), _grantRunId, _confirmedGrantSequence + 1,
			(int)Math.Min(_maxGrantBatchSize, prefix.LastSequence - _confirmedGrantSequence));
		var batch = _pendingGrantBatch;
		TimeSpan? retryAfter = null;
		try
		{
			// Never replace, shrink or expand this body after an uncertain response,
			// including a 401, 404 or rate limit. Its Steam request ID is immutable.
			using var response = await _httpClient.PostAsJsonAsync("v1/pets/grant-batch", batch, cancellationToken);
			retryAfter = ReadGrantRetryAfter(response);
			await RequireSuccessAsync(response, cancellationToken);
			var grant = await response.Content.ReadFromJsonAsync<GrantBatchResponse>(cancellationToken: cancellationToken);
			if (grant?.Pets is not >= 0 || grant.Items == null || grant.Granted != batch.Count || !grant.Replayed.HasValue)
				throw new InvalidOperationException("Backend returned no confirmed grant batch receipt.");
			if (batch.RunId != _grantRunId || _pendingGrantIds.Count < batch.Count)
				throw new InvalidOperationException("The queued grant batch changed before confirmation.");
			ApplyInventory(grant.Pets, grant.Items);
			for (var index = 0; index < batch.Count; index++) _pendingGrantIds.Dequeue();
			_confirmedGrantSequence = checked(_confirmedGrantSequence + batch.Count);
			_pendingGrantBatch = null;
			_grantFailureCount = 0; _grantRetryDelay = 0; _grantFlushDelay = GrantFlushIntervalSeconds;
			Status = $"Pets synced: {ConfirmedPets}.";
			return true;
		}
		catch (OperationCanceledException)
		{
			ScheduleGrantRetry(retryAfter);
			Status = "Pet syncing is waiting before retrying.";
		}
		catch (Exception exception)
		{
			ScheduleGrantRetry(retryAfter);
			Status = IsAuthenticated ? $"Pet syncing failed; retry scheduled: {exception.Message}" : "Steam session expired; reconnecting before syncing Pets.";
			GD.PushWarning(Status);
		}
		return false;
	}

	private TimeSpan? ReadGrantRetryAfter(HttpResponseMessage response)
	{
		var header = response.Headers.RetryAfter;
		var delay = header?.Delta ?? (header?.Date is DateTimeOffset date ? date - _timeProvider.GetUtcNow() : null);
		return delay is TimeSpan value && value > TimeSpan.Zero ? value : null;
	}

	private void ScheduleGrantRetry(TimeSpan? retryAfter)
	{
		_grantFailureCount = Math.Min(_grantFailureCount + 1, 7);
		var delay = Math.Min(300, 5 * Math.Pow(2, _grantFailureCount - 1));
		// Positive, bounded jitter also spreads clients recovering at the same
		// server deadline. It can only lengthen a supplied Retry-After interval.
		_grantRetryDelay = Math.Max(delay, retryAfter?.TotalSeconds ?? 0)
			+ Random.Shared.NextDouble() * Math.Min(30, delay * 0.2);
	}

	private void ResetGrantAccount()
	{
		_pendingGrantIds.Clear(); _pendingGrantBatch = null;
		_grantRunId = Guid.NewGuid(); _lastEnqueuedGrantSequence = 0; _confirmedGrantSequence = 0;
		_grantFailureCount = 0; _grantRetryDelay = 0; _grantFlushDelay = GrantFlushIntervalSeconds;
	}
}
