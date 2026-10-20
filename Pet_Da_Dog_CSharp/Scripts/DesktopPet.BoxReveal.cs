using Godot;
using System;
using System.Collections.Generic;

public partial class DesktopPet
{
	private const float BoxTravelSeconds = 0.5f;
	private const float BoxDigSeconds = 1.2f;
	private const float BoxRiseSeconds = 0.65f;
	private const float BoxLandSeconds = 0.18f;
	private const float BoxOpenSeconds = 0.45f;
	private const float BoxOpeningStarts = BoxTravelSeconds + BoxDigSeconds + BoxRiseSeconds + BoxLandSeconds;
	private const float BoxRevealSeconds = BoxOpeningStarts + BoxOpenSeconds;
	private const float BoxWaitLimitSeconds = 12;
	private bool _boxRevealActive;
	private bool _boxSkipRequested;
	private float _boxRevealAge;
	private string _boxRevealKind = "dig";
	private Vector2 _boxStartFoot, _boxGroundFoot;
	private Vector2 _boxLaunchFoot, _boxLandingFoot;
	private float _boxBaseScale, _boxArtBottom, _boxFlightHeight, _boxDigDepth;
	private Vector2 _boxSavedPatrol, _boxSavedEdit;
	private float _boxSavedWalk, _boxSavedPhase, _boxSavedFall;
	private bool _boxRestoreShop, _boxSavedControlsVisible, _boxSavedPatrolVisible;
	private BoxReward? _boxReward;
	private Action? _boxAcknowledge;
	private BoxRewardCard? _boxRewardWindow;
	private Node2D? _boxVisual;
	private Sprite2D? _boxSprite;
	private Texture2D? _boxClosedTexture, _boxOpenTexture;
	private readonly Queue<(BoxReward Reward, Action? Acknowledge)> _queuedShopRewards = new();

	public bool IsBoxRevealActive => _boxRevealActive;

	// The Shop starts this only after accepting a single foreground transaction.
	// No balance, item, cooldown, or saved movement preference is changed here.
	public void BeginBoxReveal(string kind)
	{
		if (_boxRevealActive || _boxReward != null) return;
		EnsureBoxPresentation();
		_boxRevealKind = NormalizeBoxKind(kind);
		_boxStartFoot = _footAnchor.Position;
		_boxSavedWalk = _walkX; _boxSavedPhase = _stepPhase;
		_boxSavedPatrol = _patrolPosition; _boxSavedEdit = _editPosition; _boxSavedFall = _fallVelocity;
		_boxSavedControlsVisible = _accessoryControls?.Visible == true;
		_boxSavedPatrolVisible = _patrolEditor?.Visible == true;
		if (_accessoryControls != null) { _accessoryControls.CancelGesture(); _accessoryControls.Visible = false; }
		if (_patrolEditor != null) _patrolEditor.Visible = false;
		_boxRestoreShop = _statusWindow?.Visible == true;
		_statusWindow?.Hide();
		_boxRevealAge = 0; _boxSkipRequested = false; _boxReward = null; _boxAcknowledge = null;
		_boxRevealActive = true;
		var extents = GetAppearanceExtents();
		var margin = Mathf.Min(Mathf.Max(extents.HalfWidth + 12, 180), _windowSize.X * 0.5f);
		_boxGroundFoot = new Vector2(Mathf.Clamp(_boxStartFoot.X, margin, _windowSize.X - margin),
			_windowSize.Y - GroundMargin - extents.Bottom);
		var prefix = _boxRevealKind == "dog" ? "Dog" : _boxRevealKind == "accessory" ? "Accessory" : "Dig";
		_boxClosedTexture = ResourceLoader.Load<Texture2D>($"res://Art/UI/Shop/{prefix}BoxClosed.svg");
		_boxOpenTexture = ResourceLoader.Load<Texture2D>($"res://Art/UI/Shop/{prefix}BoxOpen.svg");
		ConfigureBoxTrajectory(extents.HalfWidth);
		_boxSprite!.Texture = _boxClosedTexture;
		_boxSprite.Visible = false;
		_boxVisual!.Visible = true;
		if (_pettingHand != null) _pettingHand.Visible = false;
		_boxRewardWindow!.ShowProgress("Your dog is digging…", _settings?.FontScale ?? 1);
		PositionRewardWindow();
		ApplyBoxRevealPose();
	}

	public void FinishBoxReveal(BoxReward reward, Action? acknowledge = null)
	{
		if (!_boxRevealActive) { ShowShopReward(reward, acknowledge); return; }
		if (_boxReward != null) { _queuedShopRewards.Enqueue((reward, acknowledge)); return; }
		_boxReward = reward;
		_boxAcknowledge = acknowledge;
		// A slow receipt still gets the opening squash/stretch. Only an explicit skip
		// jumps directly to the card; spending and receipt acknowledgment stay separate.
		if (!_boxSkipRequested && _boxRevealAge >= BoxOpeningStarts)
			_boxRevealAge = BoxOpeningStarts;
		if (_boxRevealAge >= BoxRevealSeconds) PresentConfirmedBoxReward();
	}

	public void ShowShopReward(BoxReward reward, Action? acknowledge = null)
	{
		if (_boxRevealActive || _boxReward != null) { _queuedShopRewards.Enqueue((reward, acknowledge)); return; }
		EnsureBoxPresentation();
		_boxReward = reward; _boxAcknowledge = acknowledge;
		PresentConfirmedBoxReward();
	}

	public void FailBoxReveal(string message)
	{
		if (!_boxRevealActive) return;
		// The Shop retains the server's pending/error state. Closing presentation never
		// cancels, retries, or acknowledges an unresolved economic operation.
		_boxReward = null; _boxAcknowledge = null;
		_boxRewardWindow?.Hide();
		RestoreAfterBoxReveal();
	}

	private void EnsureBoxPresentation()
	{
		if (_boxVisual == null)
		{
			_boxVisual = ResourceLoader.Load<PackedScene>("res://UI/BoxRevealVisual.tscn").Instantiate<Node2D>();
			AddChild(_boxVisual);
			_boxSprite = _boxVisual.GetNode<Sprite2D>("Box");
		}
		if (_boxRewardWindow == null)
		{
			_boxRewardWindow = ResourceLoader.Load<PackedScene>("res://UI/BoxRewardCard.tscn").Instantiate<BoxRewardCard>();
			GetTree().Root.AddChild(_boxRewardWindow);
			_boxRewardWindow.Dismissed += DismissBoxReward;
			_boxRewardWindow.EquipRequested += EquipBoxReward;
			_boxRewardWindow.SkipRequested += SkipBoxAnimation;
		}
	}

	private static string NormalizeBoxKind(string kind) => kind.Contains("accessor", StringComparison.OrdinalIgnoreCase)
		? "accessory" : kind.Contains("dog", StringComparison.OrdinalIgnoreCase) ? "dog" : "dig";

	private void ConfigureBoxTrajectory(float appearanceHalfWidth)
	{
		// Reserve both pieces of artwork and their largest squash/stretch. Their visible
		// bottom stays on the floor, rather than treating a transparent sprite margin as feet.
		static Rect2 VisibleBoxBounds(Texture2D? texture)
		{
			if (texture == null) return new Rect2(-128, -128, 256, 256);
			var bounds = AccessoryWardrobe.GetVisibleBounds(texture);
			return new Rect2((Vector2)bounds.Position - texture.GetSize() * 0.5f, bounds.Size);
		}
		var art = VisibleBoxBounds(_boxClosedTexture).Merge(VisibleBoxBounds(_boxOpenTexture));
		var floor = _windowSize.Y - GroundMargin;
		const float edge = 12;
		const float largestStretch = 1.18f;
		var clearance = Mathf.Clamp(_visibleDogSize.X * 0.15f, 28, 60);
		var artHalfWidth = Mathf.Max(Mathf.Abs(art.Position.X), Mathf.Abs(art.End.X));
		_boxBaseScale = Mathf.Min(0.62f, Mathf.Max(0.01f, (floor - edge - 8) / Mathf.Max(1, art.Size.Y * largestStretch)));
		// Shift the digging spot to fit the pair before resorting to a smaller box.
		// Horizontal scaling is needed only if the dog, gap and normal box cannot fit together.
		var roomForBox = _windowSize.X - edge * 2 - appearanceHalfWidth * 2 - clearance;
		_boxBaseScale = Mathf.Min(_boxBaseScale, Mathf.Max(0.01f, roomForBox / Mathf.Max(1, artHalfWidth * 2 * largestStretch)));
		_boxArtBottom = art.End.Y;
		var halfWidth = artHalfWidth * _boxBaseScale * largestStretch;
		var minX = Mathf.Min(edge + halfWidth, _windowSize.X * 0.5f);
		var maxX = Mathf.Max(minX, _windowSize.X - edge - halfWidth);
		var distance = appearanceHalfWidth + halfWidth + clearance;
		var rightRoom = maxX - _boxGroundFoot.X;
		var leftRoom = _boxGroundFoot.X - minX;
		var direction = VisualFacing;
		if ((direction > 0 ? rightRoom : leftRoom) < distance &&
			(direction > 0 ? leftRoom > rightRoom : rightRoom > leftRoom)) direction = -direction;
		var minDogX = Mathf.Min(edge + appearanceHalfWidth, _windowSize.X * 0.5f);
		var maxDogX = Mathf.Max(minDogX, _windowSize.X - edge - appearanceHalfWidth);
		var minGroundX = direction > 0 ? minDogX : Mathf.Max(minDogX, minX + distance);
		var maxGroundX = direction > 0 ? Mathf.Min(maxDogX, maxX - distance) : maxDogX;
		_boxGroundFoot.X = minGroundX <= maxGroundX
			? Mathf.Clamp(_boxGroundFoot.X, minGroundX, maxGroundX)
			: direction > 0 ? minDogX : maxDogX;
		_boxLandingFoot = new Vector2(Mathf.Clamp(_boxGroundFoot.X + direction * distance, minX, maxX), floor);
		_boxLaunchFoot = new Vector2(Mathf.Clamp(_boxGroundFoot.X + VisualFacing * Mathf.Min(_visibleDogSize.X * 0.55f + 30, 95), minX, maxX), floor);
		var headroom = Mathf.Max(0, floor - edge - art.Size.Y * _boxBaseScale * largestStretch);
		_boxFlightHeight = Mathf.Min(Mathf.Clamp(_visibleDogSize.Y * 0.55f + 32, 60, 132), headroom);
		_boxDigDepth = Mathf.Clamp(_visibleDogSize.Y * 0.22f, 14, 50);
	}

	private void UpdateBoxReveal(float delta)
	{
		if (!float.IsFinite(delta) || delta < 0) return;
		_boxRevealAge += delta;
		if (_boxRevealAge >= BoxRevealSeconds && _boxReward != null)
		{
			if (_boxRewardWindow?.ShowingReward != true) PresentConfirmedBoxReward();
		}
		else if (_boxRevealAge >= BoxWaitLimitSeconds)
		{
			FailBoxReveal("Waiting for Steam confirmation. You can continue using the Shop.");
			return;
		}
		else if (_boxRevealAge >= BoxRevealSeconds)
			_boxRewardWindow?.SetProgressText("Waiting for Steam confirmation…");
	}

	private void ApplyBoxRevealPose()
	{
		if (!_boxRevealActive) return;
		var travel = Mathf.SmoothStep(0, 1, Mathf.Clamp(_boxRevealAge / BoxTravelSeconds, 0, 1));
		_footAnchor.Position = _boxStartFoot.Lerp(_boxGroundFoot, travel);
		var digAge = _boxRevealAge - BoxTravelSeconds;
		var digging = digAge >= 0 && digAge < BoxDigSeconds;
		var digEnvelope = digging ? Mathf.Sin(Mathf.Clamp(digAge / BoxDigSeconds, 0, 1) * Mathf.Pi) : 0;
		var facing = VisualFacing;
		var angle = digging ? facing * digEnvelope * (0.35f + 0.08f * Mathf.Sin(digAge * 35)) : 0;
		var squash = digging ? 1 + 0.025f * Mathf.Sin(digAge * 35) * digEnvelope : 1;
		_visualRoot.Scale = new Vector2(facing * _dogScale * squash, _dogScale / squash);
		_visualRoot.Rotation = angle;
		// Pivot around the back of the body, moving the dog and every attachment as a unit.
		var visible = new Rect2(_visibleDogLocalRect.Position + _dogSprite.Position, _visibleDogLocalRect.Size);
		var rear = visible.Position + visible.Size * new Vector2(0.23f, 0.63f);
		var scaledRear = rear * _visualRoot.Scale;
		_visualRoot.Position = scaledRear - scaledRear.Rotated(angle) + Vector2.Down * (_boxDigDepth * digEnvelope);
		foreach (var text in _textAccessories.Values) text.Scale = new Vector2(Mathf.Abs(text.Scale.X) * facing, text.Scale.Y);

		var riseAge = _boxRevealAge - BoxTravelSeconds - BoxDigSeconds;
		if (_boxSprite != null)
		{
			_boxSprite.Visible = riseAge >= 0;
			var flight = Mathf.Clamp(riseAge / BoxRiseSeconds, 0, 1);
			var travelAcross = Mathf.SmoothStep(0, 1, flight);
			var landing = Mathf.Clamp((riseAge - BoxRiseSeconds) / BoxLandSeconds, 0, 1);
			var impact = Mathf.Sin(landing * Mathf.Pi) * 0.18f;
			var opening = Mathf.Clamp((_boxRevealAge - BoxOpeningStarts) / BoxOpenSeconds, 0, 1);
			// A closed box may wait on Steam, but no outcome is ever guessed locally.
			var open = _boxReward != null && opening >= 0.5f;
			_boxSprite.Texture = open ? _boxOpenTexture : _boxClosedTexture;
			var bounce = _boxReward != null ? Mathf.Sin(opening * Mathf.Pi * 2) * 0.12f : 0;
			_boxSprite.Scale = new Vector2(_boxBaseScale * (1 + impact - bounce), _boxBaseScale * (1 - impact + bounce));
			_boxSprite.Modulate = new Color(1, 1, 1, Mathf.Clamp(flight / 0.15f, 0, 1));
			var boxFoot = _boxLaunchFoot.Lerp(_boxLandingFoot, travelAcross);
			boxFoot.Y -= 4 * _boxFlightHeight * flight * (1 - flight);
			_boxVisual!.Position = boxFoot - new Vector2(0, _boxArtBottom * _boxSprite.Scale.Y);
		}
	}

	private void SkipBoxAnimation()
	{
		if (!_boxRevealActive) return;
		_boxSkipRequested = true;
		_boxRevealAge = BoxRevealSeconds;
		ApplyBoxRevealPose();
		if (_boxReward != null) PresentConfirmedBoxReward();
		else _boxRewardWindow?.SetProgressText("Waiting for Steam confirmation…");
	}

	private void PresentConfirmedBoxReward()
	{
		if (_boxReward == null || _boxRewardWindow == null) return;
		// A direct purchase can complete between frames, before DesktopPet's regular
		// ownership reconciliation. Equip must use that new confirmed snapshot now.
		if (_backend.IsAuthenticated) _wardrobe?.SetSteamInventory(_backend.InventoryItems);
		var definition = SteamCosmeticCatalog.Find(_boxReward.ItemDefId);
		var canEquip = definition != null && _wardrobe?.OwnsSteamItem(definition.ItemDefId) == true
			&& (definition.Kind == "dog" || _wardrobe.CanAddCopy(definition.AccessoryId));
		_boxRewardWindow.ShowReward(_boxReward, canEquip, _settings?.FontScale ?? 1);
		PositionRewardWindow();
	}

	private void PositionRewardWindow()
	{
		if (_boxRewardWindow == null) return;
		var screenFoot = (_boxRevealActive ? _boxGroundFoot : _footAnchor.Position) + (Vector2)DisplayServer.WindowGetPosition(MainWindowId);
		var screen = DisplayServer.GetScreenFromRect(new Rect2I((Vector2I)screenFoot, Vector2I.One));
		var usable = DisplayServer.ScreenGetUsableRect(screen >= 0 ? screen : (int)DisplayServer.ScreenPrimary);
		_boxRewardWindow.FitToUsableRect(usable);
		var size = _boxRewardWindow.Size;
		var desired = (Vector2I)(screenFoot + new Vector2(-size.X * 0.5f, -_visibleDogSize.Y - size.Y - 20));
		_boxRewardWindow.Position = new Vector2I(Math.Clamp(desired.X, usable.Position.X, Math.Max(usable.Position.X, usable.End.X - size.X)),
			Math.Clamp(desired.Y, usable.Position.Y, Math.Max(usable.Position.Y, usable.End.Y - size.Y)));
	}

	private void EquipBoxReward()
	{
		var definition = _boxReward == null ? null : SteamCosmeticCatalog.Find(_boxReward.ItemDefId);
		if (definition == null || _wardrobe?.OwnsSteamItem(definition.ItemDefId) != true) return;
		if (definition.Kind == "dog") _wardrobe.SelectDog(definition.ItemDefId);
		else if (_wardrobe.CreatePlacementId(definition.AccessoryId) is { } instanceId)
			_wardrobe.Equip(instanceId, new Vector2(0.5f, 0.2f));
		DismissBoxReward();
	}

	private void DismissBoxReward()
	{
		// Closing the waiting panel merely returns to the Shop; the operation continues.
		var acknowledge = _boxReward == null ? null : _boxAcknowledge;
		_boxReward = null; _boxAcknowledge = null;
		_boxRewardWindow?.Hide();
		RestoreAfterBoxReveal();
		acknowledge?.Invoke();
		if (_queuedShopRewards.TryDequeue(out var next)) ShowShopReward(next.Reward, next.Acknowledge);
	}

	private void RestoreAfterBoxReveal()
	{
		if (!_boxRevealActive) return;
		_boxRevealActive = false;
		_walkX = _boxSavedWalk; _stepPhase = _boxSavedPhase;
		_patrolPosition = _boxSavedPatrol; _editPosition = _boxSavedEdit; _fallVelocity = _boxSavedFall;
		_visualRoot.Position = Vector2.Zero;
		if (_boxVisual != null) _boxVisual.Visible = false;
		if (_accessoryControls != null) _accessoryControls.Visible = _boxSavedControlsVisible;
		if (_patrolEditor != null) _patrolEditor.Visible = _boxSavedPatrolVisible;
		AnimateDog(); UpdateDogMouseRegion();
		if (_boxRestoreShop && _statusWindow != null) _statusWindow.ShowStatusWindow();
		_boxRestoreShop = false;
	}

	private void OnSteamInventoryChanged()
	{
		if (!_shutdown.IsCancellationRequested && _backend.IsAuthenticated)
			_ = _backend.RefreshShopAsync(_shutdown.Token);
	}

	private void DisposeBoxReveal()
	{
		_boxRewardWindow?.QueueFree(); _boxRewardWindow = null;
		_queuedShopRewards.Clear(); _boxAcknowledge = null;
	}
}
