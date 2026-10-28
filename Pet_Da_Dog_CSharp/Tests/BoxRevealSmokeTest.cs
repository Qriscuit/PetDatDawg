using Godot;
using System;
using System.Reflection;
using System.Threading.Tasks;

// Deterministic receipt fixtures only. This runner cannot request or grant real items.
public partial class BoxRevealSmokeTest : Node
{
	private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
	private DesktopPet _pet = null!;
	private PetSettings _settings = null!;
	private int _assertions;
	public override void _Ready() => CallDeferred(nameof(Run));

	private async void Run()
	{
		var exit = 0;
		try
		{
			foreach (var key in new[] { "PDD_DISABLE_STEAM", "PDD_DISABLE_GLOBAL_INPUT", "PDD_DISABLE_TRAY" })
				Require(System.Environment.GetEnvironmentVariable(key) == "1", $"{key} required.");
			_settings = GetNode<PetSettings>("/root/PetSettings");
			_settings.DismissWelcome(); _settings.SetDogScale(1); _settings.SetDogTransparency(1);
			_settings.SetShowPetCounter(false); _settings.SetDogClickThrough(true);
			GetNode<NativeWindowBridge>("/root/NativeWindowBridge").SetProcess(false);
			var wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
			wardrobe.Clear(); wardrobe.SelectDog(0);
			wardrobe.SetSteamInventory(new[] { new SteamInventoryItem("100", 2004, 2), new SteamInventoryItem("101", 3000, 1) });
			wardrobe.Equip("steam:2004", new Vector2(0.6f, 0.2f));
			_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>();
			AddChild(_pet); _pet.SetProcess(false); _pet.SetProcessInput(false);
			await Settle();
			foreach (var width in new[] { 1920, 3440, 5120 })
				foreach (var flip in new[] { false, true })
					await VerifySequence(width, flip);
			VerifyBoxArtwork();
			await VerifyTrajectoryGeometry();
			await VerifyWalkingStrip();
			VerifyWaitingAndFailure();
			VerifyDirectRewards(wardrobe);
			await VerifyRewardWindowBounds();
			foreach (var path in new[] { "DigPresent1", "DigPresent2", "DigPresent3Bottom", "DigPresent3Top", "DogPresent1", "DogPresent2", "DogPresent3Bottom", "DogPresent3Top", "AccessoryPresent1", "AccessoryPresent2", "AccessoryPresent3Bottom", "AccessoryPresent3Top", "GoldenBones" })
			{
				var texture = ResourceLoader.Load<Texture2D>($"res://Art/UI/Shop/{path}.svg");
				using var image = texture.GetImage();
				Require(image.GetUsedRect().HasArea() && image.GetPixel(0, 0).A == 0, $"{path} has visible art and transparent margins.");
			}
			Require(Field<BackendPetClient>(_pet, "_backend").PendingGrantCount == 0, "Presentation does not enqueue grants.");
			GD.Print($"BOX_REVEAL_SMOKE_PASS: {_assertions} assertions; normal, flipped, wide, delayed and direct rewards.");
		}
		catch (Exception exception) { exit = 1; GD.PushError($"BOX_REVEAL_SMOKE_FAIL: {exception}"); }
		finally { _pet?.QueueFree(); GetTree().Quit(exit); }
	}

	private async Task VerifySequence(int width, bool flip)
	{
		_settings.SetFlipHorizontally(flip); _settings.SetStationary(flip); _settings.SetBounceEnabled(!flip);
		_settings.SetFontScale(flip ? 1.5f : 1);
		var size = new Vector2I(width, 720);
		Set("_windowSize", size); Set("_walkX", width * 0.45f); Set("_stepPhase", 1.75f);
		Set("_patrolPosition", new Vector2(width * 0.4f, 310)); Set("_patrolTargetIndex", 3); Set("_patrolStepDirection", -1);
		Set("_patrolRunning", true);
		Call("SetOverlayGeometry", Vector2I.Zero, size); Call("AnimateDog"); Call("UpdateDogMouseRegion");
		await Settle();
		var foot = _pet.GetNode<Node2D>("FootAnchor"); var visual = foot.GetNode<Node2D>("VisualRoot");
		var start = foot.Position; var route = Field<Vector2>(_pet, "_patrolPosition");
		var nativeSize = DisplayServer.WindowGetSize(0); var nativePosition = DisplayServer.WindowGetPosition(0);
		var acknowledged = 0;
		_pet.BeginBoxReveal(flip ? "premium-dog" : "dig");
		Require(_pet.IsBoxRevealActive, "Accepted purchase begins presentation.");
		var card = Field<BoxRewardCard>(_pet, "_boxRewardWindow");
		Require(!card.ShowingReward, "No reward is shown before a server receipt.");
		Call("UpdateBoxReveal", 1.0f); Call("AnimateDog"); Call("UpdateDogMouseRegion");
		Require(foot.Position.Y > start.Y && Mathf.Abs(visual.Rotation) > 0.05f, "Actual dog travels to the ground and leans to dig.");
		Require(visual.Scale.X * (flip ? -1 : 1) > 0, "Digging preserves horizontal flip.");
		Require(visual.Position.Length() > 1, "Rear pivot offsets the whole dog and outfit.");
		Require(Field<int>(_pet, "_patrolTargetIndex") == 3 && Field<int>(_pet, "_patrolStepDirection") == -1,
			"Digging suspends route traversal without resetting progress.");
		_pet.FinishBoxReveal(new BoxReward(2004, 1, true), () => acknowledged++);
		Require(!card.ShowingReward && acknowledged == 0, "An early receipt waits for presentation and remains unacknowledged.");
		Call("UpdateBoxReveal", 2.1f); Call("AnimateDog"); Call("UpdateDogMouseRegion");
		Require(card.ShowingReward && card.GetNode<Label>("%RewardDetail").Text.Contains("Another copy"), "Confirmed receipt reveals quantity and duplicate information.");
		Require(DisplayServer.WindowGetSize(0) == nativeSize && DisplayServer.WindowGetPosition(0) == nativePosition
			&& Field<Rect2I?>(_pet, "_lastMouseRegion") == new Rect2I(Vector2I.Zero, size), "Animation leaves overlay geometry and drawing clip fixed.");
		Require(_settings.FlipHorizontally == flip && _settings.Stationary == flip && _settings.BounceEnabled == !flip,
			"Presentation never changes saved movement or appearance preferences.");
		if (width == 1920 && !flip && DisplayServer.GetName() != "headless")
		{
			await Settle(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			using var image = _pet.GetViewport().GetTexture().GetImage();
			Require(image.SavePng("res://.godot/box-reveal-smoke.png") == Error.Ok, "Rendered presentation capture saves.");
		}
		card.GetNode<Button>("%KeepReward").EmitSignal(BaseButton.SignalName.Pressed);
		Require(acknowledged == 1 && !_pet.IsBoxRevealActive, "Keep acknowledges once and restores movement.");
		Require(foot.Position.IsEqualApprox(start) && Field<Vector2>(_pet, "_patrolPosition") == route && visual.Position == Vector2.Zero,
			"Dismiss restores original position, route and transform.");
		Set("_patrolRunning", false);
	}

	private void VerifyWaitingAndFailure()
	{
		_pet.BeginBoxReveal("accessory");
		var card = Field<BoxRewardCard>(_pet, "_boxRewardWindow");
		card.GetNode<Button>("%SkipReveal").EmitSignal(BaseButton.SignalName.Pressed);
		Require(!card.ShowingReward && _pet.IsBoxRevealActive, "Skip does not invent a reward or finish the transaction.");
		Call("UpdateBoxReveal", 10f); Call("AnimateDog");
		Require(!_pet.IsBoxRevealActive && !card.Visible, "Delayed confirmations return control without indefinite digging.");
		var acknowledged = 0;
		_pet.FinishBoxReveal(new BoxReward(3000), () => acknowledged++);
		Require(card.ShowingReward && card.Visible, "A late confirmed receipt is still revealed.");
		card.GetNode<Button>("%KeepReward").EmitSignal(BaseButton.SignalName.Pressed);
		Require(acknowledged == 1, "Late receipt is acknowledged normally.");
		_pet.BeginBoxReveal("dig"); _pet.FailBoxReveal("Pending Steam confirmation");
		Require(!_pet.IsBoxRevealActive && !card.Visible, "Failure or uncertainty restores control without a fake reward.");
	}

	private void VerifyBoxArtwork()
	{
		foreach (var (kind, prefix) in new[] { ("dig", "Dig"), ("dog", "Dog"), ("accessory", "Accessory") })
		{
			_pet.BeginBoxReveal(kind);
			var box = Field<Sprite2D>(_pet, "_boxSprite");
			Set("_boxRevealAge", RevealConstant("BoxOpeningStarts") + RevealConstant("BoxOpenSeconds"));
			Call("AnimateDog");
			Require(box.Texture?.ResourcePath == $"res://Art/UI/Shop/{prefix}Present1.svg",
				$"The {kind} offer retains its closed artwork while no confirmed reward exists.");
			_pet.FinishBoxReveal(new BoxReward(2004));
			Set("_boxRevealAge", RevealConstant("BoxOpeningStarts") + RevealConstant("BoxOpenSeconds") * 0.6f);
			Call("AnimateDog");
			Require(box.Texture?.ResourcePath == $"res://Art/UI/Shop/{prefix}Present3Bottom.svg",
				$"The {kind} offer switches to its matching opened artwork after confirmation.");
			_pet.FailBoxReveal("Fixture complete");
		}
	}

	private async Task VerifyTrajectoryGeometry()
	{
		var travel = RevealConstant("BoxTravelSeconds");
		var dig = RevealConstant("BoxDigSeconds");
		var flight = RevealConstant("BoxRiseSeconds");
		var land = RevealConstant("BoxLandSeconds");
		var opening = RevealConstant("BoxOpeningStarts");
		var duration = RevealConstant("BoxRevealSeconds");
		Require(duration >= 2.5f && duration <= 3.5f, "Dig, ejection, landing and opening retain the approximately three-second presentation.");
		foreach (var width in new[] { 640, 1920 })
			foreach (var scale in new[] { PetSettings.MinDogScale, PetSettings.MaxDogScale })
				foreach (var flip in new[] { false, true })
					foreach (var horizontal in new[] { 0.05f, 0.5f, 0.95f })
					{
						_settings.SetDogScale(scale); _settings.SetFlipHorizontally(flip); _settings.SetStationary(true);
						Set("_patrolRunning", false);
						var surface = new Vector2I(width, 720);
						Set("_windowSize", surface); Set("_walkX", width * horizontal);
						Call("SetOverlayGeometry", Vector2I.Zero, surface); Call("AnimateDog");
						var start = _pet.GetNode<Node2D>("FootAnchor").Position;
						_pet.BeginBoxReveal("dig");
						var card = Field<BoxRewardCard>(_pet, "_boxRewardWindow");
						var visual = _pet.GetNode<Node2D>("FootAnchor/VisualRoot");
						Set("_boxRevealAge", travel + dig * 0.5f); Call("AnimateDog");
						var dogSprite = _pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
						var dogRect = Field<Rect2>(_pet, "_visibleDogLocalRect");
						var rear = dogRect.Position + dogSprite.Position + dogRect.Size * new Vector2(0.23f, 0.63f);
						var scaledRear = rear * visual.Scale;
						var pivotOnly = scaledRear - scaledRear.Rotated(visual.Rotation);
						Require(visual.Position.Y - pivotOnly.Y >= 13.9f, "The dog and outfit dip below the rear-pivot pose while digging.");
						Set("_boxRevealAge", travel + dig + flight * 0.5f); Call("AnimateDog");
						var box = Field<Sprite2D>(_pet, "_boxSprite");
						var airborne = SpriteVisibleRect(box);
						Require(box.Visible && airborne.End.Y < surface.Y - 3 && !card.ShowingReward,
							"The closed box bounces above the floor before any reward appears.");
						Require(box.Texture.ResourcePath == "res://Art/UI/Shop/DigPresent1.svg", "The airborne box remains closed.");
						if (width == 1920 && scale == PetSettings.MaxDogScale && !flip && horizontal == 0.5f)
							await CapturePet("box-ejection-smoke.png");
						Set("_boxRevealAge", travel + dig + flight + land * 0.5f); Call("AnimateDog");
						var landed = SpriteVisibleRect(box);
						var foot = _pet.GetNode<Node2D>("FootAnchor");
						var extents = ((float Top, float Bottom, float HalfWidth))Call("GetAppearanceExtents")!;
						var gap = box.GlobalPosition.X >= foot.Position.X
							? landed.Position.X - (foot.Position.X + extents.HalfWidth)
							: foot.Position.X - extents.HalfWidth - landed.End.X;
						Require(gap >= 20, $"The landed box clears the outfit at width {width}, dog scale {scale}, flip {flip}, start {horizontal} (gap {gap}).");
						Require(landed.Position.X >= 0 && landed.End.X <= surface.X,
							"The box lands inside the viewport even when the dog starts at either edge.");
						Require(Mathf.Abs(landed.End.Y - (surface.Y - 2)) <= 2 && box.Scale.X > box.Scale.Y,
							"Landing squash anchors visible box pixels to the floor.");
						if (width == 1920 && scale == PetSettings.MaxDogScale && !flip && horizontal == 0.5f)
							await CapturePet("box-landed-smoke.png");
						_pet.FinishBoxReveal(new BoxReward(2004));
						Require(!card.ShowingReward, "Landing completes before the confirmed reward is revealed.");
						Set("_boxRevealAge", opening + (duration - opening) * 0.6f); Call("AnimateDog");
						Require(box.Texture.ResourcePath == "res://Art/UI/Shop/DigPresent3Bottom.svg" && !card.ShowingReward,
							"The grounded box opens with stretch before the reward card.");
						Set("_boxRevealAge", duration); Call("UpdateBoxReveal", 0f); Call("AnimateDog");
						Require(card.ShowingReward && DisplayServer.WindowGetSize(0) == surface,
							"The confirmed reward follows opening without resizing the overlay.");
						card.GetNode<Button>("%KeepReward").EmitSignal(BaseButton.SignalName.Pressed);
						Require(_pet.GetNode<Node2D>("FootAnchor").Position.IsEqualApprox(start), "Dismissal restores the pre-dig dog position after space adjustments.");
					}
		_settings.SetDogScale(1);
	}

	private static float RevealConstant(string name) => (float)typeof(DesktopPet).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

	private static Rect2 SpriteVisibleRect(Sprite2D sprite)
	{
		var visible = AccessoryWardrobe.GetVisibleBounds(sprite.Texture);
		var rect = new Rect2((Vector2)visible.Position - sprite.Texture.GetSize() * 0.5f, visible.Size);
		var transform = sprite.GlobalTransform;
		var result = new Rect2(transform * rect.Position, Vector2.Zero);
		return result.Expand(transform * rect.End).Expand(transform * new Vector2(rect.End.X, rect.Position.Y))
			.Expand(transform * new Vector2(rect.Position.X, rect.End.Y));
	}

	private async Task CapturePet(string file)
	{
		if (DisplayServer.GetName() == "headless") return;
		await Settle(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		using var image = _pet.GetViewport().GetTexture().GetImage();
		Require(image.SavePng($"res://.godot/{file}") == Error.Ok, "Rendered box trajectory capture saves.");
	}

	private async Task VerifyWalkingStrip()
	{
		_settings.SetStationary(false); _settings.SetFontScale(1.5f);
		Call("MoveOverlayToBottom", true); Call("AnimateDog"); Call("UpdateDogMouseRegion");
		await Settle();
		var size = DisplayServer.WindowGetSize(0);
		Require(size.Y < DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary).Size.Y / 2,
			"Ordinary walking uses the bottom strip rather than a full-screen fixture.");
		_pet.BeginBoxReveal("accessory");
		Call("UpdateBoxReveal", 3f); Call("AnimateDog"); Call("UpdateDogMouseRegion");
		var card = Field<BoxRewardCard>(_pet, "_boxRewardWindow");
		Require(!card.ShowingReward, "A fully elapsed dig still cannot show an unconfirmed result.");
		_pet.FinishBoxReveal(new BoxReward(3000));
		Require(!card.ShowingReward, "Delayed receipt retains the box-opening animation.");
		Call("UpdateBoxReveal", 0.5f); Call("AnimateDog");
		Require(card.ShowingReward && DisplayServer.WindowGetSize(0) == size, "Late reveal works on the unchanged walking strip.");
		var bounds = new Rect2I(card.Position, card.Size);
		Require(DisplayServer.ScreenGetUsableRect(DisplayServer.GetScreenFromRect(bounds)).Encloses(bounds),
			"Reward card at 150% font size remains within the usable monitor.");
		Require((bool)Call("IsPointerOutsideDog")!, "Decorative digging leaves all overlay pixels click-through.");
		if (DisplayServer.GetName() != "headless")
		{
			await Settle(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			using var image = card.GetTexture().GetImage();
			Require(image.SavePng("res://.godot/box-reward-card-smoke.png") == Error.Ok, "Reward card visual capture saves.");
		}
		card.GetNode<Button>("%KeepReward").EmitSignal(BaseButton.SignalName.Pressed);
	}

	private void VerifyDirectRewards(AccessoryWardrobe wardrobe)
	{
		var acknowledged = 0;
		_pet.ShowShopReward(new BoxReward(2004, 1, true), () => acknowledged++);
		var card = Field<BoxRewardCard>(_pet, "_boxRewardWindow");
		Require(card.ShowingReward && !_pet.IsBoxRevealActive, "Direct purchases show a card without digging.");
		card.GetNode<Button>("%EquipReward").EmitSignal(BaseButton.SignalName.Pressed);
		Require(wardrobe.GetEquippedCount("steam:2004") == 2 && acknowledged == 1, "Equip places an unused confirmed accessory copy.");
		_pet.ShowShopReward(new BoxReward(2004));
		Require(!card.GetNode<Button>("%EquipReward").Visible, "All owned copies in use hides the equip action.");
		card.GetNode<Button>("%KeepReward").EmitSignal(BaseButton.SignalName.Pressed);
	}

	private async Task VerifyRewardWindowBounds()
	{
		var card = ResourceLoader.Load<PackedScene>("res://UI/BoxRewardCard.tscn").Instantiate<BoxRewardCard>();
		GetTree().Root.AddChild(card);
		var dismissed = 0;
		card.Dismissed += () => { dismissed++; card.Hide(); };
		try
		{
			foreach (var usable in new[] { new Rect2I(0, 0, 640, 360), new Rect2I(-1280, 0, 1280, 720) })
				foreach (var font in new[] { 0.75f, 1.5f })
				{
					card.ShowProgress("Waiting for Steam confirmation…", font);
					card.FitToUsableRect(usable);
					await Settle(); await Settle();
					VerifyReachableFooter(card, usable, "Progress");
					foreach (var reward in new[] {
						new BoxReward(101, 5),
						new BoxReward(3000, 1, true, new string('W', 180), "res://Art/Dogs/AustralianShepard1.png"),
						new BoxReward(2004), new BoxReward(101, 5) })
					{
						card.ShowReward(reward, reward.ItemDefId == 2004, font);
						card.FitToUsableRect(usable);
						await Settle(); await Settle();
						var stableSize = card.Size;
						for (var i = 0; i < 4; i++) await Settle();
						Require(card.Size == stableSize, "Reward layout settles without growing the native window over subsequent frames.");
						VerifyReachableFooter(card, usable, $"Reward {reward.ItemDefId}");
						var body = card.GetNode<ScrollContainer>("%RewardBodyScroll");
						if (reward.ItemDefId == 101)
						{
							Require(card.GetNode<Button>("%KeepReward").Text == "Done"
								&& body.GetVScrollBar().MaxValue <= body.Size.Y + 2,
								"The compact Golden Bones receipt keeps its icon, amount and Done action visible without scrolling.");
						}
						body.ScrollVertical = 100000;
						await Settle();
						VerifyReachableFooter(card, usable, "Scrolled reward");
						Require(body.GetHScrollBar().MaxValue <= body.Size.X + 2, "Reward text and full-resolution art do not create horizontal overflow.");
						if (reward.ItemDefId == 101 && font == 1.5f && usable.Size.X == 640 && DisplayServer.GetName() != "headless")
						{
							await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
							using var image = card.GetTexture().GetImage();
							Require(image.SavePng("res://.godot/box-bones-reward-smoke.png") == Error.Ok, "Small-screen Golden Bones reward capture saves.");
						}
						var previousDismissed = dismissed;
						card.GetNode<Button>("%KeepReward").EmitSignal(BaseButton.SignalName.Pressed);
						Require(dismissed == previousDismissed + 1 && !card.Visible, "The visible reward confirmation dismisses the popup once.");
					}
				}
		}
		finally { card.QueueFree(); }
	}

	private void VerifyReachableFooter(BoxRewardCard card, Rect2I usable, string label)
	{
		Require(card.Size.X <= usable.Size.X && card.Size.Y <= usable.Size.Y && card.Size.Y > 0,
			$"{label} stays bounded by the available monitor at independent font sizes.");
		var keep = card.GetNode<Button>("%KeepReward");
		Require(keep.IsVisibleInTree() && keep.Size.X > 0 && keep.Size.Y > 0
			&& card.GetVisibleRect().Grow(1).Encloses(keep.GetGlobalRect()),
			$"{label} keeps its confirmation button inside the clickable window.");
		var footer = card.GetNode<Control>("%RewardActions");
		Require(card.GetVisibleRect().Grow(1).Encloses(footer.GetGlobalRect()), $"{label} keeps the whole action footer visible.");
	}

	private void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); _assertions++; }
	private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
	private void Set(string name, object value) => _pet.GetType().GetField(name, Private)!.SetValue(_pet, value);
	private object? Call(string name, params object[] args) => _pet.GetType().GetMethod(name, Private)!.Invoke(_pet, args);
	private async Task Settle() { for (var i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
}
