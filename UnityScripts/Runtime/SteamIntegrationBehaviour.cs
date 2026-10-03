using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

#if PDD_FACEPUNCH_STEAMWORKS
using Steamworks;
#endif

namespace PetDaDog.Unity
{
    [DisallowMultipleComponent]
    public sealed class SteamIntegrationBehaviour : MonoBehaviour
    {
        public const string BackendIdentity = "petdadog-backend";

        private const uint DefaultDevelopmentAppId = 480;
        private const double AuthTicketTimeoutSeconds = 15.0;

#if PDD_FACEPUNCH_STEAMWORKS
        private AuthTicket _activeTicket;
#endif
        private bool _initialized;

        public bool IsInitialized => _initialized;
        public string Status { get; private set; } = "Steam is not initialized.";
        public ulong SteamId { get; private set; }

        public bool Initialize()
        {
            if (Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1")
            {
                Status = "Steam is disabled by PDD_DISABLE_STEAM.";
                return false;
            }

            if (_initialized)
            {
                return true;
            }

#if !PDD_FACEPUNCH_STEAMWORKS
            Status = "Facepunch.Steamworks is not enabled. Add the package and PDD_FACEPUNCH_STEAMWORKS define.";
            Debug.LogWarning(Status);
            return false;
#else
            try
            {
                var appId = ResolveAppId();
                WriteDevelopmentFileIfMissing(appId);

                // Manual callback pumping preserves the existing per-frame Steam lifecycle.
                SteamClient.Init(appId, asyncCallbacks: false);
                _initialized = SteamClient.IsValid;
                SteamId = _initialized ? SteamClient.SteamId : 0;
                Status = _initialized
                    ? $"Steam initialized as {SteamId.ToString(CultureInfo.InvariantCulture)}."
                    : "Steam initialization did not produce a valid client.";
                return _initialized;
            }
            catch (Exception exception)
            {
                Status = $"Steam initialization failed: {exception.Message}";
                Debug.LogWarning(Status);
                return false;
            }
#endif
        }

        private void Update()
        {
#if PDD_FACEPUNCH_STEAMWORKS
            if (_initialized)
            {
                SteamClient.RunCallbacks();
            }
#endif
        }

        public async Task<string> RequestBackendTicketAsync(CancellationToken cancellationToken)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("Steam is not initialized.");
            }

#if !PDD_FACEPUNCH_STEAMWORKS
            throw new InvalidOperationException("Facepunch.Steamworks is not enabled.");
#else
            _activeTicket?.Cancel();
            _activeTicket = null;

            var ticketTask = SteamUser.GetAuthTicketForWebApiAsync(BackendIdentity, AuthTicketTimeoutSeconds);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completedTask = await Task.WhenAny(ticketTask, cancellationTask);
            if (completedTask != ticketTask)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var ticket = await ticketTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (ticket == null || ticket.Data == null || ticket.Data.Length == 0)
            {
                throw new InvalidOperationException("Steam did not return a Web API ticket.");
            }

            _activeTicket = ticket;
            return ToLowerHex(ticket.Data);
#endif
        }

        private void OnDestroy()
        {
#if PDD_FACEPUNCH_STEAMWORKS
            _activeTicket?.Cancel();
            _activeTicket = null;
            if (_initialized)
            {
                SteamClient.Shutdown();
            }
#endif
            _initialized = false;
        }

        private static uint ResolveAppId()
        {
            var configured = Environment.GetEnvironmentVariable("PDD_STEAM_APP_ID");
            if (uint.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var environmentAppId))
            {
                return environmentAppId;
            }

            foreach (var path in new[]
                     {
                         Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt"),
                         Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"),
                     })
            {
                try
                {
                    if (File.Exists(path)
                        && uint.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var fileAppId))
                    {
                        return fileAppId;
                    }
                }
                catch
                {
                    // A development AppID file is only a local convenience; fall back safely.
                }
            }

            return DefaultDevelopmentAppId;
        }

        private static void WriteDevelopmentFileIfMissing(uint appId)
        {
            try
            {
                var path = Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt");
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, appId.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                // Builds can run from read-only install locations; Steam still receives the AppID from Init.
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
            {
                builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
