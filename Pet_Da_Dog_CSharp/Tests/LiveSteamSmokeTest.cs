using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Steamworks;

// Opt-in integration test. The default run reads inventory only; grants must be
// explicitly requested and always use the same production click queue as the dog.
public partial class LiveSteamSmokeTest : Node
{
	private const int PetsItemDefId = 100;
	private SteamIntegration? _steam;
	private BackendPetClient? _backend;
	private CancellationTokenSource? _timeout;
	private Callback<SteamInventoryResultReady_t>? _inventoryCallback;
	private TaskCompletionSource<NativeInventorySnapshot>? _inventoryCompletion;
	private SteamInventoryResult_t? _inventoryHandle;
	private int _assertions;
	private int _grantCount;
	private bool _finished;
	private DesktopPet? _pet;

	private sealed record NativeInventorySnapshot(long Pets, uint Timestamp, int Items);
	private sealed class NativeInventoryException(EResult result) : Exception($"Native Steam inventory returned {result}.")
	{
		public EResult Result { get; } = result;
	}

	public override void _Ready() => CallDeferred(nameof(Run));

	public override void _Process(double delta)
	{
		if (_finished) return;
		_steam?.RunCallbacks();
		if (_timeout != null) _backend?.Tick(delta, _timeout.Token);
	}

	private async void Run()
	{
		var exitCode = 0;
		try
		{
			var assembly = typeof(LiveSteamSmokeTest).Assembly;
			GD.Print($"LIVE_SMOKE_TEST_BUILD: assembly={assembly.Location} module={assembly.ManifestModule.ModuleVersionId}");
			Require(System.Environment.GetEnvironmentVariable("PDD_LIVE_STEAM_SMOKE") == "1",
				"Set PDD_LIVE_STEAM_SMOKE=1 to explicitly enable real Steam and Worker requests.");
			Require(System.Environment.GetEnvironmentVariable("PDD_STEAM_APP_ID") == "4817200", "The live test requires Pet Da Dog AppID 4817200.");
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") != "1", "Steam must be enabled for the live test.");
			_grantCount = ReadNumber("PDD_LIVE_SMOKE_GRANTS", 0, 0, 3);
			var workerOnly = System.Environment.GetEnvironmentVariable("PDD_LIVE_SMOKE_WORKER_ONLY") == "1";
			var retryIdText = System.Environment.GetEnvironmentVariable("PDD_LIVE_SMOKE_RETRY_EVENT_ID");
			var retryExpectedText = System.Environment.GetEnvironmentVariable("PDD_LIVE_SMOKE_RETRY_EXPECTED_PETS");
			Guid? retryEventId = null;
			long? retryExpectedPets = null;
			if (!string.IsNullOrWhiteSpace(retryIdText))
			{
				Require(workerOnly && _grantCount == 1 && System.Environment.GetEnvironmentVariable("PDD_LIVE_SMOKE_DOG_INPUT") != "1",
					"Retrying an existing request requires WorkerOnly, GrantCount 1 and no new dog input.");
				Require(Guid.TryParse(retryIdText, out var retryId) && retryId != Guid.Empty, "RetryEventId must be the non-empty GUID from the original request.");
				Require(long.TryParse(retryExpectedText, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) && expected > 0,
					"RetryExpectedPets must be the positive expected total recorded for the original request.");
				retryEventId = retryId;
				retryExpectedPets = expected;
			}
			else Require(string.IsNullOrWhiteSpace(retryExpectedText), "RetryExpectedPets requires an existing RetryEventId.");
			var inputMode = retryEventId.HasValue ? "retry-existing" : System.Environment.GetEnvironmentVariable("PDD_LIVE_SMOKE_DOG_INPUT") == "1" ? "dog-alpha" : "queue";
			var timeoutSeconds = ReadNumber("PDD_LIVE_SMOKE_TIMEOUT_SECONDS", 150, 30, 600);
			var endpoint = System.Environment.GetEnvironmentVariable("PDD_BACKEND_URL");
			if (string.IsNullOrWhiteSpace(endpoint)) endpoint = ProjectSettings.GetSetting("pdd/backend_url").AsString();
			Require(Uri.TryCreate(endpoint, UriKind.Absolute, out var backendUri) && backendUri.Scheme == "https" &&
				backendUri.Host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase), "The live test requires the configured HTTPS Cloudflare Worker endpoint.");
			_timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
			if (System.Environment.GetEnvironmentVariable("PDD_LIVE_SMOKE_DOG_INPUT") == "1")
			{
				var settings = GetNode<PetSettings>("/root/PetSettings");
				settings.SetDogClickThrough(false);
				settings.SetDogTransparency(1);
				settings.DismissWelcome();
				_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>();
				AddChild(_pet);
				// Keep the dog stationary and let this test drive its existing connection.
				// Otherwise DesktopPet can auto-authenticate before the native baseline,
				// and the test would race its initial Worker authentication.
				_pet.SetProcess(false);
				_steam = ReadPetField<SteamIntegration>("_steam");
				_backend = ReadPetField<BackendPetClient>("_backend");
			}
			else
			{
				_steam = new SteamIntegration();
				_backend = new BackendPetClient();
			}
			Require(_steam.Initialize(), _steam.Status);
			Require(SteamUtils.GetAppID().m_AppId == 4817200, "Steam initialized with the wrong AppID.");
			_inventoryCallback = Callback<SteamInventoryResultReady_t>.Create(OnInventoryReady);
			GD.Print($"LIVE_STEAM_CONNECTED: appId=4817200 loggedOn={SteamUser.BLoggedOn()} grantsRequested={_grantCount} mode={(retryEventId.HasValue ? "retry-existing" : workerOnly ? "worker-only" : "native-verified")}");
			Require(SteamUser.BLoggedOn(), "The Steam account is offline.");
			using var baselineTimeout = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);
			if (workerOnly) baselineTimeout.CancelAfter(TimeSpan.FromSeconds(10));
			try
			{
				var nativeBeforeAuth = await ReadNativeInventoryAsync(baselineTimeout.Token);
				GD.Print($"LIVE_NATIVE_BEFORE_WORKER_AUTH: pets={nativeBeforeAuth.Pets} timestamp={nativeBeforeAuth.Timestamp} items={nativeBeforeAuth.Items}");
			}
			catch (OperationCanceledException) when (workerOnly && !_timeout.IsCancellationRequested)
			{
				GD.Print("LIVE_NATIVE_BASELINE_UNAVAILABLE: code=timeout; continuing Worker authentication diagnostics.");
			}
			catch (NativeInventoryException exception)
			{
				GD.Print($"LIVE_NATIVE_BASELINE_UNAVAILABLE: code={exception.Result}; continuing Worker authentication diagnostics.");
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				GD.Print($"LIVE_NATIVE_BASELINE_UNAVAILABLE: errorType={exception.GetType().Name}; continuing Worker authentication diagnostics.");
			}
			await _backend.AuthenticateAsync(_steam, _timeout.Token);
			Require(_backend.IsAuthenticated && _backend.ConfirmedPets.HasValue, $"Worker authentication or initial inventory failed: {_backend.Status}");
			Require(!_backend.HasPendingExchange, "An unresolved box exchange must be completed before validating exact Pet increments.");
			var initial = _backend.ConfirmedPets!.Value;
			GD.Print($"LIVE_WORKER_AUTHENTICATED: pets={initial}");
			if (retryExpectedPets.HasValue)
				Require(initial == retryExpectedPets.Value || initial == retryExpectedPets.Value - 1,
					"The current inventory must match the original expected total or its pre-grant total before retrying that request.");
			if (!workerOnly) await CompareNativeInventoryAsync("initial", initial);

			for (var refresh = 1; refresh <= 3; refresh++)
			{
				Require(await _backend.RefreshInventoryAsync(_timeout.Token), $"Worker inventory refresh failed: {_backend.Status}");
				Require(_backend.ConfirmedPets == initial, "Inventory changed during a read-only check; close other clients and avoid inventory mutations while testing.");
				Require(SumWorkerPets() == _backend.ConfirmedPets, "The Worker total differs from the sum of its Pet item stacks.");
				GD.Print($"LIVE_WORKER_REFRESH: number={refresh} pets={_backend.ConfirmedPets}");
				if (!workerOnly) await CompareNativeInventoryAsync($"refresh-{refresh}", initial);
			}
			if (_pet != null) VerifyNonGrantInput();

			for (var grant = 1; grant <= _grantCount; grant++)
			{
				var before = _backend.ConfirmedPets!.Value;
				var expectedAfter = retryExpectedPets ?? checked(before + 1);
				if (retryEventId.HasValue) _backend.EnqueuePetGrant(retryEventId.Value);
				else if (_pet == null) _backend.EnqueuePetGrant(Guid.NewGuid());
				else
				{
					using var click = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = FindVisibleDogPoint() };
					_pet._Input(click);
				}
				Require(_backend.ConfirmedPets == before && _backend.PendingGrantCount == 1, "A queued click must not alter Steam's confirmed total.");
				var pendingIds = typeof(BackendPetClient).GetField("_pendingGrantIds", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_backend) as Queue<Guid>;
				Require(pendingIds != null && pendingIds.TryPeek(out _), "The queued click must have a recoverable request GUID.");
				Require(!retryEventId.HasValue || pendingIds!.Peek() == retryEventId.Value, "A retry must use exactly the original request GUID.");
				GD.Print($"LIVE_PET_REQUESTED: number={grant} clientEventId={pendingIds!.Peek()} input={inputMode}");
				while (_backend.PendingGrantCount > 0 || _backend.IsOperationInFlight)
				{
					_timeout.Token.ThrowIfCancellationRequested();
					if (!_backend.IsAuthenticated) throw new InvalidOperationException($"The Worker session expired while granting: {_backend.Status}");
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				}
				Require(_backend.ConfirmedPets == expectedAfter, retryEventId.HasValue
					? "The existing request did not confirm its original expected Steam total."
					: "One confirmed click did not increase the Steam-authoritative total by exactly one.");
				Require(SumWorkerPets() == _backend.ConfirmedPets, "The confirmed grant total differs from its Pet item stacks.");
				GD.Print($"{(retryEventId.HasValue ? "LIVE_PET_RETRY_CONFIRMED" : "LIVE_PET_GRANTED")}: number={grant} input={inputMode} before={before} after={_backend.ConfirmedPets} pending={_backend.PendingGrantCount}");
				Require(await _backend.RefreshInventoryAsync(_timeout.Token) && _backend.ConfirmedPets == expectedAfter,
					"A fresh inventory read did not preserve the confirmed grant total.");
			}

			var final = _backend.ConfirmedPets!.Value;
			if (workerOnly)
			{
				for (var refresh = 1; refresh <= 3; refresh++)
				{
					Require(await _backend.RefreshInventoryAsync(_timeout.Token), $"Final Worker inventory refresh failed: {_backend.Status}");
					Require(_backend.ConfirmedPets == final && SumWorkerPets() == final,
						"A fresh final Worker inventory read changed the confirmed total or returned inconsistent Pet stacks.");
					GD.Print($"LIVE_WORKER_FINAL_REFRESH: number={refresh} pets={_backend.ConfirmedPets} stackPets={SumWorkerPets()} pending={_backend.PendingGrantCount}");
				}
			}
			else await CompareNativeInventoryAsync("final", final);
			Require(final == (retryExpectedPets ?? checked(initial + _grantCount)), "The final confirmed total differs from the requested or original expected total.");
			if (retryEventId.HasValue)
				GD.Print($"LIVE_WORKER_RETRY_PASS: assertions={_assertions} requests=1 clientEventId={retryEventId.Value} initial={initial} final={final} expected={retryExpectedPets} nativeVerified=false input=retry-existing");
			else if (workerOnly)
				GD.Print($"LIVE_WORKER_SMOKE_PASS: assertions={_assertions} grants={_grantCount} initial={initial} final={final} nativeVerified=false input={inputMode}");
			else GD.Print($"LIVE_STEAM_SMOKE_PASS: assertions={_assertions} grants={_grantCount} initial={initial} final={final}");
		}
		catch (OperationCanceledException)
		{
			exitCode = 1;
			GD.PushError($"LIVE_STEAM_SMOKE_FAIL: timed out; pending={_backend?.PendingGrantCount ?? 0}. A pending grant may already have reached Steam; refresh inventory before requesting another test.");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError($"LIVE_STEAM_SMOKE_FAIL: {exception.Message}");
			// Stack traces contain call sites, not auth tickets or session values.
			GD.Print($"LIVE_SMOKE_EXCEPTION_TRACE: type={exception.GetType().Name}\n{exception.StackTrace}");
		}
		finally
		{
			_finished = true;
			DisposeResources();
			GetTree().Quit(exitCode);
		}
	}

	private async Task CompareNativeInventoryAsync(string stage, long expected)
	{
		// Steam documents that GetAllItems can return cached results when called
		// frequently. Wait for convergence instead of confusing a stale SDK cache
		// with an incorrect Worker total. This performs only inventory reads.
		for (var attempt = 1; ; attempt++)
		{
			_timeout!.Token.ThrowIfCancellationRequested();
			var native = await ReadNativeInventoryAsync();
			GD.Print($"LIVE_NATIVE_INVENTORY: stage={stage} attempt={attempt} pets={native.Pets} workerPets={expected} timestamp={native.Timestamp} items={native.Items}");
			if (native.Pets == expected)
			{
				Require(await _backend!.RefreshInventoryAsync(_timeout.Token) && _backend.ConfirmedPets == expected,
					"Worker inventory changed during the independent Steam comparison.");
				Require(SumWorkerPets() == expected, "The Worker Pet stacks do not sum to the independent Steam total.");
				return;
			}
			await Task.Delay(TimeSpan.FromSeconds(10), _timeout.Token);
		}
	}

	private async Task<NativeInventorySnapshot> ReadNativeInventoryAsync(CancellationToken? cancellationToken = null)
	{
		var completion = new TaskCompletionSource<NativeInventorySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
		_inventoryCompletion = completion;
		try
		{
			Require(SteamInventory.GetAllItems(out var handle), "Steam could not request its native inventory.");
			_inventoryHandle = handle;
			return await completion.Task.WaitAsync(cancellationToken ?? _timeout!.Token);
		}
		finally
		{
			_inventoryCompletion = null;
			DestroyInventoryResult();
		}
	}

	private void OnInventoryReady(SteamInventoryResultReady_t response)
	{
		if (_inventoryCompletion == null || !_inventoryHandle.HasValue || !response.m_handle.Equals(_inventoryHandle.Value)) return;
		try
		{
			if (response.m_result != EResult.k_EResultOK) throw new NativeInventoryException(response.m_result);
			_assertions++;
			Require(SteamInventory.CheckResultSteamID(response.m_handle, SteamUser.GetSteamID()), "The native Steam inventory belongs to another account.");
			uint count = 0;
			var hasItems = SteamInventory.GetResultItems(response.m_handle, null, ref count);
			// Steam permits GetResultItems to report false for a successfully fetched
			// empty inventory. The ready callback and account check still succeeded.
			Require(hasItems || count == 0, "Steam could not read the native inventory size.");
			var items = new SteamItemDetails_t[checked((int)count)];
			if (count > 0) Require(SteamInventory.GetResultItems(response.m_handle, items, ref count), "Steam could not read the native inventory items.");
			long pets = 0;
			for (var index = 0; index < count; index++)
			{
				var item = items[index];
				if (item.m_iDefinition.m_SteamItemDef == PetsItemDefId && (item.m_unFlags & (ushort)ESteamItemFlags.k_ESteamItemRemoved) == 0)
					pets = checked(pets + item.m_unQuantity);
			}
			_inventoryCompletion.TrySetResult(new NativeInventorySnapshot(pets, SteamInventory.GetResultTimestamp(response.m_handle), checked((int)count)));
		}
		catch (Exception exception) { _inventoryCompletion.TrySetException(exception); }
	}

	private long SumWorkerPets() => _backend!.InventoryItems.Where(item => item.ItemDefId == PetsItemDefId).Sum(item => item.Quantity);

	private T ReadPetField<T>(string name) where T : class =>
		typeof(DesktopPet).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_pet) as T
		?? throw new InvalidOperationException($"The client test could not inspect DesktopPet's {name}.");

	private Vector2 FindVisibleDogPoint()
	{
		var sprite = _pet!.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		// GetImage may share the image retained by DesktopPet for alpha hits.
		// Dispose only our duplicate so later real input can reuse that image.
		using var image = (Image)sprite.Texture.GetImage().Duplicate();
		for (var y = image.GetHeight() / 4; y < image.GetHeight(); y++)
			for (var x = image.GetWidth() / 4; x < image.GetWidth(); x++)
				if (image.GetPixel(x, y).A > 0.5f)
				{
					var point = sprite.ToGlobal(new Vector2(x + 0.5f - image.GetWidth() * 0.5f, y + 0.5f - image.GetHeight() * 0.5f));
					Require(_pet.HitEditableDog(point), "The selected opaque texture pixel must pass the dog's real alpha hit check.");
					return point;
				}
		throw new InvalidOperationException("The dog texture has no opaque test pixel.");
	}

	private void VerifyNonGrantInput()
	{
		var before = _backend!.ConfirmedPets;
		using var transparentClick = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new Vector2(-1000, -1000) };
		_pet!._Input(transparentClick);
		using var releasedClick = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = FindVisibleDogPoint() };
		_pet._Input(releasedClick);
		Require(_backend.PendingGrantCount == 0 && _backend.ConfirmedPets == before, "Transparent space and mouse release must not enqueue a grant.");
		GD.Print("LIVE_DOG_INPUT_FILTER_PASS: transparentSpace=ignored releasedClick=ignored");
	}

	private void DestroyInventoryResult()
	{
		if (_inventoryHandle.HasValue && _steam?.IsInitialized == true) SteamInventory.DestroyResult(_inventoryHandle.Value);
		_inventoryHandle = null;
	}

	private void DisposeResources()
	{
		_timeout?.Cancel();
		_inventoryCompletion?.TrySetCanceled();
		DestroyInventoryResult();
		_inventoryCallback?.Dispose();
		_inventoryCallback = null;
		_pet?.SetProcess(false);
		_pet?.Free();
		_pet = null;
		_backend?.Dispose();
		_steam?.Dispose();
		_backend = null;
		_steam = null;
		_timeout?.Dispose();
		_timeout = null;
	}

	public override void _ExitTree() { _finished = true; DisposeResources(); }

	private static int ReadNumber(string name, int fallback, int minimum, int maximum)
	{
		var text = System.Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(text)) return fallback;
		if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
			throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
		return number;
	}

	private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _assertions++; }
}
