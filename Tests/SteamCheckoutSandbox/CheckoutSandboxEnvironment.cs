#if CHECKOUT_SANDBOX
using Godot;

// Copied into a separate generated test project, never the shipping project.
public partial class CheckoutSandboxEnvironment : Node
{
	public override void _EnterTree()
	{
		System.Environment.SetEnvironmentVariable("PDD_STEAM_APP_ID", "4817200");
		System.Environment.SetEnvironmentVariable("PDD_BACKEND_URL", "https://petdadawg.americlasagna.workers.dev");
		System.Environment.SetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT", "1");
		System.Environment.SetEnvironmentVariable("PDD_DISABLE_TRAY", "1");
		System.Environment.SetEnvironmentVariable("PDD_DEV_ECONOMY", "0");
		System.Environment.SetEnvironmentVariable("PDD_DISABLE_STEAM", System.Environment.GetEnvironmentVariable("PDD_CHECKOUT_SANDBOX_OFFLINE") == "1" ? "1" : "0");
	}
}
#endif
