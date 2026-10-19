namespace Godot
{
	public static class GD
	{
		public static void PushWarning(string message) { }
	}
}

// All native calls are replaced in this isolated harness. The production Steam
// integration is compiled unchanged; no Steam SDK, hooks, HTTP or grants run.
namespace Steamworks
{
	public static class SteamAPI
	{
		public static bool Available { get; set; }
		public static Exception? InitializationError { get; set; }
		public static int InitializationCalls { get; private set; }
		public static int ShutdownCalls { get; private set; }
		public static bool Init()
		{
			InitializationCalls++;
			if (InitializationError != null) throw InitializationError;
			return Available;
		}
		public static void RunCallbacks() { }
		public static void Shutdown() => ShutdownCalls++;
	}
	public static class SteamUser
	{
		public static CSteamID GetSteamID() => new(76561198000000000);
		public static HAuthTicket GetAuthTicketForWebApi(string identity) => HAuthTicket.Invalid;
		public static void CancelAuthTicket(HAuthTicket ticket) { }
	}
	public readonly record struct CSteamID(ulong m_SteamID);
	public readonly record struct HAuthTicket(uint Value)
	{
		public static HAuthTicket Invalid => new(0);
	}
	public enum EResult { k_EResultOK = 1 }
	public struct GetTicketForWebApiResponse_t
	{
		public HAuthTicket m_hAuthTicket;
		public EResult m_eResult;
		public uint m_cubTicket;
		public byte[] m_rgubTicket;
	}
	public sealed class Callback<T> : IDisposable
	{
		public static Callback<T> Create(Action<T> action) => new();
		public void Dispose() { }
	}
}
