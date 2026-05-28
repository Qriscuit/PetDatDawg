using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Steamworks;

public sealed class SteamIntegration : IDisposable
{
	public const string BackendIdentity = "petdadog-backend";

	private const uint DefaultDevelopmentAppId = 480;
	private const double AuthTicketTimeoutSeconds = 15.0;

	private Callback<GetTicketForWebApiResponse_t>? _webApiTicketCallback;
	private TaskCompletionSource<string>? _ticketCompletion;
	private HAuthTicket? _activeTicket;
	private bool _initialized;

	public bool IsInitialized => _initialized;
	public string Status { get; private set; } = "Steam is not initialized.";
	public ulong SteamId { get; private set; }

	public bool Initialize()
	{
		if (System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1")
		{
			Status = "Steam is disabled by PDD_DISABLE_STEAM.";
			return false;
		}

		if (_initialized)
		{
			return true;
		}

		try
		{
			var appId = ResolveAppId();
			SteamAppId.WriteDevelopmentFileIfMissing(appId);

			if (!SteamAPI.Init())
			{
				Status = "SteamAPI.Init failed. Make sure Steam is running and the AppID is valid.";
				GD.PushWarning(Status);
				return false;
			}

			_initialized = true;
			SteamId = (ulong)SteamUser.GetSteamID().m_SteamID;
			Status = $"Steam initialized as {SteamId}.";
			_webApiTicketCallback = Callback<GetTicketForWebApiResponse_t>.Create(OnWebApiTicket);
			return true;
		}
		catch (Exception exception)
		{
			Status = $"Steam initialization failed: {exception.Message}";
			GD.PushWarning(Status);
			return false;
		}
	}

	public void RunCallbacks()
	{
		if (_initialized)
		{
			SteamAPI.RunCallbacks();
		}
	}

	public async Task<string> RequestBackendTicketAsync(CancellationToken cancellationToken)
	{
		if (!_initialized)
		{
			throw new InvalidOperationException("Steam is not initialized.");
		}

		if (_ticketCompletion is { Task.IsCompleted: false })
		{
			throw new InvalidOperationException("A Steam Web API ticket request is already in progress.");
		}

		_ticketCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		_activeTicket = SteamUser.GetAuthTicketForWebApi(BackendIdentity);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(AuthTicketTimeoutSeconds));
		using var _ = timeout.Token.Register(() =>
			_ticketCompletion.TrySetException(new TimeoutException("Timed out waiting for Steam Web API ticket."))
		);

		return await _ticketCompletion.Task;
	}

	public void Dispose()
	{
		if (_activeTicket.HasValue)
		{
			SteamUser.CancelAuthTicket(_activeTicket.Value);
			_activeTicket = null;
		}

		_webApiTicketCallback?.Dispose();
		_webApiTicketCallback = null;

		if (_initialized)
		{
			SteamAPI.Shutdown();
			_initialized = false;
		}
	}

	private void OnWebApiTicket(GetTicketForWebApiResponse_t response)
	{
		if (_ticketCompletion == null)
		{
			return;
		}

		if (response.m_eResult != EResult.k_EResultOK)
		{
			_ticketCompletion.TrySetException(
				new InvalidOperationException($"Steam returned {response.m_eResult} for the Web API ticket.")
			);
			return;
		}

		var ticketLength = checked((int)response.m_cubTicket);
		var builder = new StringBuilder(ticketLength * 2);
		for (var i = 0; i < ticketLength; i++)
		{
			builder.Append(response.m_rgubTicket[i].ToString("x2", CultureInfo.InvariantCulture));
		}

		_ticketCompletion.TrySetResult(builder.ToString());
	}

	private static uint ResolveAppId()
	{
		var configured = System.Environment.GetEnvironmentVariable("PDD_STEAM_APP_ID");
		if (uint.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var envAppId))
		{
			return envAppId;
		}

		return SteamAppId.TryReadDevelopmentFile(out var fileAppId)
			? fileAppId
			: DefaultDevelopmentAppId;
	}
}
