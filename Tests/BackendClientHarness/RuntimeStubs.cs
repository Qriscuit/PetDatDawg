// The transport harness links the production client without loading Godot or Steam.
namespace Godot
{
	public static class GD
	{
		public static void PushWarning(string message) { }
	}
	public static class ProjectSettings
	{
		public static bool HasSetting(string setting) => false;
		public static Variant GetSetting(string setting) => new();
	}
	public readonly struct Variant
	{
		public string AsString() => string.Empty;
	}
}

public sealed class SteamIntegration
{
	public const string BackendIdentity = "petdadog-backend";
	public int TicketRequests { get; private set; }
	public Task<string> RequestBackendTicketAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		TicketRequests++;
		return Task.FromResult("fixture-ticket");
	}
}
