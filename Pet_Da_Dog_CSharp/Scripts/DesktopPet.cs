using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public partial class DesktopPet : Node2D
{
	[Export] public PackedScene? StatusScene { get; set; }
	[Export] public PackedScene? TextAccessoryScene { get; set; }
	private const string DogTexturePath = "res://Sprites/Doggo.png";
	private const string HeartTexturePath = "res://Sprites/PetzHeart.png";
	private const string FallbackTexturePath = "res://icon.svg";
	private const string StatusWindowScenePath = "res://StatusWindow.tscn";

	private const float DogHeight = 128.0f;
	private const float WalkSpeed = 120.0f;
	private const float HopHeight = 10.0f;
	private const float StepFrequency = 8.0f;
	private const float GroundMargin = 2.0f;
	private const int OverlayPadding = 28;
	private const int RenderRegionPadding = 2;
	private const float LayoutRefreshSeconds = 1.0f;
	private const float InvisibleDogAlphaThreshold = 0.01f;
	private const float HeartLifetimeSeconds = 1.8f;
	private const float HeartRiseDistance = 90.0f;
	private const float HeartDriftDistance = 46.0f;
	private const float HeartSize = 42.0f;
	private const int MaxActiveHearts = 8;

	private static int MainWindowId => (int)DisplayServer.MainWindowId;

	private Node2D _footAnchor = null!;
	private Node2D _visualRoot = null!;
	private Sprite2D _dogSprite = null!;
	private Image? _dogImage;
	private string _activeDogTexturePath = DogTexturePath;
	private IReadOnlyList<SteamInventoryItem>? _lastInventoryItems;
	private Texture2D? _heartTexture;

	private Vector2 _visibleDogSize = new(128.0f, 128.0f);
	private Rect2 _visibleDogLocalRect = new(-64.0f, -64.0f, 128.0f, 128.0f);
	private Vector2I _windowSize = new(960, 170);
	private Rect2I _lastUsableRect;

	private readonly SteamIntegration _steam = new();
	private readonly BackendPetClient _backend = new();
	private readonly CancellationTokenSource _shutdown = new();

	private PetSettings? _settings;
	private AccessoryWardrobe? _wardrobe;
	private readonly Dictionary<string, Sprite2D> _accessorySprites = new();
	private readonly Dictionary<string, PetTextAccessory> _textAccessories = new();
	private Rect2 _appearanceLocalRect;
	private NativeWindowBridge? _nativeWindowBridge;
	private StatusWindow? _statusWindow;
	private AccessoryEditingSession? _editingSession;
	private DesktopAccessoryControls? _accessoryControls;
	private PatrolRoute? _patrolRoute;
	private DesktopPatrolEditor? _patrolEditor;
	private bool _patrolRunning, _patrolEditing;
	private Vector2 _patrolPosition;
	private int _patrolTargetIndex;
	private Vector2[] _patrolSnapshot = Array.Empty<Vector2>();
	private bool _accessoryEditing, _fallingToGround;
	private Vector2 _editPosition;
	private float _fallVelocity;
	private Rect2I? _lastMouseRegion;
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
	private readonly List<FloatingHeart> _floatingHearts = new();
	private bool _validatePets;
	private string? _lastValidationStatus;
	private double _validationTimer;

	private sealed class FloatingHeart
	{
		public FloatingHeart(Sprite2D sprite, Vector2 startPosition, Vector2 endPosition, Vector2 baseScale)
		{
			Sprite = sprite;
			StartPosition = startPosition;
			EndPosition = endPosition;
			BaseScale = baseScale;
		}

		public Sprite2D Sprite { get; }
		public Vector2 StartPosition { get; }
		public Vector2 EndPosition { get; }
		public Vector2 BaseScale { get; }
		public float Age { get; set; }
	}

	public override void _Ready()
	{
		_footAnchor = GetNode<Node2D>("FootAnchor");
		_visualRoot = GetNode<Node2D>("FootAnchor/VisualRoot");
		_dogSprite = GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		_settings = GetNodeOrNull<PetSettings>("/root/PetSettings");
		_wardrobe = GetNodeOrNull<AccessoryWardrobe>("/root/AccessoryWardrobe");
		_nativeWindowBridge = GetNodeOrNull<NativeWindowBridge>("/root/NativeWindowBridge");
		_editingSession = GetNodeOrNull<AccessoryEditingSession>("/root/AccessoryEditingSession");
		_patrolRoute = GetNodeOrNull<PatrolRoute>("/root/PatrolRoute");
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
		if (_wardrobe != null)
		{
			_wardrobe.Changed += SyncAppearance;
		}
		SyncAccessories();
		ConfigureHeartTexture();
		MoveOverlayToBottom(force: true);

		_walkX = _windowSize.X * 0.5f;
		AnimateDog();
		_accessoryControls = GetNode<DesktopAccessoryControls>("AccessoryControls");
		_accessoryControls.Configure(this, _wardrobe, _editingSession);
		if (_patrolRoute != null)
		{
			_patrolEditor = GetNode<DesktopPatrolEditor>("PatrolEditor");
			_patrolEditor.Configure(this, _patrolRoute);
			_patrolRoute.Changed += OnPatrolRouteChanged;
			_patrolRoute.EditingChanged += OnPatrolEditingChanged;
			OnPatrolRouteChanged();
			if (_patrolRoute.IsEditing) OnPatrolEditingChanged(true);
		}
		if (_editingSession != null)
		{
			_editingSession.ActiveChanged += OnAccessoryModeChanged;
			if (_editingSession.Active) OnAccessoryModeChanged(true);
		}
		_steam.Initialize();
		_validatePets = Array.IndexOf(OS.GetCmdlineUserArgs(), "--validate-pets") >= 0;
		if (_validatePets) GD.Print($"PET_VALIDATION: {_steam.Status}");
		SetProcess(true);

		// Also allow launching directly into Status, including packaged-build smoke checks.
		if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--status") >= 0 || _settings?.HasSeenWelcome == false)
		{
			CallDeferred(nameof(OpenStatusWindow));
		}
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

		_accessoryControls?.PollPointer();
		_patrolEditor?.PollPointer();
		if (_fallingToGround) UpdateDogFall(deltaF);
		else if (!_accessoryEditing && !_patrolEditing)
		{
			if (_patrolRunning) StepPatrol(deltaF);
			else StepDog(deltaF);
		}
		AnimateDog();
		UpdateFloatingHearts(deltaF);
		UpdateDogMouseRegion();
		TickBackend(delta);
		if (!ReferenceEquals(_lastInventoryItems, _backend.InventoryItems))
		{
			_lastInventoryItems = _backend.InventoryItems;
			_wardrobe?.SetSteamOwnership(_lastInventoryItems.Where(item => item.Quantity > 0).Select(item => item.ItemDefId));
		}
		UpdateStatusWindow();
		_steam.RunCallbacks();
		if (_validatePets) LogPetValidation(delta);
	}

	public override void _Input(InputEvent inputEvent)
	{
		if (_patrolEditing)
		{
			_patrolEditor?.HandleInput(inputEvent);
			GetViewport().SetInputAsHandled();
			return; // Mapping route stops never grants pets or opens the dog menu.
		}
		if (_accessoryEditing)
		{
			if (inputEvent is InputEventMouseMotion motion) _accessoryControls?.UpdatePointer(motion.Position);
			if (inputEvent is InputEventMouseButton button)
			{
				if (button.ButtonIndex == MouseButton.Left && _accessoryControls?.HandlePointer(button.Position, button.Pressed) == true)
					GetViewport().SetInputAsHandled();
				else if (button.ButtonIndex == MouseButton.Right && button.Pressed)
				{
					_accessoryControls?.HandleContextMenu(button.Position);
					GetViewport().SetInputAsHandled();
				}
			}
			return; // Editing gestures never pet the dog or enqueue currency grants.
		}
		if (ShouldDogPassThrough())
		{
			return;
		}

		if (inputEvent is not InputEventMouseButton { Pressed: true } mouseButton)
		{
			return;
		}

		if (mouseButton.ButtonIndex is not MouseButton.Left and not MouseButton.Right)
		{
			return;
		}

		if (!IsVisibleDogPixel(mouseButton.Position))
		{
			return;
		}

		if (mouseButton.ButtonIndex == MouseButton.Left)
		{
			_backend.EnqueuePetGrant(Guid.NewGuid());
			if (_validatePets) GD.Print($"PET_VALIDATION: visible dog click queued; pending={_backend.PendingGrantCount}");
			SpawnFloatingHeart();
		}
		else
		{
			OpenStatusWindow();
		}

		GetViewport().SetInputAsHandled();
	}

	public override void _UnhandledKeyInput(InputEvent inputEvent)
	{
		if (inputEvent is InputEventKey key && _accessoryControls?.HandleKey(key) == true)
			GetViewport().SetInputAsHandled();
	}

	public override void _ExitTree()
	{
		if (_patrolRoute != null)
		{
			_patrolRoute.Changed -= OnPatrolRouteChanged;
			_patrolRoute.EditingChanged -= OnPatrolEditingChanged;
		}
		if (_editingSession != null) _editingSession.ActiveChanged -= OnAccessoryModeChanged;
		_nativeWindowBridge?.SetAccessoryEditing(false);
		if (_wardrobe != null)
		{
			_wardrobe.Changed -= SyncAppearance;
		}
		if (_settings != null)
		{
			_settings.SettingsChanged -= OnSettingsChanged;
		}

		if (_nativeWindowBridge != null)
		{
			_nativeWindowBridge.StatusRequested -= OpenStatusWindow;
			_nativeWindowBridge.DogClickThroughToggleRequested -= ToggleDogClickThrough;
		}

		if (_statusWindow != null)
		{
			_statusWindow.QueueFree();
		}
		_statusWindow = null;
		while (_floatingHearts.Count > 0)
		{
			RemoveFloatingHeartAt(_floatingHearts.Count - 1);
		}

		_shutdown.Cancel();
		_dogImage?.Dispose();
		DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.MousePassthrough, false, MainWindowId);
		DisplayServer.WindowSetMousePassthrough(Array.Empty<Vector2>(), MainWindowId);
		_backend.Dispose();
		_steam.Dispose();
		_shutdown.Dispose();
	}

	private void ConfigureOverlayWindow()
	{
		EnsureTransparentOverlay();
		SetOverlayFlag(
			DisplayServer.WindowFlags.AlwaysOnTop,
			_accessoryEditing || _patrolEditing || (_settings?.AlwaysOnTop ?? PetSettings.DefaultAlwaysOnTop)
		);
		SetOverlayFlag(DisplayServer.WindowFlags.Borderless, true);
		SetOverlayFlag(DisplayServer.WindowFlags.Transparent, true);
		SetOverlayFlag(DisplayServer.WindowFlags.NoFocus, true);
		SetOverlayFlag(
			DisplayServer.WindowFlags.MousePassthrough,
			ShouldDogPassThrough() || IsPointerOutsideDog()
		);
	}

	private static void SetOverlayFlag(DisplayServer.WindowFlags flag, bool value)
	{
		if (DisplayServer.WindowGetFlag(flag, MainWindowId) != value) DisplayServer.WindowSetFlag(flag, value, MainWindowId);
	}

	private void EnsureTransparentOverlay()
	{
		GetViewport().TransparentBg = true;
		RenderingServer.SetDefaultClearColor(Colors.Transparent);
	}

	private void ConfigureDogSprite()
	{
		_activeDogTexturePath = _wardrobe?.CurrentDogTexturePath ?? DogTexturePath;
		_dogSprite.Texture = ResourceLoader.Load<Texture2D>(_activeDogTexturePath)
			?? ResourceLoader.Load<Texture2D>(FallbackTexturePath);
		_dogSprite.Centered = true;
		_dogSprite.Visible = true;

		if (_dogSprite.Texture == null)
		{
			GD.PushError("Could not load Doggo.png or the fallback icon.");
			return;
		}

		var textureSize = new Vector2I(_dogSprite.Texture.GetWidth(), _dogSprite.Texture.GetHeight());
		_dogImage?.Dispose();
		// Some texture implementations share their image; retain our own hit-test copy.
		_dogImage = (Image)_dogSprite.Texture.GetImage().Duplicate();
		var visibleBounds = AccessoryWardrobe.GetVisibleBounds(_dogSprite.Texture);
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
		_appearanceLocalRect = _visibleDogLocalRect;
		_baseDogScale = DogHeight / visibleBounds.Size.Y;
		ApplyDogSettings(refreshLayout: false);
	}

	private void ConfigureHeartTexture()
	{
		_heartTexture = ResourceLoader.Load<Texture2D>(HeartTexturePath);
		if (_heartTexture == null)
		{
			GD.PushWarning($"Could not load {HeartTexturePath}; pet feedback hearts will be disabled.");
		}
		else
		{
			var bounds = AccessoryWardrobe.GetVisibleBounds(_heartTexture);
			if (bounds.Size != Vector2I.Zero) _heartTexture = new AtlasTexture { Atlas = _heartTexture, Region = bounds, FilterClip = true };
		}
	}

	private void SyncAppearance()
	{
		if (_activeDogTexturePath != (_wardrobe?.CurrentDogTexturePath ?? DogTexturePath))
		{
			ConfigureDogSprite();
			_lastMouseRegion = null;
		}
		SyncAccessories();
		MoveOverlayToBottom(force: true);
	}

	private void SyncAccessories()
	{
		var equippedIds = new HashSet<string>();
		_appearanceLocalRect = _visibleDogLocalRect;
		_dogSprite.ZIndex = 0;
		if (_wardrobe != null)
		{
			foreach (var placement in _wardrobe.Equipped)
			{
				var definition = _wardrobe.Find(placement.Id);
				if (definition == null)
				{
					continue;
				}
				equippedIds.Add(placement.Id);
				if (definition.IsText)
				{
					if (!_textAccessories.TryGetValue(placement.Id, out var textBox))
					{
						textBox = (TextAccessoryScene ?? ResourceLoader.Load<PackedScene>("res://UI/PetTextAccessory.tscn")).Instantiate<PetTextAccessory>();
						_dogSprite.AddChild(textBox); _textAccessories.Add(placement.Id, textBox);
					}
					var textSize = definition.Size * _visibleDogLocalRect.Size.Y * placement.Scale;
					textBox.Position = _visibleDogLocalRect.Position + _visibleDogLocalRect.Size * placement.Position;
					textBox.Scale = textSize / textBox.BubbleSize * new Vector2(_direction, 1);
					textBox.RotationDegrees = placement.RotationDegrees;
					textBox.Text = placement.Text; textBox.TextColor = placement.Tint;
					textBox.BackgroundVisible = placement.BackgroundVisible; textBox.QueueRedraw();
					textBox.ZIndex = _wardrobe.GetLayerIndex(placement.Id) - _wardrobe.GetLayerIndex(AccessoryWardrobe.DogLayerId);
					_dogSprite.MoveChild(textBox, -1);
					_appearanceLocalRect = _appearanceLocalRect.Merge(AccessoryGeometry.Bounds(textBox.Position, textSize, placement.RotationDegrees));
					continue;
				}
				if (!_accessorySprites.TryGetValue(placement.Id, out var sprite))
				{
					sprite = new Sprite2D { Texture = definition.Texture, Centered = true };
					_dogSprite.AddChild(sprite);
					_accessorySprites.Add(placement.Id, sprite);
				}
				// Dog-local anchors inherit its direction, hopping, scale and transparency.
				var size = definition.Size * _visibleDogLocalRect.Size.Y * placement.Scale;
				sprite.Position = _visibleDogLocalRect.Position + _visibleDogLocalRect.Size * placement.Position;
				sprite.Scale = size / definition.Texture.GetSize();
				sprite.RotationDegrees = placement.RotationDegrees;
				sprite.Modulate = placement.Tint;
				sprite.ZIndex = _wardrobe.GetLayerIndex(placement.Id) - _wardrobe.GetLayerIndex(AccessoryWardrobe.DogLayerId);
				_dogSprite.MoveChild(sprite, -1);
				_appearanceLocalRect = _appearanceLocalRect.Merge(AccessoryGeometry.Bounds(sprite.Position, size, placement.RotationDegrees));
			}
		}
		foreach (var id in new List<string>(_accessorySprites.Keys))
		{
			if (!equippedIds.Contains(id))
			{
				var sprite = _accessorySprites[id];
				_dogSprite.RemoveChild(sprite);
				sprite.QueueFree();
				_accessorySprites.Remove(id);
			}
		}
		foreach (var id in new List<string>(_textAccessories.Keys))
		{
			if (equippedIds.Contains(id)) continue;
			var textBox = _textAccessories[id]; _dogSprite.RemoveChild(textBox); textBox.QueueFree(); _textAccessories.Remove(id);
		}
		MoveOverlayToBottom(force: false);
		ClampDogToWalkBounds();
		if (_accessoryEditing) ClampEditingDog();
		AnimateDog();
		_accessoryControls?.QueueRedraw();
		UpdateDogMouseRegion();
	}

	private (float Top, float Bottom, float HalfWidth) GetAppearanceExtents()
	{
		if (_accessorySprites.Count == 0 && _textAccessories.Count == 0)
		{
			return (_visibleDogSize.Y, 0.0f, _visibleDogSize.X * 0.5f);
		}
		var rect = new Rect2(_appearanceLocalRect.Position + _dogSprite.Position, _appearanceLocalRect.Size);
		var halfWidth = Mathf.Max(Mathf.Abs(rect.Position.X), Mathf.Abs(rect.End.X)) * 1.07f;
		var height = Mathf.Max(Mathf.Abs(rect.Position.Y), Mathf.Abs(rect.End.Y)) * 1.05f;
		var rotationMargin = Mathf.Sin(0.035f);
		return (
			(Mathf.Max(0, -rect.Position.Y) * 1.05f + halfWidth * rotationMargin) * _dogScale,
			(Mathf.Max(0, rect.End.Y) * 1.05f + halfWidth * rotationMargin) * _dogScale,
			(halfWidth + height * rotationMargin) * _dogScale);
	}

	private void MoveOverlayToBottom(bool force)
	{
		if (_patrolRunning || _patrolEditing)
		{
			EnsurePatrolOverlay(force);
			return;
		}
		if (_accessoryEditing || _fallingToGround) return;
		if (Engine.IsEmbeddedInEditor())
		{
			_windowSize = (Vector2I)GetViewportRect().Size;
			return;
		}

		var screen = (int)DisplayServer.ScreenPrimary;
		var usableRect = DisplayServer.ScreenGetUsableRect(screen);
		var appearance = GetAppearanceExtents();
		// Reserve the entire feedback trajectory so hearts are never clipped above the dog.
		var heartTop = _visibleDogSize.Y + 18 + HeartRiseDistance + HeartSize * 0.6f;
		var overlayHeight = Mathf.CeilToInt(Mathf.Max(appearance.Top, heartTop) + appearance.Bottom + HopHeight + OverlayPadding);
		var targetSize = new Vector2I(usableRect.Size.X, overlayHeight);
		var targetPosition = new Vector2I(usableRect.Position.X, usableRect.End.Y - overlayHeight);

		if (!force && usableRect == _lastUsableRect && _windowSize == targetSize
			&& GetTree().Root.Size == targetSize && GetTree().Root.Position == targetPosition
			&& DisplayServer.WindowGetMode(MainWindowId) == DisplayServer.WindowMode.Windowed
			&& DisplayServer.WindowGetSize(MainWindowId) == targetSize
			&& DisplayServer.WindowGetPosition(MainWindowId) == targetPosition)
		{
			return;
		}

		_lastUsableRect = usableRect;
		_windowSize = targetSize;
		ClampDogToWalkBounds();
		AnimateDog();
		_lastMouseRegion = null;

		SetOverlayGeometry(targetPosition, targetSize);
		ConfigureOverlayWindow();
		RefreshNativeWindowStyles(forceZOrder: false);
	}

	private void SetOverlayGeometry(Vector2I position, Vector2I size)
	{
		var window = GetTree().Root;
		// Godot ignores native size/position requests in maximized or fullscreen mode.
		// Keep the Window node and its viewport synchronized with the native rectangle.
		if (window.Mode != Window.ModeEnum.Windowed) window.Mode = Window.ModeEnum.Windowed;
		window.Size = size;
		window.Position = position;
	}

	private void EnsurePatrolOverlay(bool force)
	{
		if (_accessoryEditing) return;
		if (Engine.IsEmbeddedInEditor())
		{
			_windowSize = (Vector2I)GetViewportRect().Size;
			return;
		}
		var usable = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary);
		// As in accessory mode, leave a pixel so Windows/Godot retains windowed mode.
		var size = new Vector2I(usable.Size.X, Mathf.Max(1, usable.Size.Y - 1));
		if (!force && _lastUsableRect == usable && _windowSize == size
			&& GetTree().Root.Size == size && GetTree().Root.Position == usable.Position
			&& DisplayServer.WindowGetMode(MainWindowId) == DisplayServer.WindowMode.Windowed
			&& DisplayServer.WindowGetSize(MainWindowId) == size
			&& DisplayServer.WindowGetPosition(MainWindowId) == usable.Position) return;
		if (_patrolRunning && _windowSize != size)
			_patrolPosition = _patrolPosition / (Vector2)_windowSize * (Vector2)size;
		if (_patrolEditing && _lastUsableRect != usable)
			_editPosition = _editPosition / (Vector2)_windowSize * (Vector2)size;
		_lastUsableRect = usable;
		_windowSize = size;
		SetOverlayGeometry(usable.Position, size);
		_patrolPosition = ClampPatrolPoint(_patrolPosition);
		if (_patrolEditing) _editPosition = ClampPatrolPoint(_editPosition);
		_lastMouseRegion = null;
		_patrolEditor?.RefreshLayout(usable);
		ConfigureOverlayWindow();
		RefreshNativeWindowStyles(forceZOrder: false);
	}

	private void OnPatrolRouteChanged()
	{
		if (_patrolRoute == null) return;
		var points = _patrolRoute.Points;
		var routeChanged = points.Count != _patrolSnapshot.Length;
		for (var i = 0; !routeChanged && i < points.Count; i++) routeChanged = points[i] != _patrolSnapshot[i];
		if (routeChanged)
		{
			_patrolSnapshot = new Vector2[points.Count];
			for (var i = 0; i < points.Count; i++) _patrolSnapshot[i] = points[i];
			_patrolTargetIndex = 0;
		}
		var enabled = _patrolRoute.Enabled && points.Count >= 2;
		if (enabled == _patrolRunning) return;
		var screenFoot = _footAnchor.Position + (Vector2)DisplayServer.WindowGetPosition(MainWindowId);
		_patrolRunning = enabled;
		if (enabled)
		{
			_fallingToGround = false;
			_patrolTargetIndex = 0;
			EnsurePatrolOverlay(force: true);
			_patrolPosition = ClampPatrolPoint(screenFoot - (Vector2)DisplayServer.WindowGetPosition(MainWindowId));
		}
		else if (!_accessoryEditing && !_patrolEditing)
		{
			_editPosition = _footAnchor.Position;
			_fallingToGround = true;
			_fallVelocity = 0;
		}
		_stepPhase = 0;
		AnimateDog();
		UpdateDogMouseRegion();
	}

	private void OnPatrolEditingChanged(bool active)
	{
		if (_patrolEditing == active) return;
		if (active)
		{
			_editingSession?.SetActive(false);
			var screenFoot = _footAnchor.Position + (Vector2)DisplayServer.WindowGetPosition(MainWindowId);
			_patrolEditing = true;
			_fallingToGround = false;
			EnsurePatrolOverlay(force: true);
			_editPosition = ClampPatrolPoint(screenFoot - (Vector2)DisplayServer.WindowGetPosition(MainWindowId));
		}
		else
		{
			_patrolEditing = false;
			if (_patrolRunning)
			{
				_patrolPosition = ClampPatrolPoint(_editPosition);
				_fallingToGround = false;
			}
			else
			{
				_fallingToGround = true;
				_fallVelocity = 0;
			}
			_stepPhase = 0;
		}
		_nativeWindowBridge?.SetAccessoryEditing(_accessoryEditing || active);
		ApplyDogSettings(refreshLayout: false);
		_lastMouseRegion = null;
		UpdateDogMouseRegion();
	}

	internal bool IsPatrolEditing => _patrolEditing;
	internal bool IsPatrolling => _patrolRunning && !_patrolEditing && !_accessoryEditing && !_fallingToGround;
	internal Vector2 PatrolPosition => _patrolPosition;
	internal int PatrolTargetIndex => _patrolTargetIndex;
	internal Vector2 PatrolPointToViewport(Vector2 normalized) => ClampPatrolPoint(normalized * (Vector2)_windowSize);
	internal Vector2 NormalizePatrolPoint(Vector2 point) => ClampPatrolPoint(point) / (Vector2)_windowSize;
	private Vector2 ClampPatrolPoint(Vector2 point)
	{
		var extents = GetAppearanceExtents();
		var horizontal = Mathf.Min(extents.HalfWidth + 8, _windowSize.X * 0.5f);
		var top = Mathf.Min(extents.Top + HopHeight + 8, _windowSize.Y);
		var bottom = Mathf.Max(top, _windowSize.Y - extents.Bottom - GroundMargin - 6);
		return new Vector2(Mathf.Clamp(point.X, horizontal, _windowSize.X - horizontal), Mathf.Clamp(point.Y, top, bottom));
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

	private void StepPatrol(float delta)
	{
		if (_patrolRoute == null || _patrolRoute.Points.Count < 2 || !float.IsFinite(delta) || delta <= 0) return;
		_stepPhase += delta * StepFrequency;
		var remaining = WalkSpeed * delta;
		// Multiple points can be crossed in one frame. Bound the work even if a
		// resolution change clamps several stops onto the same screen position.
		for (var visits = 0; remaining > 0 && visits < 64; visits++)
		{
			_patrolTargetIndex %= _patrolRoute.Points.Count;
			var target = PatrolPointToViewport(_patrolRoute.Points[_patrolTargetIndex]);
			var difference = target - _patrolPosition;
			var distance = difference.Length();
			if (Mathf.Abs(difference.X) > 0.01f) _direction = Mathf.Sign(difference.X);
			if (distance > remaining)
			{
				_patrolPosition += difference * (remaining / distance);
				break;
			}
			_patrolPosition = target;
			remaining -= distance;
			_patrolTargetIndex = (_patrolTargetIndex + 1) % _patrolRoute.Points.Count;
		}
		_walkX = _patrolPosition.X;
	}

	private void AnimateDog()
	{
		// Rest while customizing, including redraws caused by live accessory/settings edits.
		var resting = _accessoryEditing || _patrolEditing || _fallingToGround;
		var hop = resting ? 0.0f : Mathf.Abs(Mathf.Sin(_stepPhase));
		var squash = resting ? 1.0f : 1.0f + (1.0f - hop) * 0.07f;
		var stretch = resting ? 1.0f : 1.0f + hop * 0.05f;

		_footAnchor.Position = resting ? _editPosition : _patrolRunning
			? _patrolPosition - new Vector2(0, hop * HopHeight) : new Vector2(
			_walkX,
			_windowSize.Y - GroundMargin - GetAppearanceExtents().Bottom - hop * HopHeight
		);
		_visualRoot.Scale = new Vector2(_direction * _dogScale * squash, _dogScale * stretch);
		_visualRoot.Rotation = resting ? 0.0f : Mathf.Sin(_stepPhase * 0.5f) * 0.035f * _direction;
		foreach (var textBox in _textAccessories.Values)
			textBox.Scale = new Vector2(Mathf.Abs(textBox.Scale.X) * _direction, textBox.Scale.Y);
	}

	private void SpawnFloatingHeart()
	{
		if (_heartTexture == null)
		{
			return;
		}

		while (_floatingHearts.Count >= MaxActiveHearts)
		{
			RemoveFloatingHeartAt(0);
		}

		var xOffset = Mathf.Sin(_stepPhase * 1.37f) * 12.0f;
		var startPosition = new Vector2(xOffset, -_visibleDogSize.Y - 16.0f);
		var endPosition = startPosition + new Vector2(_direction * HeartDriftDistance, -HeartRiseDistance);
		var baseScale = Vector2.One * (HeartSize / Mathf.Max(_heartTexture.GetWidth(), _heartTexture.GetHeight()));
		var sprite = new Sprite2D
		{
			Texture = _heartTexture,
			Centered = true,
			Position = startPosition,
			Scale = baseScale * 0.75f,
			ZIndex = 20,
			Modulate = Colors.White
		};

		_footAnchor.AddChild(sprite);
		_floatingHearts.Add(new FloatingHeart(sprite, startPosition, endPosition, baseScale));
		UpdateDogMouseRegion();
	}

	private void UpdateFloatingHearts(float delta)
	{
		for (var index = _floatingHearts.Count - 1; index >= 0; index--)
		{
			var heart = _floatingHearts[index];
			heart.Age += delta;

			var progress = Mathf.Clamp(heart.Age / HeartLifetimeSeconds, 0.0f, 1.0f);
			var easedProgress = Mathf.SmoothStep(0, 1, progress);
			heart.Sprite.Position = heart.StartPosition.Lerp(heart.EndPosition, easedProgress);
			var size = heart.Age < 0.12f ? Mathf.Lerp(0.75f, 1.1f, heart.Age / 0.12f)
				: Mathf.Lerp(1.1f, 0.18f, Mathf.Clamp((heart.Age - 0.12f) / (HeartLifetimeSeconds - 0.12f), 0, 1));
			heart.Sprite.Scale = heart.BaseScale * size;
			heart.Sprite.Modulate = new Color(1, 1, 1, 1 - Mathf.SmoothStep(0.25f, 1, progress));

			if (progress >= 1.0f)
			{
				RemoveFloatingHeartAt(index);
			}
		}
	}

	private void RemoveFloatingHeartAt(int index)
	{
		var heart = _floatingHearts[index];
		_floatingHearts.RemoveAt(index);
		heart.Sprite.QueueFree();
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

	private void LogPetValidation(double delta)
	{
		var status = $"{_backend.Status} confirmed={_backend.ConfirmedPets?.ToString() ?? "unknown"}; pending={_backend.PendingGrantCount}";
		if (_lastValidationStatus != status)
		{
			GD.Print($"PET_VALIDATION: {status}");
			_lastValidationStatus = status;
		}
		_validationTimer += delta;
		if (_validationTimer >= 10.0 && _steam.IsInitialized)
		{
			_validationTimer = 0;
			GD.Print($"PET_VALIDATION: Steam overlay enabled={Steamworks.SteamUtils.IsOverlayEnabled()}");
		}
	}

	private void UpdateDogMouseRegion()
	{
		if (Engine.IsEmbeddedInEditor())
		{
			return;
		}

		// Windows also clips rendering to this region, so include accessory overhang.
		// _Input still checks only the original dog texture alpha before granting a pet.
		var rect = _appearanceLocalRect;
		var globalBounds = AccessoryGeometry.BoundsFromCorners(_dogSprite, rect);
		foreach (var heart in _floatingHearts)
			globalBounds = globalBounds.Merge(AccessoryGeometry.BoundsFromCorners(heart.Sprite, heart.Sprite.GetRect()));
		if (_accessoryEditing && _accessoryControls?.GetRenderBounds() is Rect2 controlsBounds)
			globalBounds = globalBounds.Merge(controlsBounds);
		if (_patrolEditing) globalBounds = new Rect2(Vector2.Zero, _windowSize);
		// Godot maps this polygon to SetWindowRgn on Windows. Reapplying identical
		// regions still causes native window-position messages; fractional tight edges
		// also clip filtered sprite pixels. Round outward, pad, and update only on change.
		var start = new Vector2I(Mathf.FloorToInt(globalBounds.Position.X) - RenderRegionPadding,
			Mathf.FloorToInt(globalBounds.Position.Y) - RenderRegionPadding);
		var end = new Vector2I(Mathf.CeilToInt(globalBounds.End.X) + RenderRegionPadding,
			Mathf.CeilToInt(globalBounds.End.Y) + RenderRegionPadding);
		var region = new Rect2I(start, end - start).Intersection(new Rect2I(Vector2I.Zero, _windowSize));
		if (_lastMouseRegion != region)
		{
			var points = new[] { (Vector2)region.Position, new Vector2(region.End.X, region.Position.Y),
				(Vector2)region.End, new Vector2(region.Position.X, region.End.Y) };
			DisplayServer.WindowSetMousePassthrough(points, MainWindowId);
			_lastMouseRegion = region;
		}

		// Keep decorative pixels and transparent gaps clickable through to the desktop.
		// Global mouse polling continues while passthrough is on, restoring dog input on entry.
		var pointerOutsideDog = IsPointerOutsideDog();
		DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.MousePassthrough,
			ShouldDogPassThrough() || pointerOutsideDog, MainWindowId);
		_nativeWindowBridge?.SetPointerPassthrough(pointerOutsideDog);
	}

	private bool IsPointerOutsideDog()
	{
		if (_patrolEditing) return false;
		if (Engine.IsEmbeddedInEditor())
		{
			return false;
		}
		var point = (Vector2)(DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition(MainWindowId));
		return _accessoryEditing ? _accessoryControls?.IsInteractiveAt(point) != true : !IsVisibleDogPixel(point);
	}

	private void ApplyDogSettings(bool refreshLayout, bool forceNativeZOrder = false)
	{
		_dogScale = _baseDogScale * (_settings?.DogScale ?? _lastDogScale);
		_visibleDogSize = _visibleDogLocalRect.Size * _dogScale;
		_visualRoot.Modulate = new Color(
			1.0f,
			1.0f,
			1.0f,
			_accessoryEditing || _patrolEditing ? 1.0f : _settings?.DogTransparency ?? PetSettings.DefaultDogTransparency
		);

		ConfigureOverlayWindow();

		if (refreshLayout)
		{
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
		if (_accessoryEditing) ClampEditingDog();
		if (_patrolRunning) _patrolPosition = ClampPatrolPoint(_patrolPosition);
		AnimateDog();
		_accessoryControls?.QueueRedraw();
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
		var halfDogWidth = Mathf.Max(GetAppearanceExtents().HalfWidth, HeartDriftDistance + HeartSize * 0.6f + 16);
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
		if (_accessoryEditing || _patrolEditing) return false;
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
		if (_patrolEditing) _patrolRoute?.CancelEdit();
		if (_statusWindow == null)
		{
			var scene = StatusScene ?? ResourceLoader.Load<PackedScene>(StatusWindowScenePath);
			if (scene == null)
			{
				GD.PushError($"Could not load {StatusWindowScenePath}.");
				return;
			}

			_statusWindow = scene.Instantiate<StatusWindow>();
			_statusWindow.Visible = false;
			GetTree().Root.AddChild(_statusWindow);
			_statusWindow.Configure(_settings);
			_statusWindow.ConfigureSteamInventory(_backend, _wardrobe, _shutdown.Token);
		}

		UpdateStatusWindow();
		_statusWindow.ShowStatusWindow();
	}

	private void OnAccessoryModeChanged(bool active)
	{
		if (_accessoryEditing == active) return;
		if (active)
		{
			if (_patrolEditing) _patrolRoute?.CancelEdit();
			var screenFoot = _footAnchor.Position + (Vector2)DisplayServer.WindowGetPosition(MainWindowId);
			var usable = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary);
			_accessoryEditing = true; _fallingToGround = false;
			// An exact usable/screen rectangle makes a borderless Windows window
			// maximized/fullscreen, preventing the later return to the walking strip.
			_windowSize = new Vector2I(usable.Size.X, Mathf.Max(1, usable.Size.Y - 1));
			_lastUsableRect = usable;
			_editPosition = screenFoot - (Vector2)usable.Position;
			if (!Engine.IsEmbeddedInEditor())
			{
				SetOverlayGeometry(usable.Position, _windowSize);
			}
			_lastMouseRegion = null;
			ClampEditingDog();
		}
		else
		{
			_accessoryControls?.CancelGesture();
			_accessoryEditing = false; _fallingToGround = !_patrolRunning; _fallVelocity = 0;
			if (_patrolRunning) _patrolPosition = ClampPatrolPoint(_editPosition);
			_stepPhase = 0;
		}
		_nativeWindowBridge?.SetAccessoryEditing(active || _patrolEditing);
		ApplyDogSettings(refreshLayout: false);
		UpdateDogMouseRegion();
		_accessoryControls?.QueueRedraw();
	}

	private void UpdateDogFall(float delta)
	{
		var ground = _lastUsableRect.End.Y - DisplayServer.WindowGetPosition(MainWindowId).Y
			- GroundMargin - GetAppearanceExtents().Bottom;
		_fallVelocity += 1800 * delta;
		_editPosition.Y = Mathf.Min(ground, _editPosition.Y + _fallVelocity * delta);
		if (_editPosition.Y < ground) return;
		_fallingToGround = false; _walkX = _editPosition.X; _stepPhase = 0;
		MoveOverlayToBottom(force: true);
		ClampDogToWalkBounds();
		_lastMouseRegion = null;
	}

	private void ClampEditingDog()
	{
		// Reserve the entire edited outfit plus handle room, so controls stay reachable.
		var extents = GetAppearanceExtents();
		var horizontal = Mathf.Min(extents.HalfWidth + 64, _windowSize.X * 0.5f);
		var top = Mathf.Min(extents.Top + 88, _windowSize.Y);
		var bottom = Mathf.Max(top, _windowSize.Y - extents.Bottom - 80);
		_editPosition = new Vector2(Mathf.Clamp(_editPosition.X, horizontal, _windowSize.X - horizontal),
			Mathf.Clamp(_editPosition.Y, top, bottom));
		_walkX = _editPosition.X;
	}

	internal Sprite2D EditableDog => _dogSprite;
	internal Rect2 EditableDogRect => _visibleDogLocalRect;
	internal Rect2 EditableOutfitBounds => AccessoryGeometry.BoundsFromCorners(_dogSprite, _appearanceLocalRect);
	internal float EditableDogScale => _settings?.DogScale ?? _lastDogScale;
	internal Vector2 EditingPosition => _editPosition;
	internal bool IsAccessoryEditing => _accessoryEditing;
	internal bool HitEditableDog(Vector2 point) => IsVisibleDogPixel(point);
	internal Node2D? AccessoryNode(string id) => _textAccessories.TryGetValue(id, out var text) ? text
		: _accessorySprites.TryGetValue(id, out var sprite) ? sprite : null;
	internal Vector2 NormalizeAccessoryPoint(Vector2 point) => (_dogSprite.ToLocal(point) - _visibleDogLocalRect.Position) / _visibleDogLocalRect.Size;
	internal void MoveEditingDog(Vector2 position)
	{
		if (!position.IsFinite()) return;
		_editPosition = position; ClampEditingDog(); AnimateDog(); UpdateDogMouseRegion();
		_accessoryControls?.QueueRedraw();
	}
	internal void ResizeEditingDog(float scale)
	{
		if (!float.IsFinite(scale)) return;
		scale = Mathf.Clamp(scale, PetSettings.MinDogScale, PetSettings.MaxDogScale);
		if (_settings != null) _settings.SetDogScale(scale);
		else
		{
			_lastDogScale = scale; _dogScale = _baseDogScale * scale;
			_visibleDogSize = _visibleDogLocalRect.Size * _dogScale;
			ClampEditingDog(); AnimateDog(); UpdateDogMouseRegion();
		}
		// Also support isolated settings fixtures which are not subscribed to this node.
		if (_settings != null) ApplyDogSettings(refreshLayout: false);
		_accessoryControls?.QueueRedraw();
	}

	private void ToggleDogClickThrough()
	{
		_settings?.ToggleDogClickThrough();
	}

	private void UpdateStatusWindow()
	{
		_statusWindow?.UpdateStatus(_backend.ConfirmedPets, _backend.PendingGrantCount, _backend.Status, _steam.Status, _steam.IsInitialized, _backend.IsAuthenticated);
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

		return _dogImage != null && _dogImage.GetPixel(x, y).A > 0.03f;
	}

}
