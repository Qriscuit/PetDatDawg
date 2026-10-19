using Godot;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

// Uses a real transparent Windows viewport at several sizes without changing any
// monitor's display mode. All state and screenshots belong to the test profile.
public partial class OverlayRenderingSmokeTest : Node
{
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	private DesktopPet _pet = null!;
	private PetSettings _settings = null!;
	private NativeWindowBridge _bridge = null!;
	private StatusWindow? _menu;
	private int _assertions;
	private readonly List<Vector2I> _screenSizes = new();

	public override void _Ready() => CallDeferred(nameof(Run));

	private async void Run()
	{
		var exit = 0;
		try
		{
			foreach (var variable in new[] { "PDD_DISABLE_STEAM", "PDD_DISABLE_GLOBAL_INPUT", "PDD_DISABLE_TRAY" })
				Require(System.Environment.GetEnvironmentVariable(variable) == "1", $"{variable} is required for this test.");
			Require(OS.GetName() == "Windows" && DisplayServer.GetName() != "headless", "Overlay regression requires the real Windows renderer.");
			for (var i = 0; i < DisplayServer.GetScreenCount(); i++) _screenSizes.Add(DisplayServer.ScreenGetSize(i));
			_settings = GetNode<PetSettings>("/root/PetSettings");
			_settings.DismissWelcome();
			_settings.SetStationary(false); _settings.SetBounceEnabled(true);
			_settings.SetDogTransparency(1); _settings.SetDogScale(1);
			_settings.SetShowPetCounter(false); _settings.SetDogClickThrough(true);
			_bridge = GetNode<NativeWindowBridge>("/root/NativeWindowBridge");
			_bridge.SetProcess(false);
			var wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
			wardrobe.Clear(); wardrobe.SelectDog(0); wardrobe.Equip("SFlower1.png", new Vector2(0.6f, 0.2f));
			_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>();
			AddChild(_pet);
			_pet.SetProcess(false); _pet.SetProcessInput(false);
			await Settle();

			foreach (var size in new[] { new Vector2I(1920, 1079), new Vector2I(3440, 1439), new Vector2I(5120, 1439) })
				await VerifySurface(size);

			VerifyPointerStyleTransitions();
			for (var i = 0; i < _screenSizes.Count; i++)
				Require(DisplayServer.ScreenGetSize(i) == _screenSizes[i], "Window-size fixtures never alter the desktop display resolution.");
			Require(Field<BackendPetClient>(_pet, "_backend").PendingGrantCount == 0,
				"Rendering, menu and native style fixtures do not enqueue gameplay grants.");
			GD.Print($"OVERLAY_RENDERING_SMOKE_PASS: {_assertions} assertions; 1920, 3440 and 5120 pixel native surfaces");
		}
		catch (Exception exception) { exit = 1; GD.PushError($"OVERLAY_RENDERING_SMOKE_FAIL: {exception}"); }
		finally
		{
			// DesktopPet owns and frees its status window during exit.
			_menu?.Hide();
			if (_pet != null) _pet.QueueFree();
			GetTree().Quit(exit);
		}
	}

	private async Task VerifySurface(Vector2I size)
	{
		_menu?.Hide();
		GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession").SetActive(false);
		_settings.SetShowPetCounter(false);
		_settings.SetDogClickThrough(true);
		Set(_pet, "_fallingToGround", false);
		Call(_pet, "UpdateFloatingHearts", 10f); Call(_pet, "UpdatePettingHand", 10f);
		await SetSurface(size);
		var expected = new Rect2I(Vector2I.Zero, size);
		var initialFoot = _pet.GetNode<Node2D>("FootAnchor").Position;
		using (var probe = new WindowMessages(MainHandle))
		{
			for (var step = 0; step < 12; step++)
			{
				Call(_pet, "StepDog", 0.1f); Call(_pet, "AnimateDog"); Call(_pet, "UpdateDogMouseRegion");
				Require(NativeRegion() == expected && Field<Rect2I?>(_pet, "_lastMouseRegion") == expected,
					$"{size.X}: walking and bouncing preserve the actual native drawing region.");
			}
			Require(probe.WindowPositionChanges == 0 && probe.FrameChanges == 0,
				$"{size.X}: animation produces no native region/position or frame-change messages.");
		}
		Require(_pet.GetNode<Node2D>("FootAnchor").Position.DistanceTo(initialFoot) > 30,
			"The stability fixture actually moves the animated dog.");
		await VerifyRenderedAlpha($"{size.X}-walking");
		VerifyDogHitPixels();

		// Feedback is intentionally hidden by the product while click-through is on.
		// Enable normal dog interaction after the cursor-independent message probe.
		_settings.SetDogClickThrough(false);
		Call(_pet, "SpawnFloatingHeart");
		Call(_pet, "ShowPettingHand", _pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite").GlobalPosition);
		Call(_pet, "UpdateFloatingHearts", 0.12f); Call(_pet, "UpdatePettingHand", 0.08f);
		Call(_pet, "UpdateDogMouseRegion");
		Require(Field<Sprite2D>(_pet, "_pettingHand").Visible
			&& Field<System.Collections.IList>(_pet, "_floatingHearts").Count == 1,
			"Feedback fixture visibly includes both the petting hand and a heart.");
		Require(NativeRegion() == expected, "Pet feedback changes pixels without changing the native region.");
		await VerifyRenderedAlpha($"{size.X}-feedback");
		Call(_pet, "UpdateFloatingHearts", 10f); Call(_pet, "UpdatePettingHand", 10f);

		foreach (var placement in Enum.GetValues<PetCounterPlacement>())
		{
			_settings.SetPetCounterPosition(placement); _settings.SetShowPetCounter(true);
			await SetSurface(size);
			var counter = _pet.GetNode<PetCounterDisplay>("PetCounter");
			Require(counter.Visible && counter.GetRenderBounds() is Rect2 bounds
				&& new Rect2(Vector2.Zero, size).Encloses(bounds), $"{placement} counter fits the {size.X} viewport.");
			Require(NativeRegion() == expected, "Changing counter position preserves the full native region after layout settles.");
			if (placement == PetCounterPlacement.TopRight) await VerifyRenderedAlpha($"{size.X}-counter");
		}
		_settings.SetShowPetCounter(false);
		Call(_pet, "OpenStatusWindow"); _menu = Field<StatusWindow>(_pet, "_statusWindow");
		_menu.GetNode<Button>("%SettingsTab").EmitSignal(BaseButton.SignalName.Pressed);
		await SetSurface(size);
		Require(_menu.Visible && !_menu.Transparent && _menu.GetViewport().World2D != _pet.GetViewport().World2D,
			"Settings uses its own opaque native window and canvas, separate from the transparent dog.");
		await VerifyRenderedAlpha($"{size.X}-settings");
		_menu.GetNode<Button>("%ItemsTab").EmitSignal(BaseButton.SignalName.Pressed);
		await SetSurface(size);
		Set(_pet, "_editPosition", new Vector2(size.X * 0.6f, size.Y * 0.55f));
		Call(_pet, "AnimateDog"); Call(_pet, "UpdateDogMouseRegion");
		GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession").SelectAccessory("SFlower1.png");
		Require(Field<bool>(_pet, "_accessoryEditing") && _pet.GetNode<DesktopAccessoryControls>("AccessoryControls").GetRenderBounds().HasValue,
			"Items fixture draws active on-dog editing controls.");
		Require(NativeRegion() == expected, "Opening editing controls preserves the full viewport's native region.");
		await VerifyRenderedAlpha($"{size.X}-items");
		_menu.Hide();
	}

	private async Task SetSurface(Vector2I size)
	{
		// Setting Window.Size creates a real GPU surface at the fixture's resolution,
		// including when part of the test window extends beyond the current monitor.
		Set(_pet, "_windowSize", size);
		Set(_pet, "_lastUsableRect", new Rect2I(Vector2I.Zero, size + new Vector2I(0, 1)));
		Set(_pet, "_walkX", size.X * 0.35f); Set(_pet, "_stepPhase", 0f);
		Call(_pet, "SetOverlayGeometry", Vector2I.Zero, size);
		Call(_pet, "AnimateDog"); Call(_pet, "UpdateDogMouseRegion");
		await Settle();
		Require(GetTree().Root.Size == size && DisplayServer.WindowGetSize(0) == size
			&& (Vector2I)_pet.GetViewportRect().Size == size, $"Godot, viewport and native dimensions agree at {size}.");
		Require(NativeRegion() == new Rect2I(Vector2I.Zero, size), $"The native render region matches the resized {size.X} surface.");
	}

	private async Task VerifyRenderedAlpha(string name)
	{
		await Settle();
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		var allowed = new List<Rect2>();
		void AddSprites(Node node)
		{
			if (node is Sprite2D sprite && sprite.IsVisibleInTree())
				allowed.Add(AccessoryGeometry.BoundsFromCorners(sprite, sprite.GetRect()).Grow(4));
			foreach (var child in node.GetChildren()) AddSprites(child);
		}
		AddSprites(_pet);
		if (_pet.GetNode<PetCounterDisplay>("PetCounter").GetRenderBounds() is Rect2 badge) allowed.Add(badge.Grow(4));
		if (_pet.GetNode<DesktopAccessoryControls>("AccessoryControls").GetRenderBounds() is Rect2 controls) allowed.Add(controls.Grow(4));
		using var image = _pet.GetViewport().GetTexture().GetImage();
		image.Convert(Image.Format.Rgba8);
		var pixels = image.GetData(); var visible = 0; var outside = 0; var clear = 0;
		for (var y = 0; y < image.GetHeight(); y++) for (var x = 0; x < image.GetWidth(); x++)
		{
			if (pixels[(y * image.GetWidth() + x) * 4 + 3] <= 1) { clear++; continue; }
			visible++;
			var covered = false;
			foreach (var bounds in allowed) if (bounds.HasPoint(new Vector2(x + 0.5f, y + 0.5f))) { covered = true; break; }
			if (!covered) outside++;
		}
		Require(visible > 100 && clear > image.GetWidth() * image.GetHeight() / 2,
			$"{name}: GPU readback contains visible artwork and substantial transparent desktop space.");
		Require(outside == 0, $"{name}: no opaque artifacts outside individual artwork/counter/editing bounds (found {outside}).");
		Require(image.SavePng($"res://.godot/overlay-rendering-{name}.png") == Error.Ok, "Transparent viewport regression capture saves.");
	}

	private void VerifyDogHitPixels()
	{
		var dog = _pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		using var image = dog.Texture.GetImage();
		var found = false;
		for (var y = 0; y < image.GetHeight() && !found; y++) for (var x = 0; x < image.GetWidth() && !found; x++)
		{
			if (image.GetPixel(x, y).A < 0.9f) continue;
			var point = dog.ToGlobal(new Vector2(x + 0.5f, y + 0.5f) - dog.Texture.GetSize() * 0.5f);
			Require((bool)Call(_pet, "IsVisibleDogPixel", point)!, "Visible dog pixels remain interactive inside the stable native region.");
			found = true;
		}
		Require(found && !(bool)Call(_pet, "IsVisibleDogPixel", new Vector2(4, 4))!,
			"A distant transparent viewport pixel is excluded from the dog hit target.");
	}

	private void VerifyPointerStyleTransitions()
	{
		_menu?.Hide();
		GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession").SetActive(false);
		_settings.SetDogClickThrough(false); _settings.SetDogTransparency(1);
		_bridge.SetAccessoryEditing(false); _bridge.SetPointerPassthrough(false); _bridge.ApplyDesktopPetWindowStyles();
		var size = DisplayServer.WindowGetSize(0); var region = NativeRegion();
		using var probe = new WindowMessages(MainHandle);
		for (var i = 0; i < 12; i++)
		{
			var passthrough = i % 2 == 0;
			_bridge.SetPointerPassthrough(passthrough);
			Require(((GetWindowLongPtr(MainHandle, -20).ToInt64() & 0x20) != 0) == passthrough,
				"Pointer transitions still change the real WS_EX_TRANSPARENT style.");
		}
		Require(probe.FrameChanges == 0, "Pointer-only transitions never issue SWP_FRAMECHANGED.");
		Require(DisplayServer.WindowGetSize(0) == size && NativeRegion() == region,
			"Pointer-only transitions preserve native size and render region.");
	}

	private Rect2I NativeRegion()
	{
		var handle = CreateRectRgn(0, 0, 0, 0);
		try
		{
			Require(handle != IntPtr.Zero && GetWindowRgn(MainHandle, handle) != 0 && GetRgnBox(handle, out _) != 0,
				"The test window has a queryable native drawing region.");
			GetRgnBox(handle, out var rectangle);
			return new Rect2I(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
		}
		finally { if (handle != IntPtr.Zero) DeleteObject(handle); }
	}

	private static IntPtr MainHandle => (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, 0);
	private async Task Settle() { for (var i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
	private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
	private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
	private static object? Call(object instance, string name, params object[] arguments) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, arguments);
	private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _assertions++; }

	// Observe only this test's HWND. No cursor movement, input injection, hooks into
	// other applications, or production diagnostic state is required.
	private sealed class WindowMessages : IDisposable
	{
		private readonly IntPtr _window;
		private readonly SubclassProc _callback;
		public int WindowPositionChanges { get; private set; }
		public int FrameChanges { get; private set; }
		public WindowMessages(IntPtr window)
		{
			_window = window; _callback = Observe;
			if (!SetWindowSubclass(window, _callback, 1, 0)) throw new InvalidOperationException("Cannot observe the test window's messages.");
		}
		private IntPtr Observe(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
		{
			if (message == 0x0047 && lParam != IntPtr.Zero)
			{
				WindowPositionChanges++;
				if ((Marshal.PtrToStructure<WindowPosition>(lParam).Flags & 0x20) != 0) FrameChanges++;
			}
			return DefSubclassProc(window, message, wParam, lParam);
		}
		public void Dispose() { RemoveWindowSubclass(_window, _callback, 1); GC.KeepAlive(_callback); }
	}
	[StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
	[StructLayout(LayoutKind.Sequential)] private struct WindowPosition { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
	[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
	[DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, nuint id, nuint data);
	[DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, nuint id);
	[DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
	[DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
	[DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
	[DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect rectangle);
	[DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
}
