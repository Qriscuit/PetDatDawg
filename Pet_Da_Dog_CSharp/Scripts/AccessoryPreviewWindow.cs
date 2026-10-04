using System;
using Godot;

/// <summary>A small, independent placement canvas for the accessory menu.</summary>
public partial class AccessoryPreviewWindow : Window
{
	private const string LayoutPath = "user://preview_layout.cfg";
	private static readonly Vector2I DefaultSize = new(400, 360);
	private static readonly Vector2I DefaultMinimumSize = new(300, 280);
	private AccessoryWardrobe? _wardrobe;
	private bool _layoutLoaded;
	private Vector2I _windowedPosition;
	private Vector2I _windowedSize = DefaultSize;

	public AccessoryPreview Preview { get; private set; } = null!;
	public event Action? PreviewClosed;
	public event Func<InputEventKey, bool>? ShortcutRequested;

	public override void _Ready()
	{
		Title = "Pet Da Dog · Preview";
		InitialPosition = WindowInitialPosition.Absolute;
		Size = DefaultSize;
		MinSize = DefaultMinimumSize;
		Borderless = false;
		AlwaysOnTop = false;
		Transparent = false;
		TransparentBg = false;
		Unresizable = false;
		Exclusive = false;
		Transient = false;
		PopupWindow = false;
		Visible = false;
		Theme = WoodlandTheme.Build();
		BuildUi();
		if (_wardrobe != null) Preview.Configure(_wardrobe);
		CloseRequested += OnCloseRequested;
	}

	public override void _Process(double delta)
	{
		// Preserve the ordinary window geometry when the user later minimizes it.
		if (_layoutLoaded && Visible && Mode == ModeEnum.Windowed) RememberWindowedLayout();
	}

	public override void _ExitTree() => SaveLayout();

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (!Visible || @event is not InputEventKey { Pressed: true, Echo: false } key || ShortcutRequested == null) return;
		foreach (Func<InputEventKey, bool> handler in ShortcutRequested.GetInvocationList())
		{
			if (!handler(key)) continue;
			SetInputAsHandled();
			break;
		}
	}

	public void Configure(AccessoryWardrobe wardrobe)
	{
		if (_wardrobe == wardrobe) return;
		_wardrobe = wardrobe;
		if (IsNodeReady()) Preview.Configure(wardrobe);
	}

	public void ShowPreview(Window owner)
	{
		// Show without activation, then allow normal focus and keyboard input on click.
		Unfocusable = true;
		try
		{
			if (Mode == ModeEnum.Minimized) Mode = ModeEnum.Windowed;
			if (!_layoutLoaded)
			{
				LoadLayout(owner);
				_layoutLoaded = true;
			}
			ClampLayoutToScreen(owner.CurrentScreen);
			RememberWindowedLayout();
			Show();
		}
		finally
		{
			Unfocusable = false;
		}
	}

	private void BuildUi()
	{
		var background = new PanelContainer();
		background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		background.AddThemeStyleboxOverride("panel", WoodlandTheme.WindowStyle());
		AddChild(background);

		var margin = new MarginContainer();
		foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 12);
		background.AddChild(margin);
		var content = new VBoxContainer
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill
		};
		content.AddThemeConstantOverride("separation", 6);
		margin.AddChild(content);

		var heading = new Label { Text = "Preview", ThemeTypeVariation = "WoodlandHeading", MouseFilter = Control.MouseFilterEnum.Ignore };
		heading.AddThemeFontSizeOverride("font_size", 16);
		heading.AddThemeColorOverride("font_color", WoodlandTheme.Parchment.Lightened(0.1f));
		content.AddChild(heading);
		Preview = new AccessoryPreview
		{
			Name = "AccessoryPreview",
			CustomMinimumSize = new Vector2(0, 160),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill
		};
		content.AddChild(Preview);

		var instructions = new Label
		{
			Text = "Choose an accessory in the menu.\nClick to place; drag to adjust.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		instructions.AddThemeColorOverride("font_color", WoodlandTheme.Parchment.Lightened(0.1f));
		instructions.AddThemeFontSizeOverride("font_size", 12);
		content.AddChild(instructions);
	}

	private void OnCloseRequested()
	{
		Preview.CancelInteraction();
		SaveLayout();
		Hide();
		PreviewClosed?.Invoke();
	}

	private void LoadLayout(Window owner)
	{
		Position = new Vector2I(owner.Position.X - Size.X - 16, owner.Position.Y);
		using var config = new ConfigFile();
		if (config.Load(LayoutPath) != Error.Ok) return;
		var position = config.GetValue("preview", "position", Position);
		var size = config.GetValue("preview", "size", Size);
		if (position.VariantType == Variant.Type.Vector2I) Position = position.AsVector2I();
		if (size.VariantType != Variant.Type.Vector2I) return;
		var stored = size.AsVector2I();
		if (stored.X > 0 && stored.Y > 0) Size = stored.Max(DefaultMinimumSize);
	}

	private void ClampLayoutToScreen(int ownerScreen)
	{
		if (Engine.IsEmbeddedInEditor()) return;
		var screenCount = DisplayServer.GetScreenCount();
		if (screenCount < 1) return;
		var usable = DisplayServer.ScreenGetUsableRect(Mathf.Clamp(ownerScreen, 0, screenCount - 1));
		var center = (Vector2)Position + (Vector2)Size * 0.5f;
		var nearestDistance = float.PositiveInfinity;
		for (var screen = 0; screen < screenCount; screen++)
		{
			var candidate = DisplayServer.ScreenGetUsableRect(screen);
			if (candidate.Size.X <= 0 || candidate.Size.Y <= 0) continue;
			var candidateCenter = (Vector2)candidate.Position + (Vector2)candidate.Size * 0.5f;
			var distance = candidateCenter.DistanceSquaredTo(center);
			if (((Rect2)candidate).HasPoint(center)) { usable = candidate; break; }
			if (distance < nearestDistance) { nearestDistance = distance; usable = candidate; }
		}
		if (usable.Size.X <= 0 || usable.Size.Y <= 0) return;
		// Leave space for the native titlebar and resize border as well as client content.
		var maximum = new Vector2I(Mathf.Max(1, usable.Size.X - 24), Mathf.Max(1, usable.Size.Y - 56));
		MinSize = DefaultMinimumSize.Min(maximum);
		Size = new Vector2I(Mathf.Clamp(Size.X, MinSize.X, maximum.X), Mathf.Clamp(Size.Y, MinSize.Y, maximum.Y));
		var left = usable.Position.X + 12;
		var top = usable.Position.Y + 40;
		Position = new Vector2I(
			Mathf.Clamp(Position.X, left, Mathf.Max(left, usable.End.X - Size.X - 12)),
			Mathf.Clamp(Position.Y, top, Mathf.Max(top, usable.End.Y - Size.Y - 16)));
	}

	private void RememberWindowedLayout()
	{
		if (Mode != ModeEnum.Windowed) return;
		_windowedPosition = Position;
		_windowedSize = Size;
	}

	private void SaveLayout()
	{
		if (!_layoutLoaded) return;
		RememberWindowedLayout();
		using var config = new ConfigFile();
		config.SetValue("preview", "position", _windowedPosition);
		config.SetValue("preview", "size", _windowedSize);
		var error = config.Save(LayoutPath);
		if (error != Error.Ok) GD.PushWarning($"Could not save preview window layout: {error}");
	}
}
