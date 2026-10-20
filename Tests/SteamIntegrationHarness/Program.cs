using Steamworks;

var checks = 0;
void Check(bool condition, string name)
{
	checks++;
	if (!condition) throw new InvalidOperationException(name);
}

var originalDirectory = Environment.CurrentDirectory;
var originalGlobalInput = Environment.GetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT");
var originalSteamFlag = Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM");
var originalAppId = Environment.GetEnvironmentVariable("PDD_STEAM_APP_ID");
var originalProcessAppId = Environment.GetEnvironmentVariable("SteamAppId");
var originalProcessGameId = Environment.GetEnvironmentVariable("SteamGameId");
var testDirectory = Path.Combine(Path.GetTempPath(), "PetDaDogSteamReconnect-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
try
{
	Environment.CurrentDirectory = testDirectory;
	Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", "1");
	Environment.SetEnvironmentVariable("PDD_STEAM_APP_ID", "4817200");
	Environment.SetEnvironmentVariable("PDD_DISABLE_STEAM", "1");
	using (var disabled = new SteamIntegration())
	{
		Check(!disabled.Initialize() && !disabled.TryInitialize(100), "An explicitly disabled client never starts Steam on initial or later frames.");
		Check(SteamAPI.InitializationCalls == 0 && disabled.Status.Contains("disabled"), "Disabling Steam avoids every native initialization attempt.");
	}

	Environment.SetEnvironmentVariable("PDD_DISABLE_STEAM", null);
	using (var steam = new SteamIntegration())
	{
		Check(!steam.Initialize() && SteamAPI.InitializationCalls == 1, "Launching before Steam is ready records the initial failure.");
		Check(!steam.TryInitialize(0) && !steam.TryInitialize(-1) && SteamAPI.InitializationCalls == 1,
			"The first backend frame and negative deltas cannot bypass the retry delay.");
		Check(!steam.TryInitialize(2) && !steam.TryInitialize(2.5) && SteamAPI.InitializationCalls == 1,
			"Unavailable Steam does not initialize once per frame.");
		Check(!steam.TryInitialize(0.5) && SteamAPI.InitializationCalls == 2, "The next attempt occurs after five accumulated seconds.");
		SteamAPI.Available = true;
		Check(!steam.TryInitialize(4) && SteamAPI.InitializationCalls == 2, "A repeated failure resets the complete delay.");
		Check(steam.TryInitialize(1) && steam.IsInitialized && SteamAPI.InitializationCalls == 3,
			"Starting Steam later reconnects the running game after its bounded delay.");
		Check(steam.SteamId == 76561198000000000 && steam.Status.StartsWith("Steam initialized"), "A successful retry supplies the Steam identity for backend authentication.");
		Check(steam.TryInitialize(100) && SteamAPI.InitializationCalls == 3, "An initialized client does not repeatedly initialize Steam.");
	}
	Check(SteamAPI.ShutdownCalls == 1, "A recovered connection shuts down normally.");

	SteamAPI.InitializationError = new InvalidOperationException("Fixture native runtime unavailable.");
	using (var throwing = new SteamIntegration())
	{
		Check(!throwing.Initialize() && SteamAPI.InitializationCalls == 4, "Initialization exceptions leave the client disconnected.");
		Check(!throwing.TryInitialize(4) && SteamAPI.InitializationCalls == 4, "Initialization exceptions use the same retry delay.");
		SteamAPI.InitializationError = null;
		Check(throwing.TryInitialize(1) && SteamAPI.InitializationCalls == 5, "An initialization exception does not permanently disable earning.");
	}

	Environment.SetEnvironmentVariable("PDD_STEAM_APP_ID", "invalid");
	using (var configured = new SteamIntegration())
	{
		Check(!configured.Initialize() && SteamAPI.InitializationCalls == 5, "Invalid AppIDs fail before reaching the SDK.");
		Environment.SetEnvironmentVariable("PDD_STEAM_APP_ID", "4817200");
		Check(!configured.TryInitialize(4) && SteamAPI.InitializationCalls == 5, "Configuration failures also respect the retry delay.");
		Check(configured.TryInitialize(1) && SteamAPI.InitializationCalls == 6, "Correcting configuration permits a later retry.");
	}
	await CommerceScenarios.RunAsync();
	Console.WriteLine($"STEAM_RECONNECT_HARNESS_PASS: {checks} reconnect checks plus commerce scenarios; no native Steam, input hooks, network calls or grants.");
}
finally
{
	Environment.CurrentDirectory = originalDirectory;
	Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", originalGlobalInput);
	Environment.SetEnvironmentVariable("PDD_DISABLE_STEAM", originalSteamFlag);
	Environment.SetEnvironmentVariable("PDD_STEAM_APP_ID", originalAppId);
	Environment.SetEnvironmentVariable("SteamAppId", originalProcessAppId);
	Environment.SetEnvironmentVariable("SteamGameId", originalProcessGameId);
	File.Delete(Path.Combine(testDirectory, "steam_appid.txt"));
	Directory.Delete(testDirectory);
}
