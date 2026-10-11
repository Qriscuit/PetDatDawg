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
			// These variables affect this process only and override a stale development file.
			SteamAppId.ConfigureProcess(appId);
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

		cancellationToken.ThrowIfCancellationRequested();
		CancelActiveTicket();
		var ticket = SteamUser.GetAuthTicketForWebApi(BackendIdentity);
		var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		_ticketCompletion = completion;
		_activeTicket = ticket;
		try
		{
			if (ticket.Equals(HAuthTicket.Invalid))
				throw new InvalidOperationException("Steam could not create a Web API ticket.");
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(AuthTicketTimeoutSeconds));
			using var registration = timeout.Token.Register(() =>
			{
				// Capture this request, so a late cancellation cannot cancel a renewal.
				if (cancellationToken.IsCancellationRequested) completion.TrySetCanceled(cancellationToken);
				else completion.TrySetException(new TimeoutException("Timed out waiting for Steam Web API ticket."));
			});
			return await completion.Task;
		}
		catch
		{
			if (_activeTicket.HasValue && _activeTicket.Value.Equals(ticket)) CancelActiveTicket();
			throw;
		}
		finally
		{
			if (ReferenceEquals(_ticketCompletion, completion)) _ticketCompletion = null;
		}
	}

	public void Dispose()
	{
		_ticketCompletion?.TrySetCanceled();
		CancelActiveTicket();

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
		if (_ticketCompletion == null || !_activeTicket.HasValue || !response.m_hAuthTicket.Equals(_activeTicket.Value))
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
		if (ticketLength <= 0 || ticketLength > response.m_rgubTicket.Length)
		{
			_ticketCompletion.TrySetException(new InvalidOperationException("Steam returned an invalid Web API ticket length."));
			return;
		}
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
		if (!string.IsNullOrWhiteSpace(configured))
		{
			if (!uint.TryParse(configured.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var envAppId) || envAppId == 0)
				throw new InvalidOperationException("PDD_STEAM_APP_ID must be a valid Steam AppID.");
			return envAppId;
		}

		return SteamAppId.TryReadDevelopmentFile(out var fileAppId)
			? fileAppId
			: SteamAppId.GameAppId;
	}

	private void CancelActiveTicket()
	{
		if (!_activeTicket.HasValue) return;
		if (_initialized && !_activeTicket.Value.Equals(HAuthTicket.Invalid)) SteamUser.CancelAuthTicket(_activeTicket.Value);
		_activeTicket = null;
	}
}
