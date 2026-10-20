namespace Godot
{
	public static class GD
	{
		public static void PushWarning(string message) { }
	}
	public enum Error { Ok, Failed }
	public static class OS
	{
		public static string? LastOpenedUrl { get; private set; }
		public static Error ShellOpen(string url) { LastOpenedUrl = url; return Error.Ok; }
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
	public enum EResult { k_EResultOK = 1, k_EResultFail = 2 }
	public readonly record struct SteamAPICall_t(ulong Value);
	public readonly record struct SteamItemDef_t(int Value);
	public readonly record struct SteamInventoryResult_t(int Value);
	public struct SteamInventoryRequestPricesResult_t { public EResult m_result; public string m_rgchCurrency; }
	public struct SteamInventoryStartPurchaseResult_t { public EResult m_result; public ulong m_ulOrderID; public ulong m_ulTransID; }
	public struct SteamInventoryResultReady_t { public EResult m_result; public SteamInventoryResult_t m_handle; }
	public struct SteamInventoryFullUpdate_t { }
	public struct GameOverlayActivated_t { public byte m_bActive; }
	public enum EActivateGameOverlayToWebPageMode { k_EActivateGameOverlayToWebPageMode_Default }
	public static class SteamInventory
	{
		public static int PriceRequests { get; private set; }
		public static int PurchaseStarts { get; private set; }
		public static int LastPurchasedItem { get; private set; }
		public static Dictionary<int, (ulong Current, ulong Original)> Prices { get; } = new();
		public static List<SteamInventoryResult_t> ReleasedResults { get; } = new();
		private static int _nextResult = 100;
		public static bool GetAllItems(out SteamInventoryResult_t handle) { handle = new(++_nextResult); return true; }
		public static void DestroyResult(SteamInventoryResult_t handle) => ReleasedResults.Add(handle);
		public static SteamAPICall_t RequestPrices() { PriceRequests++; return new((ulong)PriceRequests); }
		public static bool GetItemPrice(SteamItemDef_t item, out ulong current, out ulong original)
		{
			if (Prices.TryGetValue(item.Value, out var price)) { current = price.Current; original = price.Original; return true; }
			current = original = 0; return false;
		}
		public static SteamAPICall_t StartPurchase(SteamItemDef_t[] ids, uint[] quantities, uint count)
		{
			if (ids.Length != 1 || quantities.Length != 1 || quantities[0] != 1 || count != 1) throw new InvalidOperationException("Only one currency bundle per checkout.");
			PurchaseStarts++; LastPurchasedItem = ids[0].Value; return new((ulong)PurchaseStarts);
		}
	}
	public static class SteamUtils { public static bool OverlayEnabled { get; set; } = true; public static bool IsOverlayEnabled() => OverlayEnabled; }
	public static class SteamFriends
	{
		public static string? LastOpenedUrl { get; private set; }
		public static void ActivateGameOverlayToWebPage(string url, EActivateGameOverlayToWebPageMode mode) => LastOpenedUrl = url;
	}
	public sealed class CallResult<T> : IDisposable
	{
		private static CallResult<T>? _current;
		private Action<T, bool>? _callback;
		public static CallResult<T> Create(Action<T, bool> callback) => new() { _callback = callback };
		public void Set(SteamAPICall_t call) => _current = this;
		public static void Dispatch(T result, bool failed = false) => _current?._callback?.Invoke(result, failed);
		public void Dispose() { _callback = null; if (ReferenceEquals(_current, this)) _current = null; }
	}
	public struct GetTicketForWebApiResponse_t
	{
		public HAuthTicket m_hAuthTicket;
		public EResult m_eResult;
		public uint m_cubTicket;
		public byte[] m_rgubTicket;
	}
	public sealed class Callback<T> : IDisposable
	{
		private static readonly List<Callback<T>> Active = new();
		private Action<T>? _action;
		public static Callback<T> Create(Action<T> action) { var callback = new Callback<T> { _action = action }; Active.Add(callback); return callback; }
		public static void Dispatch(T result) { foreach (var callback in Active.ToArray()) callback._action?.Invoke(result); }
		public void Dispose() { _action = null; Active.Remove(this); }
	}
}
