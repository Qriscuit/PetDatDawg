using Godot;
using System;
using System.Threading;

public partial class DesktopPet : Node2D
{
	private const string DogTexturePath = "res://Sprites/Doggo.png";
	private const string FallbackTexturePath = "res://icon.svg";
	private const string StatusWindowScenePath = "res://StatusWindow.tscn";

	private const float DogHeight = 128.0f;
	private const float WalkSpeed = 120.0f;
	private const float HopHeight = 10.0f;
	private const float StepFrequency = 8.0f;
	private const float GroundMargin = 2.0f;
	private const int OverlayPadding = 28;
	private const float LayoutRefreshSeconds = 1.0f;
	private const float InvisibleDogAlphaThreshold = 0.01f;

	private static int MainWindowId => (int)DisplayServer.MainWindowId;

	private Node2D _footAnchor = null!;
	private Node2D _visualRoot = null!;
	private Sprite2D _dogSprite = null!;

	private Vector2 _visibleDogSize = new(128.0f, 128.0f);
	private Rect2 _visibleDogLocalRect = new(-64.0f, -64.0f, 128.0f, 128.0f);
	private Vector2I _windowSize = new(960, 170);
	private Rect2I _lastUsableRect;

	private readonly SteamIntegration _steam = new();
	private readonly BackendPetClient _backend = new();
	private readonly CancellationTokenSource _shutdown = new();

	private PetSettings? _settings;
	private NativeWindowBridge? _nativeWindowBridge;
	private StatusWindow? _statusWindow;
	private float _baseDogScale = 1.0f;
	private float _dogScale = 1.0f;
	private float _walkX;
	private float _direction = 1.0f;
	private float _stepPhase;
	private float _layoutTimer;
	private double _authRetryTimer;
	private bool _authStarted;
	private bool _lastAlwaysOnTop = PetSettings.DefaultAlwaysOnTop;
	private float _lastDogScale = PetSettings.DefaultDogScale;

	public override void _Ready()
	{
		_footAnchor = GetNode<Node2D>("FootAnchor");
		_visualRoot = GetNode<Node2D>("FootAnchor/VisualRoot");
		_dogSprite = GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		_settings = GetNodeOrNull<PetSettings>("/root/PetSettings");
		_nativeWindowBridge = GetNodeOrNull<NativeWindowBridge>("/root/NativeWindowBridge");
		_lastAlwaysOnTop = _settings?.AlwaysOnTop ?? PetSettings.DefaultAlwaysOnTop;
		_lastDogScale = _settings?.DogScale ?? PetSettings.DefaultDogScale;

		if (_settings != null)
		{
			_settings.SettingsChanged += OnSettingsChanged;
		}

		if (_nativeWindowBridge != null)
		{
			_nativeWindowBridge.StatusRequested += OpenStatusWindow;
			_nativeWindowBridge.DogClickThroughToggleRequested += ToggleDogClickThrough;
		}

		EnsureTransparentOverlay();
		GetViewport().GuiEmbedSubwindows = false;

		ConfigureOverlayWindow();
		ConfigureDogSprite();
		MoveOverlayToBottom(force: true);

		_walkX = _windowSize.X * 0.5f;
		_steam.Initialize();
		SetProcess(true);
	}

	public override void _Process(double delta)
	{
		var deltaF = (float)delta;

		_layoutTimer += deltaF;
		if (_layoutTimer >= LayoutRefreshSeconds)
		{
			_layoutTimer = 0.0f;
			MoveOverlayToBottom(force: false);
		}

		StepDog(deltaF);
		AnimateDog();
		UpdateDogMouseRegion();
		TickBackend(delta);
		UpdateStatusWindow();
		_steam.RunCallbacks();
	}

	public override void _Input(InputEvent inputEvent)
	{
		if (ShouldDogPassThrough())
		{
			return;
		}

		if (inputEvent is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouseButton)
		{
			return;
		}

		if (!IsVisibleDogPixel(mouseButton.Position))
		{
			return;
		}

		_backend.EnqueuePetGrant(Guid.NewGuid());
		GetViewport().SetInputAsHandled();
	}

	public override void _ExitTree()
	{
		if (_settings != null)
		{
			_settings.SettingsChanged -= OnSettingsChanged;
		}

		if (_nativeWindowBridge != null)
		{
			_nativeWindowBridge.StatusRequested -= OpenStatusWindow;
			_nativeWindowBridge.DogClickThroughToggleRequested -= ToggleDogClickThrough;
		}

		_statusWindow?.QueueFree();
		_statusWindow = null;
		_shutdown.Cancel();
		DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.MousePassthrough, false, MainWindowId);
		DisplayServer.WindowSetMousePassthrough(Array.Empty<Vector2>(), MainWindowId);
		_backend.Dispose();
		_steam.Dispose();
		_shutdown.Dispose();
	}

	private void ConfigureOverlayWindow()
	{
		EnsureTransparentOverlay();
		DisplayServer.WindowSetFlag(
			DisplayServer.WindowFlags.AlwaysOnTop,
			_settings?.AlwaysOnTop ?? PetSettings.DefaultAlwaysOnTop,
			MainWindowId
		);
		DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true, MainWindowId);
		DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Transparent, true, MainWindowId);
		DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true, MainWindowId);
		DisplayServer.WindowSetFlag(
			DisplayServer.WindowFlags.MousePassthrough,
			ShouldDogPassThrough(),
			MainWindowId
		);
	}

	private void EnsureTransparentOverlay()
	{
		GetViewport().TransparentBg = true;
		RenderingServer.SetDefaultClearColor(Colors.Transparent);
	}

	private void ConfigureDogSprite()
	{
		_dogSprite.Texture = ResourceLoader.Load<Texture2D>(DogTexturePath)
			?? ResourceLoader.Load<Texture2D>(FallbackTexturePath);
		_dogSprite.Centered = true;
		_dogSprite.Visible = true;

		if (_dogSprite.Texture == null)
		{
			GD.PushError("Could not load Doggo.png or the fallback icon.");
			return;
		}

		var textureSize = new Vector2I(_dogSprite.Texture.GetWidth(), _dogSprite.Texture.GetHeight());
		var visibleBounds = GetVisibleBounds(_dogSprite.Texture);
		if (visibleBounds.Size == Vector2I.Zero)
		{
			visibleBounds = new Rect2I(Vector2I.Zero, textureSize);
		}

		var textureCenter = new Vector2(textureSize.X, textureSize.Y) * 0.5f;
		var visibleCenter = new Vector2(
			visibleBounds.Position.X + visibleBounds.Size.X * 0.5f,
			visibleBounds.Position.Y + visibleBounds.Size.Y * 0.5f
		);
		var visibleBottom = visibleBounds.Position.Y + visibleBounds.Size.Y;

		_dogSprite.Position = new Vector2(
			-(visibleCenter.X - textureCenter.X),
			-(visibleBottom - textureCenter.Y)
		);

		_visibleDogLocalRect = new Rect2(
			new Vector2(visibleBounds.Position.X, visibleBounds.Position.Y) - new Vector2(textureSize.X, textureSize.Y) * 0.5f,
			visibleBounds.Size
		);
		_baseDogScale = DogHeight / visibleBounds.Size.Y;
		ApplyDogSettings(refreshLayout: false);
	}

	private void MoveOverlayToBottom(bool force)
	{
		if (Engine.IsEmbeddedInEditor())
		{
			_windowSize = (Vector2I)GetViewportRect().Size;
			return;
		}

		var screen = (int)DisplayServer.ScreenPrimary;
		var usableRect = DisplayServer.ScreenGetUsableRect(screen);
		var overlayHeight = Mathf.CeilToInt(_visibleDogSize.Y + HopHeight + OverlayPadding);

		if (!force && usableRect == _lastUsableRect && overlayHeight == _windowSize.Y)
		{
			return;
		}

		_lastUsableRect = usableRect;
		_windowSize = new Vector2I(usableRect.Size.X, overlayHeight);

		DisplayServer.WindowSetSize(_windowSize, MainWindowId);
		DisplayServer.WindowSetPosition(new Vector2I(usableRect.Position.X, usableRect.End.Y - overlayHeight), MainWindowId);
		ConfigureOverlayWindow();
		RefreshNativeWindowStyles(forceZOrder: false);
	}

	private void StepDog(float delta)
	{
		_stepPhase += delta * StepFrequency;
		_walkX += _direction * WalkSpeed * delta;

		var (minX, maxX) = GetWalkBounds();

		if (_walkX <= minX)
		{
			_walkX = minX;
			_direction = 1.0f;
		}
		else if (_walkX >= maxX)
		{
			_walkX = maxX;
			_direction = -1.0f;
		}
	}

	private void AnimateDog()
	{
		var hop = Mathf.Abs(Mathf.Sin(_stepPhase));
		var squash = 1.0f + (1.0f - hop) * 0.07f;
		var stretch = 1.0f + hop * 0.05f;

		_footAnchor.Position = new Vector2(
			_walkX,
			_windowSize.Y - GroundMargin - hop * HopHeight
		);
		_visualRoot.Scale = new Vector2(_direction * _dogScale * squash, _dogScale * stretch);
		_visualRoot.Rotation = Mathf.Sin(_stepPhase * 0.5f) * 0.035f * _direction;
	}

	private void TickBackend(double delta)
	{
		if (_shutdown.IsCancellationRequested || !_steam.IsInitialized)
		{
			return;
		}

		if (!_backend.IsAuthenticated)
		{
			_authRetryTimer -= delta;
			if (!_authStarted || _authRetryTimer <= 0.0)
			{
				_authStarted = true;
				_authRetryTimer = 5.0;
				_ = _backend.AuthenticateAsync(_steam, _shutdown.Token);
			}
		}

		_backend.Tick(delta, _shutdown.Token);
	}

	private void UpdateDogMouseRegion()
	{
		if (Engine.IsEmbeddedInEditor())
		{
			return;
		}

		if (ShouldDogPassThrough())
		{
			DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.MousePassthrough, true, MainWindowId);
			return;
		}

		DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.MousePassthrough, false, MainWindowId);

		var rect = _visibleDogLocalRect;
		var points = new[]
		{
			_dogSprite.ToGlobal(rect.Position),
			_dogSprite.ToGlobal(new Vector2(rect.End.X, rect.Position.Y)),
			_dogSprite.ToGlobal(rect.End),
			_dogSprite.ToGlobal(new Vector2(rect.Position.X, rect.End.Y)),
		};

		DisplayServer.WindowSetMousePassthrough(points, MainWindowId);
	}

	private void ApplyDogSettings(bool refreshLayout, bool forceNativeZOrder = false)
	{
		_dogScale = _baseDogScale * (_settings?.DogScale ?? PetSettings.DefaultDogScale);
		_visibleDogSize = _visibleDogLocalRect.Size * _dogScale;
		_visualRoot.Modulate = new Color(
			1.0f,
			1.0f,
			1.0f,
			_settings?.DogTransparency ?? PetSettings.DefaultDogTransparency
		);

		ConfigureOverlayWindow();

		if (refreshLayout)
		{
			_lastUsableRect = new Rect2I();
			MoveOverlayToBottom(force: true);
			if (forceNativeZOrder)
			{
				RefreshNativeWindowStyles(forceZOrder: true);
			}
		}
		else
		{
			RefreshNativeWindowStyles(forceNativeZOrder);
		}

		ClampDogToWalkBounds();
		UpdateDogMouseRegion();
	}

	private void OnSettingsChanged()
	{
		var alwaysOnTop = _settings?.AlwaysOnTop ?? PetSettings.DefaultAlwaysOnTop;
		var alwaysOnTopChanged = alwaysOnTop != _lastAlwaysOnTop;
		var dogScale = _settings?.DogScale ?? PetSettings.DefaultDogScale;
		var dogScaleChanged = !Mathf.IsEqualApprox(dogScale, _lastDogScale);

		_lastAlwaysOnTop = alwaysOnTop;
		_lastDogScale = dogScale;

		ApplyDogSettings(refreshLayout: dogScaleChanged, forceNativeZOrder: alwaysOnTopChanged);
	}

	private (float MinX, float MaxX) GetWalkBounds()
	{
		var halfDogWidth = _visibleDogSize.X * 0.5f;
		var minX = Mathf.Max(halfDogWidth + 6.0f, 16.0f);
		var maxX = Mathf.Max(minX, _windowSize.X - minX);
		return (minX, maxX);
	}

	private void ClampDogToWalkBounds()
	{
		var (minX, maxX) = GetWalkBounds();
		_walkX = Mathf.Clamp(_walkX, minX, maxX);
	}

	private bool ShouldDogPassThrough()
	{
		return (_settings?.DogClickThrough ?? PetSettings.DefaultDogClickThrough) || IsDogInvisible();
	}

	private bool IsDogInvisible()
	{
		return (_settings?.DogTransparency ?? PetSettings.DefaultDogTransparency) <= InvisibleDogAlphaThreshold;
	}

	private void RefreshNativeWindowStyles(bool forceZOrder)
	{
		_nativeWindowBridge?.CallDeferred(
			forceZOrder
				? "ApplyDesktopPetWindowStylesAndRaise"
				: "ApplyDesktopPetWindowStyles"
		);
	}

	private void OpenStatusWindow()
	{
		if (_statusWindow == null)
		{
			var scene = ResourceLoader.Load<PackedScene>(StatusWindowScenePath);
			if (scene == null)
			{
				GD.PushError($"Could not load {StatusWindowScenePath}.");
				return;
			}

			_statusWindow = scene.Instantiate<StatusWindow>();
			GetTree().Root.AddChild(_statusWindow);
			_statusWindow.Configure(_settings);
		}

		UpdateStatusWindow();
		_statusWindow.ShowStatusWindow();
	}

	private void ToggleDogClickThrough()
	{
		_settings?.ToggleDogClickThrough();
	}

	private void UpdateStatusWindow()
	{
		_statusWindow?.UpdateStatus(_backend.ConfirmedPets, _backend.PendingGrantCount, _backend.Status, _steam.Status);
	}

	private bool IsVisibleDogPixel(Vector2 viewportPosition)
	{
		if (_dogSprite.Texture == null)
		{
			return false;
		}

		var textureSize = new Vector2(_dogSprite.Texture.GetWidth(), _dogSprite.Texture.GetHeight());
		var localPosition = _dogSprite.ToLocal(viewportPosition);
		var texturePosition = localPosition + textureSize * 0.5f;
		var x = Mathf.FloorToInt(texturePosition.X);
		var y = Mathf.FloorToInt(texturePosition.Y);

		if (x < 0 || y < 0 || x >= textureSize.X || y >= textureSize.Y)
		{
			return false;
		}

		var image = _dogSprite.Texture.GetImage();
		return image != null && image.GetPixel(x, y).A > 0.03f;
	}

	private static Rect2I GetVisibleBounds(Texture2D texture)
	{
		var image = texture.GetImage();
		if (image == null)
		{
			return new Rect2I();
		}

		var minX = image.GetWidth();
		var minY = image.GetHeight();
		var maxX = -1;
		var maxY = -1;

		for (var y = 0; y < image.GetHeight(); y++)
		{
			for (var x = 0; x < image.GetWidth(); x++)
			{
				if (image.GetPixel(x, y).A <= 0.03f)
				{
					continue;
				}

				minX = Mathf.Min(minX, x);
				minY = Mathf.Min(minY, y);
				maxX = Mathf.Max(maxX, x);
				maxY = Mathf.Max(maxY, y);
			}
		}

		return maxX < minX || maxY < minY
			? new Rect2I()
			: new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
	}
}
