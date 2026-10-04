using Godot;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

// Native-only probe of real desktop editing. Capture only the small dog/control region.
public partial class OverlayNativeDiagnostic : Node
{
	private const string Output = "res://.godot/overlay-native-diagnostic";
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	private DesktopPet? _pet;
	private StatusWindow? _status;
	private DesktopAccessoryControls? _controls;
	private Rect2 _appearance;
	private int _worstGpu;
	public override void _Ready() => CallDeferred(nameof(Run));
	private async void Run()
	{
		try
		{
			DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Output));
			var wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
			wardrobe.StoragePath = $"{Output}/diagnostic-outfit.cfg"; wardrobe.Clear();
			_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>(); AddChild(_pet); _pet.SetProcess(false);
			SetField(_pet, "_settings", null); await Settle();
			_status = GetField<StatusWindow?>(_pet, "_statusWindow"); _status?.Hide(); FinishFall();
			var size = GetField<Vector2I>(_pet, "_windowSize"); SetField(_pet, "_walkX", size.X * 0.3f); SetField(_pet, "_stepPhase", 0f);
			Call(_pet, "AnimateDog"); Call(_pet, "UpdateDogMouseRegion"); await Capture("menu-closed");
			Call(_pet, "OpenStatusWindow"); _status = GetField<StatusWindow>(_pet, "_statusWindow");
			_controls = GetField<DesktopAccessoryControls>(_pet, "_accessoryControls");
			_pet.MoveEditingDog(new Vector2(GetField<Vector2I>(_pet, "_windowSize").X * 0.25f, 340));
			var session = GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession");
			wardrobe.SetTint("FairyWing.jpg", new Color(0.92f, 0.55f, 0.88f)); wardrobe.SetTransform("FairyWing.jpg", 1.8f, 0);
			session.ChooseAccessory("FairyWing.jpg"); session.PlaceAt(new Vector2(0.4f, 0.42f));
			session.ChooseAccessory("SafetyGlasses.jpg"); session.PlaceAt(new Vector2(0.8f, 0.29f));
			await Capture("menu-items-open");
			using (var menu = _status.GetTexture().GetImage()) menu.SavePng($"{Output}/menu-native.png");
			var initialRegion = GetField<Rect2I?>(_pet, "_lastMouseRegion");
			for (var frame = 0; frame < 30; frame++)
			{
				_pet._Process(1.0 / 60); await Capture($"editing-{frame:D2}", false);
				if (GetField<Rect2I?>(_pet, "_lastMouseRegion") != initialRegion) throw new InvalidOperationException("Still Items geometry changed its native clip.");
			}
			GD.Print($"OVERLAY_DIAG editing_worst_gpu={_worstGpu}, stable_region_30_frames=true");
			_status.Hide(); await Capture("menu-hidden-falling"); FinishFall(); await Capture("menu-hidden-grounded");
			GD.Print("OVERLAY_DIAG_PASS");
		}
		catch (Exception exception) { GD.PushError($"OVERLAY_DIAG_FAIL: {exception}"); }
		finally { GetTree().Quit(); }
	}
	private void FinishFall()
	{
		for (var frame = 0; frame < 240 && GetField<bool>(_pet!, "_fallingToGround"); frame++) _pet!._Process(1.0 / 60);
	}
	private async Task Settle()
	{
		for (var frame = 0; frame < 3; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
	}
	private async Task Capture(string name, bool save = true)
	{
		Call(_pet!, "UpdateDogMouseRegion"); await Settle();
		_appearance = AccessoryGeometry.BoundsFromCorners(_pet!.EditableDog, GetField<Rect2>(_pet, "_appearanceLocalRect"));
		if (_controls?.GetRenderBounds() is Rect2 controls) _appearance = _appearance.Merge(controls);
		var region = GetField<Rect2I?>(_pet, "_lastMouseRegion")!.Value;
		using var viewport = GetTree().Root.GetTexture().GetImage(); var outsideAlpha = 0;
		for (var y = 0; y < viewport.GetHeight(); y++) for (var x = 0; x < viewport.GetWidth(); x++)
			if (!region.HasPoint(new Vector2I(x, y)) && viewport.GetPixel(x, y).A > 1f / 255) outsideAlpha++;
		_worstGpu = Math.Max(_worstGpu, outsideAlpha);
		if (save) { using var cropped = viewport.GetRegion(CropRect(viewport)); cropped.SavePng($"{Output}/{name}-viewport.png"); }
		GD.Print($"OVERLAY_DIAG {name} gpu_margin_nontransparent={outsideAlpha}, render_region={region}, appearance={_appearance}");
		if (OS.GetName() == "Windows")
		{
			var handle = CreateRectRgn(0, 0, 0, 0); var hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, 0);
			var kind = GetWindowRgn(hwnd, handle); GetRgnBox(handle, out var bounds); DeleteObject(handle);
			GD.Print($"OVERLAY_DIAG {name} native_region={kind}:({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom})");
		}
		if (!save) return;
		var cropRect = CropRect(viewport); cropRect.Position += GetTree().Root.Position;
		using var screen = DisplayServer.ScreenGetImageRect(cropRect);
		if (screen == null) { GD.Print($"OVERLAY_DIAG {name} screen_capture_unavailable"); return; }
		var hasColor = false;
		for (var y = 0; y < screen.GetHeight() && !hasColor; y += 3) for (var x = 0; x < screen.GetWidth(); x += 3)
		{
			var pixel = screen.GetPixel(x, y); if (pixel.R > 0.01f || pixel.G > 0.01f || pixel.B > 0.01f) { hasColor = true; break; }
		}
		if (!hasColor) GD.Print($"OVERLAY_DIAG {name} screen_capture_unavailable_all_black (no compositor verdict)");
		else { screen.SavePng($"{Output}/{name}-screen.png"); GD.Print($"OVERLAY_DIAG {name} native_crop_saved (visual review required; no uncontrolled background comparison)"); }
	}
	private Rect2I CropRect(Image image)
	{
		var start = new Vector2I(Mathf.FloorToInt(_appearance.Position.X) - 14, Mathf.FloorToInt(_appearance.Position.Y) - 14);
		var end = new Vector2I(Mathf.CeilToInt(_appearance.End.X) + 14, Mathf.CeilToInt(_appearance.End.Y) + 14);
		return new Rect2I(start, end - start).Intersection(new Rect2I(Vector2I.Zero, new Vector2I(image.GetWidth(), image.GetHeight())));
	}
	[StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
	[DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
	[DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
	[DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect bounds);
	[DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
	private static T GetField<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
	private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
	private static void Call(object target, string name) => target.GetType().GetMethod(name, Private)!.Invoke(target, null);
}
