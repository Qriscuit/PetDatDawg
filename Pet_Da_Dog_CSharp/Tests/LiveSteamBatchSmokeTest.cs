using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Steamworks;
using HttpClient = System.Net.Http.HttpClient;
using Environment = System.Environment;
using FileAccess = System.IO.FileAccess;

// Explicit, bounded real-Steam verification. This scene never loads DesktopPet,
// BackendPetClient, or Shop code. Only its two saved grant batches may add Pets.
public partial class LiveSteamBatchSmokeTest : Node
{
	private const int MaximumNewPets = 302;
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private SteamIntegration? _steam;
	private HttpClient? _http;
	private CancellationTokenSource? _timeout;
	private Callback<SteamInventoryResultReady_t>? _inventoryCallback;
	private TaskCompletionSource<long>? _nativeCompletion;
	private SteamInventoryResult_t? _nativeHandle;
	private bool _finished;
	private int _assertions;
	private string _planPath = "";
	private BatchPlan? _plan;

	public sealed record BatchRequest(Guid ClientEventId, Guid RunId, long FirstSequence, int Count);
	public sealed record AttemptPlan(BatchRequest Request, BatchRequest OverlapProbe)
	{
		public bool Started { get; set; }
		public bool Confirmed { get; set; }
		public bool DuplicateVerified { get; set; }
		public bool ConflictVerified { get; set; }
		public bool OverlapVerified { get; set; }
		public bool FreshInventoryVerified { get; set; }
	}
	public sealed record BatchPlan(int Version, string BackendUrl, string AccountHash, long BaselinePets, AttemptPlan[] Attempts)
	{
		public long? NativeBaselinePets { get; set; }
		public bool Completed { get; set; }
		public string? LastVerifiedStep { get; set; }
		public string? DeploymentUrl { get; set; }
		public bool DeploymentVerified { get; set; }
	}
	private sealed record PreparedBatches(int Version, AttemptPlan[] Attempts);
	private sealed record AuthResponse(string SessionToken, string SteamId, int? MaxGrantBatchSize);
	private sealed record InventoryResponse(long? Pets, SteamInventoryItem[]? Items, JsonElement PendingOperation);
	private sealed record GrantResponse(long? Pets, SteamInventoryItem[]? Items, int Granted, bool? Replayed);
	private sealed record ErrorResponse(string? Code, string? SteamEndpoint = null, string? SteamFailureCategory = null,
		int? SteamHttpStatus = null, JsonElement SteamResult = default, JsonElement Replayed = default, JsonElement ReceiptShape = default);
	private sealed class VerificationFailure(string message) : Exception(message);

	public override void _Ready() => CallDeferred(nameof(Run));
	public override void _Process(double delta) { if (!_finished) _steam?.RunCallbacks(); }

	private async void Run()
	{
		var exitCode = 0;
		try
		{
			Require(Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_SMOKE") == "1", "Explicit live batch test opt-in is required.");
			Require(Environment.GetEnvironmentVariable("PDD_STEAM_APP_ID") == "4817200", "The batch test requires AppID 4817200.");
			Require(Environment.GetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT") == "1" && Environment.GetEnvironmentVariable("PDD_DISABLE_TRAY") == "1", "Global input and the tray must remain disabled.");
			var endpoint = Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_URL") ?? "";
			Require(Uri.TryCreate(endpoint, UriKind.Absolute, out var backend) && ((backend.Scheme == "https" && backend.Host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase))
				|| (backend.Scheme == "http" && backend.IsLoopback && Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_ALLOW_LOOPBACK") == "1"))
				&& backend.AbsolutePath == "/" && string.IsNullOrEmpty(backend.Query) && string.IsNullOrEmpty(backend.Fragment), "An HTTPS workers.dev root endpoint is required.");
			var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_PLAN_DIRECTORY") ?? "");
			var ignoredRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot")) + Path.DirectorySeparatorChar;
			Require(directory.StartsWith(ignoredRoot, StringComparison.OrdinalIgnoreCase), "The saved test plan must be inside this project's ignored .godot directory.");
			Directory.CreateDirectory(directory);
			_planPath = Path.Combine(directory, "batch-plan.json");
			var resume = Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_RESUME") == "1";
			var verifyDeployment = Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_VERIFY_DEPLOYMENT") == "1";
			Require(resume == File.Exists(_planPath), "Use Resume for an existing plan; use a new directory only for a separately authorized test.");
			Require(!verifyDeployment || (resume && backend!.Scheme == "https" && backend.Host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase)), "Deployment verification requires a saved plan and an HTTPS workers.dev endpoint.");
			var seconds = int.Parse(Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_TIMEOUT_SECONDS") ?? "300", CultureInfo.InvariantCulture);
			Require(seconds is >= 60 and <= 900, "Test timeout must be between 60 and 900 seconds.");
			_timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
			_http = new HttpClient { BaseAddress = backend, Timeout = TimeSpan.FromSeconds(20) };
			_steam = new SteamIntegration();
			Require(_steam.Initialize() && SteamUser.BLoggedOn() && SteamUtils.GetAppID().m_AppId == 4817200, "Real Steam initialization or account sign-in failed.");
			_inventoryCallback = Callback<SteamInventoryResultReady_t>.Create(OnInventoryReady);
			var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_steam.SteamId.ToString(CultureInfo.InvariantCulture)))).ToLowerInvariant();
			if (resume)
			{
				_plan = JsonSerializer.Deserialize<BatchPlan>(File.ReadAllText(_planPath), Json);
				ValidatePlan(_plan, verifyDeployment ? _plan!.BackendUrl : backend!.AbsoluteUri, fingerprint);
			}
			await AuthenticateAsync();
			var current = await ReadInventoryAsync();
			GD.Print($"LIVE_BATCH_RESUME_INVENTORY: current={current} resume={resume} deploymentVerification={verifyDeployment}");
			if (!resume)
			{
				var preparedPath = Path.Combine(directory, "prepared-batches.json");
				Require(File.Exists(preparedPath), "Prepare the two frozen batch IDs before launching real Steam verification.");
				var prepared = JsonSerializer.Deserialize<PreparedBatches>(File.ReadAllText(preparedPath), Json);
				Require(prepared?.Version == 1 && prepared.Attempts != null, "The prepared batch file is invalid.");
				_plan = new BatchPlan(1, backend!.AbsoluteUri, fingerprint, current, prepared!.Attempts!);
				ValidatePlan(_plan, backend.AbsoluteUri, fingerprint);
				_plan.NativeBaselinePets = await TryReadNativeAsync();
				if (Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_REQUIRE_NATIVE") == "1")
					Require(_plan.NativeBaselinePets == current, "Native Steam inventory must match the baseline before any grants.");
				// Both immutable payloads, probe IDs and the baseline reach disk before
				// the first grant. A timeout must resume this file, never a fresh run.
				SavePlan("baseline_saved");
			}
			Require(current >= _plan!.BaselinePets && current <= checked(_plan.BaselinePets + MaximumNewPets), "Inventory is outside this saved test's bounded baseline; stop and investigate other clients.");
			GD.Print($"LIVE_BATCH_PLAN: accountHash={fingerprint} baseline={_plan.BaselinePets} intendedNewPets={MaximumNewPets} resume={resume} nativeBaselineVerified={_plan.NativeBaselinePets == _plan.BaselinePets}");
			if (verifyDeployment)
			{
				Require(_plan.Completed && current == checked(_plan.BaselinePets + MaximumNewPets), "Complete the saved preflight before deployment replay; its final total must remain unchanged.");
				Require(_plan.DeploymentUrl == null || _plan.DeploymentUrl == backend!.AbsoluteUri, "A deployment verification attempt already belongs to another endpoint.");
				_plan.DeploymentUrl = backend!.AbsoluteUri; SavePlan("deployment_before_replay");
				await VerifyDeploymentAsync();
				_plan.DeploymentVerified = true; SavePlan("deployment_verified");
				GD.Print($"LIVE_BATCH_DEPLOYMENT_PASS: assertions={_assertions} total={current} newGrants=0 sameBatchIds=true purchases=0 recycle=0 acknowledgments=0");
			}
			else if (_plan.Completed)
			{
				Require(current == checked(_plan.BaselinePets + MaximumNewPets), "Completed plan inventory has changed; no grants were sent.");
				GD.Print($"LIVE_BATCH_SMOKE_PASS: assertions={_assertions} initial={_plan.BaselinePets} final={current} intendedGrants=302 resumedCompleted=true purchases=0 recycle=0 acknowledgments=0");
			}
			else
			{
				for (var index = 0; index < _plan.Attempts.Length; index++) await VerifyAttemptAsync(index);
				var final = await ReadInventoryAsync();
				Require(final == checked(_plan.BaselinePets + MaximumNewPets), "The final inventory differs from the exact 302-Pet test delta.");
				_plan.Completed = true; SavePlan("complete");
				GD.Print($"LIVE_BATCH_SMOKE_PASS: assertions={_assertions} initial={_plan.BaselinePets} final={final} intendedGrants=302 purchases=0 recycle=0 acknowledgments=0");
			}
		}
		catch (Exception exception)
		{
			exitCode = 1;
			var reason = exception is VerificationFailure ? exception.Message : exception is OperationCanceledException ? "The bounded test timed out or was canceled." : $"The test stopped with {exception.GetType().Name}.";
			GD.PushError($"LIVE_BATCH_SMOKE_FAIL: {reason} Resume the existing saved plan before any further grant test.");
		}
		finally { _finished = true; DisposeResources(); GetTree().Quit(exitCode); }
	}

	private async Task VerifyDeploymentAsync()
	{
		var expected = checked(_plan!.BaselinePets + MaximumNewPets);
		for (var index = 0; index < _plan.Attempts.Length; index++)
		{
			var attempt = _plan.Attempts[index];
			if (index == 1) await WaitForNextMinuteAsync();
			SavePlan($"deployment_batch_{index}_before_replay");
			var replay = await SendBatchAsync(attempt.Request);
			Require(replay.Replayed == true && replay.Granted == attempt.Request.Count && ValidateInventory(replay.Pets, replay.Items) == expected, "Deployment replay did not confirm the original Steam request without additional Pets.");
			var duplicate = await SendBatchAsync(attempt.Request);
			Require(duplicate.Replayed == true && duplicate.Granted == attempt.Request.Count && ValidateInventory(duplicate.Pets, duplicate.Items) == expected, "The deployed Worker's duplicate replay changed the confirmed inventory.");
			await RequireRejectedAsync(attempt.Request with { Count = attempt.Request.Count - 1 }, "operation_conflict");
			await RequireRejectedAsync(attempt.OverlapProbe, "sequence_conflict");
			Require(await ReadInventoryAsync() == expected, "A deployment conflict probe changed confirmed inventory.");
			SavePlan($"deployment_batch_{index}_verified");
			GD.Print($"LIVE_BATCH_DEPLOYMENT_REPLAY: count={attempt.Request.Count} total={expected} replayed=true newPets=0 conflictsRejected=true");
		}
	}

	private async Task AuthenticateAsync()
	{
		var ticket = await _steam!.RequestBackendTicketAsync(_timeout!.Token);
		using var response = await _http!.PostAsJsonAsync("v1/auth/steam", new { ticketHex = ticket, identity = SteamIntegration.BackendIdentity }, _timeout.Token);
		Require(response.IsSuccessStatusCode, $"Steam authentication returned HTTP {(int)response.StatusCode}.");
		var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: _timeout.Token);
		Require(auth != null && !string.IsNullOrWhiteSpace(auth.SessionToken) && auth.SteamId == _steam.SteamId.ToString(CultureInfo.InvariantCulture), "The authenticated Steam account did not match this process.");
		Require(auth!.MaxGrantBatchSize == 300, "The candidate must advertise a 300-Pet batch maximum for this authorized test.");
		_http!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.SessionToken);
	}

	private async Task<long> ReadInventoryAsync()
	{
		using var response = await _http!.GetAsync("v1/inventory", _timeout!.Token);
		Require(response.IsSuccessStatusCode, $"Inventory read returned HTTP {(int)response.StatusCode}; no replacement grant was generated.");
		var inventory = await response.Content.ReadFromJsonAsync<InventoryResponse>(cancellationToken: _timeout.Token);
		Require(inventory?.PendingOperation.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array), "An existing pending exchange requires separate recovery; this test will not resume it.");
		return ValidateInventory(inventory?.Pets, inventory?.Items);
	}

	private async Task VerifyAttemptAsync(int index)
	{
		var attempt = _plan!.Attempts[index];
		if (attempt.FreshInventoryVerified) return;
		var expected = checked(_plan.BaselinePets + (index == 0 ? 2 : MaximumNewPets));
		var before = checked(expected - attempt.Request.Count);
		var current = await ReadInventoryAsync();
		Require(current == before || (attempt.Started && current == expected), "Inventory does not match this frozen attempt's before/after totals; no new grant will be issued.");
		if (index == 1 && !attempt.Started)
		{
			// The first two Pets used the shared minute budget. Start the 300-Pet
			// request in a fresh UTC minute, still honoring an actual server 429.
			await WaitForNextMinuteAsync();
		}
		if (!attempt.Confirmed)
		{
			attempt.Started = true; SavePlan($"batch_{index}_before_send");
			var grant = await SendBatchAsync(attempt.Request);
			Require(ValidateInventory(grant.Pets, grant.Items) == expected, "The confirmed batch did not produce its exact expected delta.");
			Require(grant.Granted == attempt.Request.Count && grant.Replayed.HasValue, "The batch receipt does not confirm the frozen count.");
			attempt.Confirmed = true; SavePlan($"batch_{index}_confirmed");
			GD.Print($"LIVE_BATCH_CONFIRMED: count={attempt.Request.Count} total={expected} replayed={grant.Replayed}");
		}
		if (!attempt.DuplicateVerified)
		{
			SavePlan($"batch_{index}_before_duplicate");
			var replay = await SendBatchAsync(attempt.Request);
			Require(replay.Replayed == true && replay.Granted == attempt.Request.Count && ValidateInventory(replay.Pets, replay.Items) == expected, "A duplicate batch did not replay exactly once.");
			attempt.DuplicateVerified = true; SavePlan($"batch_{index}_duplicate_verified");
		}
		var transportProbe = Environment.GetEnvironmentVariable("PDD_LIVE_BATCH_TRANSPORT_PROBE") == "1";
		if (!transportProbe && !attempt.ConflictVerified)
		{
			SavePlan($"batch_{index}_before_conflict_probe");
			await RequireRejectedAsync(attempt.Request with { Count = attempt.Request.Count - 1 }, "operation_conflict");
			attempt.ConflictVerified = true; SavePlan($"batch_{index}_conflict_verified");
		}
		if (!transportProbe && !attempt.OverlapVerified)
		{
			SavePlan($"batch_{index}_before_overlap_probe");
			await RequireRejectedAsync(attempt.OverlapProbe, "sequence_conflict");
			attempt.OverlapVerified = true; SavePlan($"batch_{index}_overlap_verified");
		}
		Require(await ReadInventoryAsync() == expected, "A fresh inventory read changed after duplicate/conflict probes.");
		attempt.FreshInventoryVerified = true; SavePlan($"batch_{index}_fresh_inventory_verified");
		GD.Print($"LIVE_BATCH_VERIFIED: count={attempt.Request.Count} total={expected} duplicate=true alteredPayloadRejected={attempt.ConflictVerified} overlapRejected={attempt.OverlapVerified} transportProbe={transportProbe}");
	}

	private async Task WaitForNextMinuteAsync()
	{
		var now = DateTimeOffset.UtcNow;
		var next = now.AddMinutes(1).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond).AddSeconds(1);
		GD.Print("LIVE_BATCH_WAIT: reason=separate_shared_budget_minute");
		await Task.Delay(next - now, _timeout!.Token);
	}

	private async Task<GrantResponse> SendBatchAsync(BatchRequest request)
	{
		for (var retry = 0; retry < 3; retry++)
		{
			using var response = await _http!.PostAsJsonAsync("v1/pets/grant-batch", request, _timeout!.Token);
			if (response.StatusCode == HttpStatusCode.TooManyRequests)
			{
				var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is DateTimeOffset at ? at - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(60));
				Require(delay.TotalSeconds is >= 0 and <= 90, "Rate-limit retry interval exceeded the test's bounded minute wait.");
				GD.Print("LIVE_BATCH_WAIT: reason=server_rate_limit retryPayload=unchanged");
				await Task.Delay(delay + TimeSpan.FromSeconds(1), _timeout.Token);
				continue;
			}
			if (!response.IsSuccessStatusCode) await LogSafeFailureAsync(response);
			Require(response.IsSuccessStatusCode, $"Frozen batch returned HTTP {(int)response.StatusCode}; resume the same plan before retrying.");
			return await response.Content.ReadFromJsonAsync<GrantResponse>(cancellationToken: _timeout.Token)
				?? throw new VerificationFailure("The frozen batch returned an empty receipt.");
		}
		throw new VerificationFailure("The frozen batch remained rate limited after bounded retries.");
	}

	private async Task LogSafeFailureAsync(HttpResponseMessage response)
	{
		ErrorResponse? error = null;
		try { error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: _timeout!.Token); }
		catch (JsonException) { }
		catch (NotSupportedException) { }
		var code = error?.Code is "steam_unavailable" or "steam_access_denied" or "service_unavailable" or "storage_quota_exceeded"
			or "operation_conflict" or "sequence_conflict" or "invalid_request" or "rate_limited" or "configuration_required" ? error.Code : "unknown";
		var endpoint = error?.SteamEndpoint is "ISteamUserAuth/AuthenticateUserTicket" or "ISteamUser/CheckAppOwnership"
			or "IInventoryService/GetInventory" or "IInventoryService/AddItem" or "IInventoryService/ExchangeItem" ? error.SteamEndpoint : "unknown";
		var category = error?.SteamFailureCategory is "item_properties_error" or "item_definition_error" or "inventory_disabled" or "permission_denied"
			or "invalid_parameters" or "steam_rejected" or "missing_response" or "invalid_success_flag" or "missing_item_json" or "invalid_item_json"
			or "invalid_item_data" or "invalid_response_json" or "invalid_response_document" or "request_timeout" or "connection_failed"
			or "missing_pets_receipt" or "invalid_replayed_flag" ? error.SteamFailureCategory : "unknown";
		var status = error?.SteamHttpStatus is >= 100 and <= 599 ? error.SteamHttpStatus.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
		var result = error?.SteamResult.ValueKind == JsonValueKind.Number && error.SteamResult.TryGetInt32(out var value) && value is >= 0 and <= 65535 ? value.ToString(CultureInfo.InvariantCulture) : "unknown";
		var replayed = error?.Replayed.ValueKind is JsonValueKind.True or JsonValueKind.False ? error.Replayed.GetBoolean().ToString() : "unknown";
		GD.Print($"LIVE_BATCH_HTTP_FAILURE: http={(int)response.StatusCode} code={code} steamEndpoint={endpoint} steamHttpStatus={status} category={category} steamResult={result} replayed={replayed}");
		if (error?.ReceiptShape.ValueKind == JsonValueKind.Object)
		{
			var shape = error.ReceiptShape;
			var keys = shape.TryGetProperty("responseKeys", out var responseKeys) && responseKeys.ValueKind == JsonValueKind.Array
				? string.Join(",", responseKeys.EnumerateArray().Take(8).Where(key => key.ValueKind == JsonValueKind.String && key.GetString() is "success" or "replayed" or "item_json" or "error").Select(key => key.GetString())) : "unknown";
			var quantityFirst = "unknown"; var quantityLast = "unknown";
			if (shape.TryGetProperty("quantities", out var quantities) && quantities.ValueKind == JsonValueKind.Array && quantities.GetArrayLength() is > 0 and <= 300)
			{
				quantityFirst = SafeNumeric(quantities[0], 4294967295);
				quantityLast = SafeNumeric(quantities[quantities.GetArrayLength() - 1], 4294967295);
			}
			var itemDefs = shape.TryGetProperty("itemDefIds", out var definitions) && definitions.ValueKind == JsonValueKind.Array
				? string.Join(",", definitions.EnumerateArray().Take(300).Select(value => SafeNumeric(value, int.MaxValue)).Distinct()) : "unknown";
			GD.Print($"LIVE_BATCH_RECEIPT_SHAPE: http={SafeShapeNumber(shape, "httpStatus", 599)} steamResult={SafeShapeNumber(shape, "steamResult", 65535)} responseKeys={keys} success={SafeShapeBoolean(shape, "success")} replayed={SafeShapeBoolean(shape, "replayed")} rowCount={SafeShapeNumber(shape, "rowCount", 300)} distinctItemIds={SafeShapeNumber(shape, "distinctItemIds", 300)} itemDefIds={itemDefs} quantityFirst={quantityFirst} quantityLast={quantityLast}");
		}
	}

	private static string SafeNumeric(JsonElement value, long maximum)
	{
		long number;
		if ((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number))
			|| (value.ValueKind == JsonValueKind.String && value.GetString() is string text && text.Length <= 10 && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)))
			return number >= 0 && number <= maximum ? number.ToString(CultureInfo.InvariantCulture) : "unknown";
		return "unknown";
	}
	private static string SafeShapeNumber(JsonElement shape, string property, long maximum) => shape.TryGetProperty(property, out var value) ? SafeNumeric(value, maximum) : "unknown";
	private static string SafeShapeBoolean(JsonElement shape, string property) => shape.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean().ToString() : "unknown";

	private async Task RequireRejectedAsync(BatchRequest probe, string expectedCode)
	{
		using var response = await _http!.PostAsJsonAsync("v1/pets/grant-batch", probe, _timeout!.Token);
		var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: _timeout.Token);
		Require(response.StatusCode == HttpStatusCode.Conflict && error?.Code == expectedCode, "A batch conflict probe was not rejected with the expected fixed code; stop before additional grants.");
	}

	private long ValidateInventory(long? pets, SteamInventoryItem[]? items)
	{
		Require(pets is >= 0 && items != null && items.All(item => item.Quantity >= 0), "The server returned no valid confirmed inventory.");
		Require(items!.Where(item => item.ItemDefId == 100).Sum(item => item.Quantity) == pets, "The confirmed Pets total differs from Steam item stacks.");
		return pets!.Value;
	}

	private void ValidatePlan(BatchPlan? plan, string endpoint, string accountHash)
	{
		Require(plan != null && plan.Version == 1 && plan.BackendUrl == endpoint && plan.AccountHash == accountHash && plan.BaselinePets is >= 0 and <= long.MaxValue - MaximumNewPets, "The saved plan does not match this endpoint/account or has an invalid baseline.");
		Require(plan!.Attempts is { Length: 2 }, "The plan must contain exactly the two originally authorized batches.");
		var first = plan.Attempts[0]; var second = plan.Attempts[1];
		Require(first.Request.ClientEventId != Guid.Empty && first.Request.RunId != Guid.Empty && first.Request.Count == 2 && first.Request.FirstSequence == 1
			&& second.Request.ClientEventId != Guid.Empty && second.Request.Count == 300 && second.Request.FirstSequence == 3 && second.Request.RunId == first.Request.RunId
			&& second.Request.ClientEventId != first.Request.ClientEventId, "The saved batch payloads are invalid; never replace them with new IDs.");
		foreach (var attempt in plan.Attempts)
			Require(attempt.OverlapProbe.ClientEventId != Guid.Empty && attempt.OverlapProbe.ClientEventId != attempt.Request.ClientEventId && attempt.OverlapProbe.RunId == first.Request.RunId
				&& attempt.OverlapProbe.FirstSequence == attempt.Request.FirstSequence && attempt.OverlapProbe.Count == 1, "The saved rejection probe is invalid.");
	}

	private void SavePlan(string step)
	{
		_plan!.LastVerifiedStep = step;
		var temporary = _planPath + ".tmp";
		using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
		{
			JsonSerializer.Serialize(stream, _plan, Json); stream.Flush(flushToDisk: true);
		}
		File.Move(temporary, _planPath, overwrite: true);
	}

	private async Task<long?> TryReadNativeAsync()
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(_timeout!.Token);
		limit.CancelAfter(TimeSpan.FromSeconds(10));
		_nativeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			Require(_steam!.TryRequestInventorySnapshot(out var handle), "Native inventory request could not start.");
			_nativeHandle = handle;
			return await _nativeCompletion.Task.WaitAsync(limit.Token);
		}
		catch { GD.Print("LIVE_BATCH_NATIVE_BASELINE: unavailable=true"); return null; }
		finally { _nativeCompletion = null; ReleaseNative(); }
	}

	private void OnInventoryReady(SteamInventoryResultReady_t result)
	{
		if (_nativeCompletion == null || !_nativeHandle.HasValue || !_nativeHandle.Value.Equals(result.m_handle)) return;
		try
		{
			Require(result.m_result == EResult.k_EResultOK && SteamInventory.CheckResultSteamID(result.m_handle, SteamUser.GetSteamID()), "Native inventory was unconfirmed or belonged to another account.");
			uint count = 0;
			Require(SteamInventory.GetResultItems(result.m_handle, null, ref count) || count == 0, "Native inventory size could not be read.");
			var items = new SteamItemDetails_t[checked((int)count)];
			Require(count == 0 || SteamInventory.GetResultItems(result.m_handle, items, ref count), "Native inventory items could not be read.");
			_nativeCompletion.TrySetResult(items.Where(item => item.m_iDefinition.m_SteamItemDef == 100 && (item.m_unFlags & (ushort)ESteamItemFlags.k_ESteamItemRemoved) == 0).Sum(item => (long)item.m_unQuantity));
		}
		catch (Exception exception) { _nativeCompletion.TrySetException(exception); }
	}

	private void ReleaseNative() { if (_nativeHandle.HasValue) _steam?.ReleaseInventorySnapshot(_nativeHandle.Value); _nativeHandle = null; }
	private void Require(bool condition, string message) { if (!condition) throw new VerificationFailure(message); _assertions++; }
	private void DisposeResources()
	{
		_timeout?.Cancel(); _nativeCompletion?.TrySetCanceled(); ReleaseNative();
		_inventoryCallback?.Dispose(); _inventoryCallback = null;
		_http?.Dispose(); _http = null; _steam?.Dispose(); _steam = null;
		_timeout?.Dispose(); _timeout = null;
	}
	public override void _ExitTree() { _finished = true; DisposeResources(); }
}
