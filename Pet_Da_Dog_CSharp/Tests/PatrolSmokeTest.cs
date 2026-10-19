using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

// Native regression scene. The runner redirects all user preferences to an isolated profile.
public partial class PatrolSmokeTest : Node
{
	private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
	private const string OutputDirectory = "res://.godot/patrol-smoke";
	private readonly string _storagePath = $"{OutputDirectory}/route-{Guid.NewGuid():N}.cfg";
	private readonly string _wardrobePath = $"{OutputDirectory}/outfit-{Guid.NewGuid():N}.cfg";
	private PatrolRoute _route = null!;
	private DesktopPet? _pet;
	private int _assertions;

	public override void _Ready() => CallDeferred(nameof(Run));

	private async void Run()
	{
		var exitCode = 0;
		try
		{
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1", "Patrol smoke requires disabled Steam.");
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_GLOBAL_INPUT") == "1", "Patrol smoke disables physical activity capture so its input assertions stay deterministic.");
			Require(Engine.GetVersionInfo()["string"].AsString().StartsWith("4.6.3", StringComparison.Ordinal), "Patrol smoke uses Godot 4.6.3.");
			Require(DisplayServer.GetName() != "headless", "Patrol smoke requires the real native desktop window.");
			DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(OutputDirectory));
			_route = GetNode<PatrolRoute>("/root/PatrolRoute");
			_route.StoragePath = _storagePath;
			_route.ClearRoute();
			_route.SetTraversalMode(PatrolTraversalMode.Loop);
			VerifyModel();
			VerifyPersistence();
			await VerifyDesktop();
			GD.Print($"PATROL_SMOKE_PASS: {_assertions} assertions");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError($"PATROL_SMOKE_FAIL: {exception}");
		}
		finally
		{
			if (_pet != null) _pet.SetProcess(false);
			if (_route != null) _route.ClearRoute();
			System.IO.File.Delete(ProjectSettings.GlobalizePath(_storagePath));
			System.IO.File.Delete(ProjectSettings.GlobalizePath(_wardrobePath));
			GetTree().Quit(exitCode);
		}
	}

	private void VerifyModel()
	{
		var route = _route;
		var changes = 0;
		var editEvents = new List<bool>();
		void Changed() => changes++;
		void EditingChanged(bool editing) => editEvents.Add(editing);
		route.Changed += Changed; route.EditingChanged += EditingChanged;
		try
		{
			Require(route.Points.Count == 0 && !route.Enabled && !route.IsEditing && !route.CanFinish && route.TraversalMode == PatrolTraversalMode.Loop, "An empty route is disabled and defaults to loop traversal.");
			route.SetEnabled(true);
			Require(!route.Enabled && changes == 0, "An empty route cannot be enabled.");
			Require(!route.AddPoint(Vector2.Zero) && !route.MovePoint(0, Vector2.One) && !route.RemovePoint(0) && !route.UndoLastPoint() && !route.ClearDraft(), "Draft mutation requires an editing session.");
			Require(route.BeginEdit() && !route.BeginEdit() && editEvents.SequenceEqual(new[] { true }), "Starting a route opens one editing session.");
			Require(!route.AddPoint(new Vector2(float.NaN, 0)) && !route.AddPoint(new Vector2(0, float.PositiveInfinity)), "Nonfinite stops are rejected.");
			Require(route.AddPoint(new Vector2(-2, 3)) && route.DraftPoints.Single() == new Vector2(0, 1), "Screen-relative route stops clamp to the usable range.");
			var beforeDuplicate = changes;
			Require(!route.AddPoint(new Vector2(0, 1)) && changes == beforeDuplicate && !route.CanFinish && !route.FinishEdit(), "One stop and duplicate neighboring stops cannot create a route.");
			Require(route.AddPoint(new Vector2(0.6f, 0.3f)) && route.CanFinish && route.Points.Count == 0, "Two distinct draft stops can finish without altering the saved route early.");
			Require(!route.MovePoint(-1, Vector2.Zero) && !route.MovePoint(4, Vector2.Zero) && !route.MovePoint(0, new Vector2(float.NaN, 0)), "Invalid stop edits are ignored.");
			Require(!route.MovePoint(1, route.DraftPoints[0]) && !route.MovePoint(1, route.DraftPoints[1]), "Moving onto a neighbor or keeping the same stop emits no edit.");
			Require(route.MovePoint(1, new Vector2(0.8f, 0.4f)) && route.FinishEdit(), "A valid draft commits its edited positions.");
			Require(route.Enabled && !route.IsEditing && route.DraftPoints.Count == 0 && route.LastSaveSucceeded && editEvents.SequenceEqual(new[] { true, false }), "Finishing enables and saves the route, then closes the editor.");
			var original = route.Points.ToArray();
			Require(route.BeginEdit() && route.DraftPoints.SequenceEqual(original), "Editing an existing route starts from its saved stops.");
			route.MovePoint(0, Vector2.Zero); route.AddPoint(Vector2.One); route.CancelEdit();
			Require(route.Points.SequenceEqual(original) && route.Enabled && !route.IsEditing && route.DraftPoints.Count == 0, "Cancel discards draft positions and preserves the running route.");
			route.SetEnabled(false);
			Require(!route.Enabled && route.Points.SequenceEqual(original), "Disabling keeps the saved route available.");
			var beforeNoop = changes; route.SetEnabled(false); route.CancelEdit();
			Require(changes == beforeNoop, "Repeated disabling and canceling an inactive editor emit no change.");
			route.SetEnabled(true);
			Require(route.Enabled, "A valid saved route can be enabled again.");
			route.BeginEdit(); route.ClearDraft();
			Require(!route.CanFinish && !route.ClearDraft() && route.Points.SequenceEqual(original), "Clearing a draft does not erase the running route.");
			for (var index = 0; index < PatrolRoute.MaxPoints; index++)
				Require(route.AddPoint(new Vector2(index / (float)PatrolRoute.MaxPoints, index % 2)), "The editor accepts each stop up to its limit.");
			Require(!route.AddPoint(Vector2.One) && route.DraftPoints.Count == PatrolRoute.MaxPoints, "The route has a bounded sixteen-stop maximum.");
			Require(route.UndoLastPoint() && route.DraftPoints.Count == PatrolRoute.MaxPoints - 1 && route.AddPoint(Vector2.One), "Undoing a stop frees one slot for a replacement.");
			route.CancelEdit(); route.BeginEdit(); route.ClearDraft();
			route.AddPoint(Vector2.Zero); route.AddPoint(Vector2.One); route.AddPoint(Vector2.Zero);
			Require(route.RemovePoint(1) && route.DraftPoints.Count == 1 && !route.CanFinish, "Removing a middle stop normalizes newly adjacent equal stops.");
			route.ClearRoute();
			Require(!route.IsEditing && !route.Enabled && route.Points.Count == 0 && route.DraftPoints.Count == 0 && editEvents[^1] == false, "Clear route also exits mapping and removes its draft.");
			var beforeMode = changes;
			route.SetTraversalMode(PatrolTraversalMode.BackAndForth);
			Require(route.TraversalMode == PatrolTraversalMode.BackAndForth && changes == beforeMode + 1, "Changing traversal mode updates and notifies the route even before stops are mapped.");
			route.SetTraversalMode(PatrolTraversalMode.BackAndForth);
			Require(changes == beforeMode + 1, "Selecting the existing traversal mode emits no change.");
			route.SetTraversalMode(PatrolTraversalMode.Loop);
		}
		finally { route.Changed -= Changed; route.EditingChanged -= EditingChanged; }
	}

	private void VerifyPersistence()
	{
		var route = _route;
		route.SetTraversalMode(PatrolTraversalMode.BackAndForth);
		CommitRoute(new Vector2(0.2f, 0.4f), new Vector2(0.8f, 0.6f));
		var saved = route.Points.ToArray();
		using (var config = new ConfigFile())
		{
			Require(config.Load(_storagePath) == Error.Ok && config.GetValue("route", "points").VariantType == Variant.Type.PackedVector2Array, "Route persistence stores normalized points as a packed vector array.");
		}
		var loaded = LoadRoute();
		Require(loaded.Enabled && loaded.Points.SequenceEqual(saved) && !loaded.IsEditing && loaded.LastSaveSucceeded && loaded.TraversalMode == PatrolTraversalMode.BackAndForth, "Saved stops, enabled state and traversal mode reload without restoring an unfinished editor."); loaded.Free();
		route.BeginEdit(); route.MovePoint(0, Vector2.One);
		loaded = LoadRoute(); Require(loaded.Points.SequenceEqual(saved), "A draft is never written over the saved route before Finish."); loaded.Free(); route.CancelEdit();
		route.SetEnabled(false); loaded = LoadRoute();
		Require(!loaded.Enabled && loaded.Points.SequenceEqual(saved) && loaded.TraversalMode == PatrolTraversalMode.BackAndForth, "Disabled routes retain their points and traversal preference across restarts."); loaded.Free();
		using (var config = new ConfigFile())
		{
			var points = new Godot.Collections.Array { "invalid", new Vector2(float.NaN, 0), new Vector2(-4, 3), new Vector2(0, 1), new Vector2(0.5f, 0.5f), new Vector2(float.PositiveInfinity, 0) };
			for (var index = 0; index < 24; index++) points.Add(new Vector2(index % 2, index / 24f));
			config.SetValue("route", "points", points); config.SetValue("route", "enabled", true);
			Require(config.Save(_storagePath) == Error.Ok, "Malformed route fixture saves.");
		}
		loaded = LoadRoute();
		Require(loaded.Enabled && loaded.Points.Count == PatrolRoute.MaxPoints && loaded.Points[0] == new Vector2(0, 1) && loaded.Points[1] == new Vector2(0.5f, 0.5f), "Loading filters invalid types/nonfinite stops, clamps bounds, removes adjacent duplicates and limits count.");
		Require(loaded.Points.All(point => point.IsFinite() && point.X >= 0 && point.X <= 1 && point.Y >= 0 && point.Y <= 1), "Every loaded patrol stop is finite and screen-relative."); loaded.Free();
		foreach (var invalidPoints in new Variant[] { "wrong-type", Array.Empty<Vector2>(), new[] { Vector2.One, Vector2.One } })
		{
			using var config = new ConfigFile(); config.SetValue("route", "points", invalidPoints); config.SetValue("route", "enabled", true); config.Save(_storagePath);
			loaded = LoadRoute(); Require(!loaded.Enabled && loaded.Points.Count <= 1, "Invalid or single-stop saves cannot start patrol movement."); loaded.Free();
		}
		using (var config = new ConfigFile())
		{
			config.SetValue("route", "points", saved); config.SetValue("route", "enabled", "true"); config.Save(_storagePath);
		}
		loaded = LoadRoute(); Require(!loaded.Enabled && loaded.Points.SequenceEqual(saved) && loaded.TraversalMode == PatrolTraversalMode.Loop, "Malformed enabled flags preserve stops, and legacy saves default to loop traversal."); loaded.Free();
		foreach (var invalidMode in new Variant[] { "BackAndForth", 99, -1, true })
		{
			using var config = new ConfigFile(); config.SetValue("route", "points", saved); config.SetValue("route", "enabled", true); config.SetValue("route", "traversal_mode", invalidMode);
			Require(config.Save(_storagePath) == Error.Ok, "A malformed traversal fixture saves.");
			loaded = LoadRoute(); Require(loaded.Enabled && loaded.Points.SequenceEqual(saved) && loaded.TraversalMode == PatrolTraversalMode.Loop, "Invalid stored traversal preferences keep the valid route and default to looping."); loaded.Free();
		}
		var missingPath = _storagePath + ".missing";
		loaded = new PatrolRoute { StoragePath = missingPath }; AddChild(loaded);
		Require(!loaded.Enabled && loaded.Points.Count == 0 && loaded.TraversalMode == PatrolTraversalMode.Loop, "Missing preferences keep the original walking and loop traversal behavior."); loaded.Free();
		var validPath = route.StoragePath;
		try
		{
			route.StoragePath = OutputDirectory + "/missing-directory/route.cfg";
			route.BeginEdit(); route.ClearDraft(); route.AddPoint(Vector2.Zero); route.AddPoint(Vector2.One);
			Require(route.FinishEdit() && route.Enabled && !route.LastSaveSucceeded, "A failed local save reports its failure while keeping the valid route usable for the current run.");
		}
		finally { route.StoragePath = validPath; route.ClearRoute(); route.SetTraversalMode(PatrolTraversalMode.Loop); }
	}

	private PatrolRoute LoadRoute()
	{
		var route = new PatrolRoute { StoragePath = _storagePath }; AddChild(route); return route;
	}

	private void CommitRoute(params Vector2[] points)
	{
		_route.BeginEdit(); _route.ClearDraft();
		foreach (var point in points) Require(_route.AddPoint(point), "A route fixture adds a distinct stop.");
		Require(_route.FinishEdit(), "A route fixture finishes successfully.");
	}

	private async Task VerifyDesktop()
	{
		var settings = GetNode<PetSettings>("/root/PetSettings");
		settings.DismissWelcome(); settings.SetDogScale(1); settings.SetDogTransparency(1); settings.SetDogClickThrough(false); settings.SetStationary(false); settings.SetBounceEnabled(true);
		var wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
		wardrobe.StoragePath = _wardrobePath; wardrobe.Clear();
		_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>(); AddChild(_pet); _pet.SetProcess(false);
		await SettleFrames();
		CallPrivate(_pet, "OpenStatusWindow");
		var status = GetField<StatusWindow>(_pet, "_statusWindow");
		var editor = GetField<DesktopPatrolEditor>(_pet, "_patrolEditor");
		var toolbar = GetField<PatrolRouteToolbar>(editor, "_toolbar");
		var backend = GetField<BackendPetClient>(_pet, "_backend");
		var initialGrants = backend.PendingGrantCount;
		var usable = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary);
		var middle = new Vector2(usable.Size.X * 0.4f, Mathf.Max(300, usable.Size.Y * 0.5f));
		_pet.MoveEditingDog(middle);
		GetField<Button>(status, "_settingsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(GetField<CheckBox>(status, "_usePatrolCheck").Disabled && GetField<Button>(status, "_clearPatrolButton").Disabled, "Settings cannot enable or clear an absent route.");
		await SaveWindowImage(status, "patrol-settings-empty.png");
		GetField<Button>(status, "_editPatrolButton").EmitSignal(BaseButton.SignalName.Pressed);
		await SettleFrames();
		Require(_route.IsEditing && _pet.IsPatrolEditing && !_pet.IsAccessoryEditing && !status.Visible && toolbar.Visible, "Setting a route replaces the menu with the native mapping toolbar.");
		VerifyNativeGeometry(usable);
		CallPrivate(_pet, "UpdateDogMouseRegion");
		Require(GetField<Rect2I?>(_pet, "_lastMouseRegion") == new Rect2I(Vector2I.Zero, GetField<Vector2I>(_pet, "_windowSize")), "Mapping captures the entire usable route canvas.");
		var blank = new Vector2(25, usable.Size.Y * 0.72f);
		Require(!((bool)CallPrivate(_pet, "IsVisibleDogPixel", blank)!), "The native mapping hit probe uses empty desktop space.");
		var mainHandle = WindowHandle(0);
		var blankHit = WindowFromPoint(ScreenPoint(blank));
		GD.Print($"PATROL_NATIVE mapping-blank: main={mainHandle}, hit={blankHit}, position={ScreenPoint(blank).X},{ScreenPoint(blank).Y}");
		Require(IsWindowOrChild(blankHit, mainHandle), "Native empty desktop space reaches the route overlay while mapping.");
		var done = GetField<Button>(toolbar, "_doneButton");
		var donePoint = (Vector2)toolbar.Position + done.GetGlobalRect().GetCenter();
		var toolbarHandle = WindowHandle(toolbar.GetWindowId());
		var doneHit = WindowFromPoint(new NativePoint((int)donePoint.X, (int)donePoint.Y));
		GD.Print($"PATROL_NATIVE toolbar-done: toolbar={toolbarHandle}, hit={doneHit}, position={donePoint}");
		Require(IsWindowOrChild(doneHit, toolbarHandle), "The native toolbar stays above the full capture canvas and its Done button receives clicks.");
		Require(done.Disabled && GetField<Button>(toolbar, "_undoButton").Disabled && GetField<Button>(toolbar, "_clearButton").Disabled, "An empty mapping draft disables Done, Undo and Clear.");
		var dogPoint = OpaqueDogPoint();
		Mouse(dogPoint, MouseButton.Left, true); Mouse(dogPoint, MouseButton.Left, false);
		Require(_route.DraftPoints.Count == 1 && backend.PendingGrantCount == initialGrants && done.Disabled, "Clicking the dog while mapping creates a stop without granting a pet.");
		var marker = editor.GetMarkerPosition(0); var moved = marker + new Vector2(65, -55);
		Mouse(marker + new Vector2(3, 2), MouseButton.Left, true);
		editor.UpdatePointer(moved + new Vector2(3, 2)); Mouse(moved + new Vector2(3, 2), MouseButton.Left, false);
		Require(_route.DraftPoints.Count == 1 && editor.GetMarkerPosition(0).DistanceTo(moved) < 0.05f, "Dragging a numbered stop preserves the grab offset and edits its normalized position.");
		var beforeInvalid = _route.DraftPoints.ToArray();
		Mouse(new Vector2(float.NaN, 20), MouseButton.Left, true); editor.UpdatePointer(new Vector2(20, float.PositiveInfinity));
		Require(_route.DraftPoints.SequenceEqual(beforeInvalid), "Nonfinite pointer events cannot corrupt route stops.");
		Mouse(editor.GetMarkerPosition(0), MouseButton.Right, true);
		Require(_route.DraftPoints.Count == 0 && !status.Visible && backend.PendingGrantCount == initialGrants, "Right-click removes a marker without opening Status or granting a pet.");
		Mouse(blank, MouseButton.Left, true); Mouse(blank, MouseButton.Left, false);
		Require(_route.DraftPoints.Count == 1 && editor.GetMarkerPosition(0).IsFinite(), "A transparent desktop click adds a safe route stop.");
		GetField<Button>(toolbar, "_undoButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(_route.DraftPoints.Count == 0, "Toolbar Undo removes the latest stop.");
		Mouse(middle - new Vector2(70, 0), MouseButton.Left, true); Mouse(middle - new Vector2(70, 0), MouseButton.Left, false);
		Mouse(middle + new Vector2(70, 0), MouseButton.Left, true); Mouse(middle + new Vector2(70, 0), MouseButton.Left, false);
		Require(!done.Disabled, "Two distinct canvas stops enable Done.");
		GetField<Button>(toolbar, "_clearButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(_route.DraftPoints.Count == 0 && done.Disabled, "Toolbar Clear removes only the draft.");
		toolbar.FindChild("PatrolCancel", true, false).GetNode<Button>(".").EmitSignal(BaseButton.SignalName.Pressed);
		await SettleFrames();
		Require(!_route.IsEditing && !_route.Enabled && !toolbar.Visible && status.Visible && GetField<Control>(status, "_settingsPage").Visible, "Cancel closes mapping and returns to the original Settings page.");

		GetField<Button>(status, "_itemsTabButton").EmitSignal(BaseButton.SignalName.Pressed); _pet.MoveEditingDog(middle);
		GetField<Button>(status, "_settingsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
		GetField<Button>(status, "_editPatrolButton").EmitSignal(BaseButton.SignalName.Pressed); await SettleFrames();
		var first = middle - new Vector2(70, 0);
		var stops = new[] { first, first + new Vector2(0, 70), first + new Vector2(70, 70), first + new Vector2(35, -40) };
		foreach (var stop in stops) { Mouse(stop, MouseButton.Left, true); Mouse(stop, MouseButton.Left, false); }
		Require(_route.DraftPoints.Count == 4 && !done.Disabled, "Vertical, horizontal and diagonal stops create a loop in click order.");
		await SaveWindowImage(toolbar, "patrol-toolbar.png"); await CaptureRouteCanvas(editor);
		var start = GetField<Node2D>(_pet, "_footAnchor").Position;
		done.EmitSignal(BaseButton.SignalName.Pressed); await SettleFrames();
		Require(_route.Enabled && _pet.IsPatrolling && !toolbar.Visible && status.Visible && GetField<Control>(status, "_settingsPage").Visible, "Done saves the route, starts patrol and restores Settings.");
		Require(_pet.PatrolPosition.DistanceTo(start) < 0.05f && _pet.PatrolTargetIndex == 0, "Patrol starts from the current dog position without teleporting to its first stop.");
		var savedStops = _route.Points.ToArray(); var loaded = LoadRoute();
		Require(loaded.Enabled && loaded.Points.SequenceEqual(savedStops), "The toolbar saves exactly the ordered stops mapped on the desktop."); loaded.Free();
		Require(GetField<Label>(status, "_patrolSummary").Text.Contains("1 → 2 → 3 → 4 → 1") && GetField<CheckBox>(status, "_usePatrolCheck").ButtonPressed, "Settings explains the saved visit order and enabled state.");
		await SaveWindowImage(status, "patrol-settings-saved.png");
		CallPrivate(_pet, "UpdateDogMouseRegion");
		Require(!IsWindowOrChild(WindowFromPoint(ScreenPoint(blank)), mainHandle), "After Done, empty desktop space passes through the route overlay again.");
		await VerifyMotion(usable);
		VerifyTraversalPreferences(status, settings);
		VerifyResolutionAndEdges(wardrobe);
		await VerifySuspensionAndExit(status, toolbar, backend, initialGrants, usable);
	}

	private async Task VerifyMotion(Rect2I usable)
	{
		var pet = _pet!; var previous = pet.PatrolPosition; var previousTarget = pet.PatrolTargetIndex;
		var targetTransitions = new List<int>(); var movement = new HashSet<int>();
		var oldFps = Engine.MaxFps; Engine.MaxFps = 60; pet.SetProcess(true);
		try
		{
			for (var frame = 0; frame < 420 && targetTransitions.Count < 5; frame++)
			{
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				var current = pet.PatrolPosition; var target = pet.PatrolTargetIndex; var distance = current.DistanceTo(previous);
				Require(current.IsFinite() && distance < 18, "Actual native frames advance patrol smoothly without teleporting.");
				if (distance > 0.01f) movement.Add(previousTarget);
				if (target != previousTarget)
				{
					Require(target == (previousTarget + 1) % _route.Points.Count, "Patrol visits each numbered stop in order, including the wrap to stop one.");
					targetTransitions.Add(target);
					var stop = pet.PatrolPointToViewport(_route.Points[previousTarget]);
					// A turn can make the direct displacement shorter than the distance
					// traveled after the stop. Check the two segments against elapsed time.
					var travelViaStop = previous.DistanceTo(stop) + stop.DistanceTo(current);
					var frameBudget = 120f * (float)pet.GetProcessDeltaTime();
					Require(Mathf.Abs(travelViaStop - frameBudget) <= 0.2f,
						$"Each transition reaches its stop within the frame's travel budget (via={travelViaStop}, budget={frameBudget}).");
					var outgoing = pet.PatrolPointToViewport(_route.Points[target]) - stop;
					Require(Mathf.Abs(outgoing.Cross(current - stop)) < Math.Max(1, outgoing.Length() * 0.02f)
						&& outgoing.Dot(current - stop) >= -0.05f,
						"After reaching a stop, motion follows the next ordered segment.");
				}
				else
				{
					var targetPoint = pet.PatrolPointToViewport(_route.Points[target]); var direction = targetPoint - previous;
					Require(Mathf.Abs(direction.Cross(current - previous)) < Math.Max(1, direction.Length() * 0.02f), "Movement follows the straight vertical or diagonal segment to its current target.");
					Require(current.DistanceTo(targetPoint) <= previous.DistanceTo(targetPoint) + 0.05f, "Every patrol frame makes progress toward its target.");
				}
				VerifyArtworkInsideViewport();
				if (frame % 12 == 0) VerifyNativeGeometry(usable);
				previous = current; previousTarget = target;
			}
			Require(targetTransitions.SequenceEqual(new[] { 1, 2, 3, 0, 1 }) && movement.Count == 4, "The real desktop dog completes every segment and starts a second loop.");
			Require(GetField<float>(pet, "_stepPhase") > 0, "Patrol retains the dog's walking and bounce animation.");
		}
		finally { pet.SetProcess(false); Engine.MaxFps = oldFps; }
		var position = pet.PatrolPosition; var targetIndex = pet.PatrolTargetIndex;
		foreach (var delta in new[] { float.NaN, float.PositiveInfinity, -1f, 0f }) CallPrivate(pet, "StepPatrol", delta);
		Require(pet.PatrolPosition == position && pet.PatrolTargetIndex == targetIndex, "Nonfinite and nonpositive movement deltas leave patrol state intact.");
	}

	private void VerifyTraversalPreferences(StatusWindow status, PetSettings settings)
	{
		var pet = _pet!; var savedStops = _route.Points.ToArray();
		var mode = status.GetNode<OptionButton>("%PatrolTraversalMode");
		var position = pet.PatrolPosition; var target = pet.PatrolTargetIndex;
		var reverseIndex = mode.GetItemIndex((int)PatrolTraversalMode.BackAndForth);
		Require(reverseIndex >= 0, "Settings offers back-and-forth traversal by its saved mode ID.");
		mode.Select(reverseIndex); mode.EmitSignal(OptionButton.SignalName.ItemSelected, (long)reverseIndex);
		Require(_route.TraversalMode == PatrolTraversalMode.BackAndForth && pet.PatrolPosition == position && pet.PatrolTargetIndex == target && _route.Points.SequenceEqual(savedStops), "Changing traversal through Settings preserves the dog's position, target and saved stops.");
		Require(GetField<Label>(status, "_patrolSummary").Text.Contains("1 → 2 → 3 → 4 → 3 → 2 → 1"), "Settings explains the return through intermediate stops instead of the closing diagonal.");
		VerifyTraversalSequence(savedStops, new[] { 1, 2, 3, 2, 1, 0, 1 });
		VerifyTraversalSequence(new[] { new Vector2(0.4f, 0.5f), new Vector2(0.45f, 0.55f) }, new[] { 1, 0, 1, 0, 1 });
		VerifyTraversalSequence(new[] { new Vector2(0.4f, 0.5f), new Vector2(0.45f, 0.55f), new Vector2(0.4f, 0.5f) }, new[] { 1, 2, 1, 0, 1 });
		CommitRoute(savedStops);
		var size = GetField<Vector2I>(pet, "_windowSize");
		try
		{
			SetField(pet, "_windowSize", Vector2I.One); SetField(pet, "_patrolPosition", pet.PatrolPointToViewport(savedStops[0])); SetField(pet, "_patrolTargetIndex", 0);
			foreach (var traversal in new[] { PatrolTraversalMode.Loop, PatrolTraversalMode.BackAndForth })
			{
				_route.SetTraversalMode(traversal); CallPrivate(pet, "StepPatrol", 1f);
				Require(pet.PatrolPosition.IsFinite() && pet.PatrolTargetIndex >= 0 && pet.PatrolTargetIndex < savedStops.Length, "Fully collapsed screen-space stops stay finite and keep a valid target in either traversal mode.");
			}
		}
		finally { SetField(pet, "_windowSize", size); }
		_route.SetTraversalMode(PatrolTraversalMode.BackAndForth);
		SetField(pet, "_patrolPosition", pet.PatrolPointToViewport(savedStops[^1])); SetField(pet, "_patrolTargetIndex", savedStops.Length - 1); SetField(pet, "_patrolStepDirection", 1);
		CallPrivate(pet, "StepPatrol", 0.05f); CallPrivate(pet, "AnimateDog");
		Require(pet.PatrolTargetIndex == savedStops.Length - 2 && GetField<int>(pet, "_patrolStepDirection") == -1, "Reaching the final stop starts the return toward its adjacent stop.");
		var bounce = status.GetNode<CheckBox>("%BounceEnabled"); var stationary = status.GetNode<CheckBox>("%Stationary");
		bounce.ButtonPressed = false;
		Require(!settings.BounceEnabled && _route.Enabled, "The bounce toggle does not disable the route.");
		position = pet.PatrolPosition; pet._Process(0.1);
		Require(pet.PatrolPosition.DistanceTo(position) > 1 && pet.PatrolPosition.DistanceTo(position) <= 12.05f, "With bounce off, patrol still advances at walking speed.");
		VerifyUprightDog(patrol: true);
		stationary.ButtonPressed = true;
		position = pet.PatrolPosition; target = pet.PatrolTargetIndex;
		var stepDirection = GetField<int>(pet, "_patrolStepDirection"); var phase = GetField<float>(pet, "_stepPhase");
		for (var frame = 0; frame < 12; frame++) pet._Process(1.0 / 60);
		Require(settings.Stationary && _route.Enabled && pet.PatrolPosition == position && pet.PatrolTargetIndex == target && GetField<int>(pet, "_patrolStepDirection") == stepDirection && GetField<float>(pet, "_stepPhase") == phase, "Stationary pauses patrol without losing its return direction, target or animation phase.");
		VerifyUprightDog(patrol: true);
		var reloadedSettings = new PetSettings(); AddChild(reloadedSettings);
		Require(reloadedSettings.Stationary && !reloadedSettings.BounceEnabled, "Stationary and bounce preferences persist across a fresh settings load."); reloadedSettings.Free();
		stationary.ButtonPressed = false; bounce.ButtonPressed = true;
		pet._Process(0.05);
		Require(!settings.Stationary && settings.BounceEnabled && pet.PatrolPosition.DistanceTo(position) > 1 && pet.PatrolTargetIndex == target && GetField<int>(pet, "_patrolStepDirection") == stepDirection, "Resuming movement continues the existing return segment rather than restarting the route.");
		VerifyMirrorPreservesPatrol(status, settings);
	}

	private void VerifyMirrorPreservesPatrol(StatusWindow status, PetSettings settings)
	{
		var pet = _pet!; var flip = status.GetNode<CheckBox>("%FlipHorizontally");
		var stationary = status.GetNode<CheckBox>("%Stationary"); var visual = GetField<Node2D>(pet, "_visualRoot");
		var originalFlip = settings.FlipHorizontally; var originalStationary = settings.Stationary;
		var stops = _route.Points.ToArray();
		try
		{
			stationary.ButtonPressed = true;
			var position = pet.PatrolPosition; var target = pet.PatrolTargetIndex;
			var stepDirection = GetField<int>(pet, "_patrolStepDirection"); var phase = GetField<float>(pet, "_stepPhase");
			var direction = GetField<float>(pet, "_direction"); var walk = GetField<float>(pet, "_walkX"); var scaleX = visual.Scale.X;
			flip.ButtonPressed = !originalFlip;
			Require(Mathf.IsEqualApprox(visual.Scale.X, -scaleX) && pet.PatrolPosition == position && pet.PatrolTargetIndex == target
				&& GetField<int>(pet, "_patrolStepDirection") == stepDirection && GetField<float>(pet, "_direction") == direction
				&& GetField<float>(pet, "_stepPhase") == phase && _route.Points.SequenceEqual(stops),
				"Mirroring a stationary dog on its return route changes its facing while preserving the route, current position, target and return direction.");
			CallPrivate(pet, "StepPatrol", 0.1f);
			Require(pet.PatrolPosition == position && pet.PatrolTargetIndex == target && GetField<float>(pet, "_stepPhase") == phase,
				"A mirrored stationary dog remains paused on the same route segment.");
			stationary.ButtonPressed = false;
			CallPrivate(pet, "StepPatrol", 0.01f);
			var mirroredPosition = pet.PatrolPosition; var mirroredTarget = pet.PatrolTargetIndex;
			var mirroredStep = GetField<int>(pet, "_patrolStepDirection"); var mirroredDirection = GetField<float>(pet, "_direction");
			SetField(pet, "_patrolPosition", position); SetField(pet, "_patrolTargetIndex", target);
			SetField(pet, "_patrolStepDirection", stepDirection); SetField(pet, "_stepPhase", phase);
			SetField(pet, "_direction", direction); SetField(pet, "_walkX", walk);
			flip.ButtonPressed = originalFlip;
			CallPrivate(pet, "StepPatrol", 0.01f); CallPrivate(pet, "AnimateDog");
			Require(pet.PatrolPosition.IsEqualApprox(mirroredPosition) && pet.PatrolTargetIndex == mirroredTarget
				&& GetField<int>(pet, "_patrolStepDirection") == mirroredStep && GetField<float>(pet, "_direction") == mirroredDirection
				&& _route.Points.SequenceEqual(stops),
				"From the same return segment, mirrored and ordinary patrol advance to the same point and target with the same movement direction.");
		}
		finally { flip.ButtonPressed = originalFlip; stationary.ButtonPressed = originalStationary; }
	}

	private void VerifyTraversalSequence(Vector2[] stops, int[] expectedTargets)
	{
		var pet = _pet!; CommitRoute(stops);
		SetField(pet, "_patrolPosition", pet.PatrolPointToViewport(stops[0])); SetField(pet, "_patrolTargetIndex", 0); SetField(pet, "_patrolStepDirection", 1);
		var transitions = new List<int>();
		const float delta = 1f / 60;
		for (var frame = 0; frame < 1200 && transitions.Count < expectedTargets.Length; frame++)
		{
			var previous = pet.PatrolPosition; var previousTarget = pet.PatrolTargetIndex;
			CallPrivate(pet, "StepPatrol", delta); CallPrivate(pet, "AnimateDog");
			var current = pet.PatrolPosition; var target = pet.PatrolTargetIndex;
			Require(current.IsFinite() && current.DistanceTo(previous) <= 120 * delta + 0.05f, "A return-route frame stays within the walking travel budget.");
			if (target != previousTarget)
			{
				Require(Math.Abs(target - previousTarget) == 1, "Back-and-forth traversal always advances to an adjacent numbered stop.");
				var stop = pet.PatrolPointToViewport(stops[previousTarget]);
				Require(Mathf.Abs(previous.DistanceTo(stop) + stop.DistanceTo(current) - 120 * delta) <= 0.1f, "Endpoint reversals spend the remaining frame budget on the adjacent return segment.");
				transitions.Add(target);
			}
			else Require(current.DistanceTo(pet.PatrolPointToViewport(stops[target])) <= previous.DistanceTo(pet.PatrolPointToViewport(stops[target])) + 0.05f, "A return-route frame makes progress toward its current stop.");
			VerifyArtworkInsideViewport();
		}
		Require(transitions.SequenceEqual(expectedTargets), "Back-and-forth visits every forward and return stop, including two-stop and repeated-position routes.");
	}

	private void VerifyUprightDog(bool patrol)
	{
		var pet = _pet!; var visual = GetField<Node2D>(pet, "_visualRoot"); var foot = GetField<Node2D>(pet, "_footAnchor");
		var scale = GetField<float>(pet, "_dogScale"); var direction = GetField<float>(pet, "_direction");
		Require(visual.Scale.IsEqualApprox(new Vector2(direction * scale, scale)) && Mathf.IsZeroApprox(visual.Rotation), "Disabled bounce leaves the dog upright with no squash, stretch or rocking.");
		if (patrol) Require(foot.Position.IsEqualApprox(pet.PatrolPosition), "Disabled bounce places the foot exactly on the patrol path.");
	}

	private void VerifyResolutionAndEdges(AccessoryWardrobe wardrobe)
	{
		var pet = _pet!; var originalSize = GetField<Vector2I>(pet, "_windowSize"); var originalPosition = pet.PatrolPosition;
		var originalPhase = GetField<float>(pet, "_stepPhase"); var originalDirection = GetField<float>(pet, "_direction"); var stops = _route.Points.ToArray();
		try
		{
			SetField(pet, "_windowSize", new Vector2I(800, 600)); var first = pet.PatrolPointToViewport(new Vector2(0.4f, 0.6f));
			SetField(pet, "_windowSize", new Vector2I(1600, 1200)); var second = pet.PatrolPointToViewport(new Vector2(0.4f, 0.6f));
			Require(second.IsEqualApprox(first * 2) && pet.NormalizePatrolPoint(second).IsEqualApprox(new Vector2(0.4f, 0.6f)), "Normalized stops adapt to a new usable resolution and round-trip through viewport coordinates.");
			SetField(pet, "_windowSize", originalSize);
			wardrobe.Equip(AccessoryWardrobe.TextAccessoryId, AccessoryWardrobe.TextMaxPosition); wardrobe.SetTransform(AccessoryWardrobe.TextAccessoryId, 2, 40);
			foreach (var corner in new[] { Vector2.Zero, Vector2.Right, Vector2.One, Vector2.Down })
				foreach (var phase in new[] { 0f, Mathf.Pi * 0.5f }) foreach (var direction in new[] { -1f, 1f })
				{
					SetField(pet, "_patrolPosition", pet.PatrolPointToViewport(corner)); SetField(pet, "_stepPhase", phase); SetField(pet, "_direction", direction);
					CallPrivate(pet, "AnimateDog"); VerifyArtworkInsideViewport();
				}
			Require(_route.Points.SequenceEqual(stops), "Resolution and outfit safety clamping preserve the user's saved route coordinates.");
		}
		finally
		{
			wardrobe.Clear(); SetField(pet, "_windowSize", originalSize); SetField(pet, "_patrolPosition", originalPosition); SetField(pet, "_stepPhase", originalPhase); SetField(pet, "_direction", originalDirection); CallPrivate(pet, "AnimateDog");
		}
	}

	private async Task VerifySuspensionAndExit(StatusWindow status, PatrolRouteToolbar toolbar, BackendPetClient backend, int grants, Rect2I usable)
	{
		var pet = _pet!;
		GetField<Button>(status, "_itemsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
		var paused = pet.PatrolPosition; var target = pet.PatrolTargetIndex; var phase = GetField<float>(pet, "_stepPhase");
		var stepDirection = GetField<int>(pet, "_patrolStepDirection");
		for (var i = 0; i < 12; i++) pet._Process(1.0 / 60);
		Require(pet.IsAccessoryEditing && !pet.IsPatrolling && pet.PatrolPosition == paused && pet.PatrolTargetIndex == target && GetField<int>(pet, "_patrolStepDirection") == stepDirection && GetField<float>(pet, "_stepPhase") == phase, "Items suspends patrol position, target, return direction and walk phase while keeping the route enabled.");
		pet.MoveEditingDog(paused + new Vector2(35, -20)); var edited = pet.EditingPosition;
		GetField<Button>(status, "_dogsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(pet.IsPatrolling && !GetField<bool>(pet, "_fallingToGround") && pet.PatrolTargetIndex == target && GetField<int>(pet, "_patrolStepDirection") == stepDirection && pet.PatrolPosition.IsEqualApprox(edited), "Leaving Items resumes its existing target and return direction from the dog's edited desktop position.");
		pet._Process(0.1);
		Require(pet.PatrolPosition.DistanceTo(edited) > 1 && pet.PatrolPosition.DistanceTo(edited) <= 12.05f, "Resumed patrol moves smoothly back toward the route.");
		status.Hide(); _route.BeginEdit(); await SettleFrames();
		Require(toolbar.Visible && !status.Visible && pet.IsPatrolEditing, "External route editing opens its toolbar without opening Status.");
		var saved = _route.Points.ToArray(); _route.MovePoint(0, Vector2.One);
		toolbar._Input(new InputEventKey { Keycode = Key.Escape, Pressed = true }); await SettleFrames();
		Require(!toolbar.Visible && !status.Visible && pet.IsPatrolling && _route.Points.SequenceEqual(saved), "Toolbar Escape cancels changes and resumes patrol without inventing a menu return.");
		_route.BeginEdit(); await SettleFrames();
		toolbar.EmitSignal(Window.SignalName.CloseRequested); await SettleFrames();
		Require(!_route.IsEditing && !toolbar.Visible && pet.IsPatrolling, "Closing the mapping toolbar cancels the draft and restores patrol input.");
		CallPrivate(pet, "OpenStatusWindow"); GetField<Button>(status, "_settingsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
		GetField<CheckBox>(status, "_usePatrolCheck").ButtonPressed = false;
		Require(!_route.Enabled && !pet.IsPatrolling && GetField<bool>(pet, "_fallingToGround"), "Turning off patrol starts a ground fall while retaining the route.");
		status.GetNode<CheckBox>("%Stationary").ButtonPressed = true;
		var oldFps = Engine.MaxFps; Engine.MaxFps = 60; pet.SetProcess(true);
		try
		{
			for (var frame = 0; frame < 180 && GetField<bool>(pet, "_fallingToGround"); frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Require(!GetField<bool>(pet, "_fallingToGround"), "Disabling patrol still lands through actual native frames with stationary enabled.");
			var stationaryWalk = GetField<float>(pet, "_walkX");
			for (var frame = 0; frame < 12; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Require(GetField<float>(pet, "_walkX") == stationaryWalk, "After landing, stationary keeps the dog at its settled ground position.");
			status.GetNode<CheckBox>("%Stationary").ButtonPressed = false;
			var walk = GetField<float>(pet, "_walkX"); var foot = GetField<Node2D>(pet, "_footAnchor");
			for (var frame = 0; frame < 24; frame++)
			{
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				var screenFoot = foot.GlobalPosition + NativeWindowPosition(); var bottom = ((float, float, float))CallPrivate(pet, "GetAppearanceExtents")!;
				Require(screenFoot.Y <= usable.End.Y - 2 - bottom.Item2 + 0.05f && screenFoot.Y >= usable.End.Y - 2 - bottom.Item2 - 10.1f, "After disabling patrol, continued walking stays at the real usable screen ground.");
				Require(GetTree().Root.Mode == Window.ModeEnum.Windowed && DisplayServer.WindowGetMode(0) == DisplayServer.WindowMode.Windowed, "Returning from patrol never leaves the overlay fullscreen or maximized.");
			}
			Require(GetField<float>(pet, "_walkX") != walk && _route.Points.SequenceEqual(saved), "The original ground walk resumes while the saved route remains available.");
		}
		finally { pet.SetProcess(false); Engine.MaxFps = oldFps; }
		VerifyGroundMotionPreferences(status);
		Require(backend.PendingGrantCount == grants && backend.ConfirmedPets == null, "Route controls, mapping, movement and mode transitions never grant or fake pets.");
		status.Hide(); var opaque = OpaqueDogPoint(); Mouse(opaque, MouseButton.Left, true); Mouse(opaque, MouseButton.Left, false);
		Require(backend.PendingGrantCount == grants, "A local visible dog click provides feedback without duplicating a global activity grant.");
		Mouse(Vector2.Zero, MouseButton.Left, true);
		Require(backend.PendingGrantCount == grants, "Local overlay input never bypasses the single global activity earning path.");
		pet.RecordInputActivity(3);
		Require(backend.PendingGrantCount == grants + 3 && backend.ConfirmedPets == null, "Three observed global input activities enqueue three independent grants without inventing a confirmed total.");
		pet.RecordInputActivity(0); pet.RecordInputActivity(-1);
		Require(backend.PendingGrantCount == grants + 3, "Empty and invalid global activity batches add no grants.");
		CallPrivate(pet, "OpenStatusWindow"); GetField<Button>(status, "_settingsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
		GetField<Button>(status, "_clearPatrolButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(_route.Points.Count == 0 && !pet.IsPatrolling && GetField<CheckBox>(status, "_usePatrolCheck").Disabled, "Clear route restores the empty Settings state and original walk.");
	}

	private void VerifyGroundMotionPreferences(StatusWindow status)
	{
		var pet = _pet!; var stationary = status.GetNode<CheckBox>("%Stationary"); var bounce = status.GetNode<CheckBox>("%BounceEnabled");
		SetField(pet, "_walkX", GetField<Vector2I>(pet, "_windowSize").X * 0.5f);
		bounce.ButtonPressed = false;
		var walk = GetField<float>(pet, "_walkX"); pet._Process(0.1);
		Require(Mathf.Abs(GetField<float>(pet, "_walkX") - walk) > 1 && Mathf.Abs(GetField<float>(pet, "_walkX") - walk) <= 12.05f, "Ground walking continues at its existing speed with bounce disabled.");
		VerifyUprightDog(patrol: false);
		var foot = GetField<Node2D>(pet, "_footAnchor");
		var extents = ((float, float, float))CallPrivate(pet, "GetAppearanceExtents")!;
		Require(Mathf.IsEqualApprox(foot.Position.Y, GetField<Vector2I>(pet, "_windowSize").Y - 2 - extents.Item2), "Bounce off keeps the foot exactly on the ground.");
		bounce.ButtonPressed = true; stationary.ButtonPressed = true;
		walk = GetField<float>(pet, "_walkX"); var phase = GetField<float>(pet, "_stepPhase"); var position = foot.Position;
		for (var frame = 0; frame < 12; frame++) pet._Process(1.0 / 60);
		Require(GetField<float>(pet, "_walkX") == walk && GetField<float>(pet, "_stepPhase") == phase && foot.Position == position, "Stationary pauses ground walking and animation even when bounce remains enabled.");
		VerifyUprightDog(patrol: false);
		stationary.ButtonPressed = false; pet._Process(0.05);
		Require(GetField<float>(pet, "_walkX") != walk, "Turning off stationary resumes ground walking from its paused position.");
	}

	private void VerifyArtworkInsideViewport()
	{
		var pet = _pet!; var bounds = pet.EditableOutfitBounds; var size = GetField<Vector2I>(pet, "_windowSize");
		Require(bounds.Position.X >= -0.1f && bounds.Position.Y >= -0.1f && bounds.End.X <= size.X + 0.1f && bounds.End.Y <= size.Y + 0.1f, "The complete animated outfit stays inside the patrol viewport, including screen-edge stops.");
	}

	private void VerifyNativeGeometry(Rect2I usable)
	{
		var size = new Vector2I(usable.Size.X, usable.Size.Y - 1); var root = GetTree().Root;
		Require(root.Mode == Window.ModeEnum.Windowed && DisplayServer.WindowGetMode(0) == DisplayServer.WindowMode.Windowed, "The patrol overlay stays Windowed rather than exact-screen fullscreen.");
		Require(root.Position == usable.Position && root.Size == size && DisplayServer.WindowGetPosition(0) == usable.Position && DisplayServer.WindowGetSize(0) == size && GetField<Vector2I>(_pet!, "_windowSize") == size, "Native, scene and cached patrol geometry remain synchronized.");
		Require(GetWindowRect(WindowHandle(0), out var rect) && rect.Left == usable.Position.X && rect.Top == usable.Position.Y && rect.Right - rect.Left == size.X && rect.Bottom - rect.Top == size.Y, "The actual Windows patrol rectangle matches the viewport throughout movement.");
	}

	private Vector2 OpaqueDogPoint()
	{
		var dog = _pet!.EditableDog; using var image = dog.Texture.GetImage();
		for (var y = 0; y < image.GetHeight(); y++) for (var x = 0; x < image.GetWidth(); x++)
			if (image.GetPixel(x, y).A > 0.95f) return dog.ToGlobal(new Vector2(x + 0.5f, y + 0.5f) - dog.Texture.GetSize() * 0.5f);
		throw new InvalidOperationException("The dog texture has no opaque click target.");
	}

	private void Mouse(Vector2 position, MouseButton button, bool pressed) => _pet!._Input(new InputEventMouseButton { Position = position, ButtonIndex = button, Pressed = pressed });
	private async Task SettleFrames()
	{
		for (var i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
	}
	private async Task SaveWindowImage(Window window, string name)
	{
		await SettleFrames(); using var image = window.GetTexture().GetImage();
		Require(image.SavePng($"{OutputDirectory}/{name}") == Error.Ok, "The actual native UI viewport saves for visual review.");
	}
	private async Task CaptureRouteCanvas(DesktopPatrolEditor editor)
	{
		await SettleFrames(); using var image = _pet!.GetViewport().GetTexture().GetImage();
		var bounds = _pet.EditableOutfitBounds;
		for (var index = 0; index < _route.DraftPoints.Count; index++) bounds = bounds.Merge(new Rect2(editor.GetMarkerPosition(index) - Vector2.One * 26, Vector2.One * 52));
		bounds = bounds.Grow(16);
		var crop = new Rect2I(new Vector2I(Mathf.FloorToInt(bounds.Position.X), Mathf.FloorToInt(bounds.Position.Y)), new Vector2I(Mathf.CeilToInt(bounds.Size.X), Mathf.CeilToInt(bounds.Size.Y))).Intersection(new Rect2I(Vector2I.Zero, GetField<Vector2I>(_pet, "_windowSize")));
		using var cropped = image.GetRegion(crop);
		Require(cropped.SavePng($"{OutputDirectory}/patrol-route-canvas.png") == Error.Ok, "The route review image contains only the dog and its numbered native markers.");
		Require(image.GetPixel(5, 5).A < 0.01f, "The mapping overlay preserves a transparent desktop background.");
	}

	private void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
		_assertions++;
	}
	private static T GetField<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
	private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
	private static object? CallPrivate(object target, string name, params object[] args) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);
	private static IntPtr WindowHandle(int id) => (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, id);
	private static bool IsWindowOrChild(IntPtr actual, IntPtr expected) => actual == expected || actual != IntPtr.Zero && GetAncestor(actual, 2) == expected;
	private static NativePoint ScreenPoint(Vector2 local)
	{
		var position = DisplayServer.WindowGetPosition(0); return new NativePoint(position.X + (int)local.X, position.Y + (int)local.Y);
	}
	private static Vector2 NativeWindowPosition()
	{
		if (!GetWindowRect(WindowHandle(0), out var rect)) throw new InvalidOperationException("Cannot read the native overlay rectangle.");
		return new Vector2(rect.Left, rect.Top);
	}
	[StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }
	[StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
	[DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
	[DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
	[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
