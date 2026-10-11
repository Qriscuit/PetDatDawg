using Godot;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Run with Run-DogPresetSmoke.ps1: every preference file and ownership fixture is isolated.
public partial class DogPresetSmokeTest : Node
{
	private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
	private readonly string _outfitPath = $"res://.godot/dog-preset-outfit-{Guid.NewGuid():N}.cfg";
	private readonly string _presetPath = $"res://.godot/dog-preset-library-{Guid.NewGuid():N}.cfg";
	private readonly List<AccessoryWardrobe> _fixtures = new();
	private AccessoryWardrobe? _wardrobe;
	private StatusWindow? _menu;
	private BackendPetClient? _backend;
	private int _assertions;
	private sealed record Preference(string Id, Color Tint, float Scale, float Rotation);
	private sealed record Outfit(int RawDog, AccessoryPlacement[] Accessories, string[] Layers, Preference[] Preferences,
		string Text, bool Background, int HistoryDepth, string UndoLabel);

	public override void _Ready() => CallDeferred(nameof(Run));
	private async void Run()
	{
		var exitCode = 0;
		try
		{
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1", "Preset smoke requires Steam disabled and fake ownership.");
			DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://.godot"));
			_wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
			_wardrobe.StoragePath = _outfitPath;
			_wardrobe.PresetStoragePath = _presetPath;
			Require(_wardrobe.Presets.Count == 0, "A fresh isolated profile begins without saved dog presets.");
			GetNode<PetSettings>("/root/PetSettings").DismissWelcome();
			GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession").SetActive(false);
			var dog = SteamCosmeticCatalog.All.Last(item => item.Kind == "dog");
			var accessory = SteamCosmeticCatalog.All.First(item => item.Kind == "accessory");
			var preset = VerifySaveRoundTripAndUndo(_wardrobe, dog, accessory);
			VerifyMalformedPresets(_wardrobe, preset);
			VerifyAtomicOwnershipRejection(_wardrobe, preset, dog, accessory);
			VerifyHiddenCosmeticCapture(_wardrobe, preset, dog, accessory);
			await VerifyMenu(_wardrobe, preset, dog, accessory);
			VerifyDeletion(_wardrobe, preset);
			Require(_backend?.ConfirmedPets == null && _backend?.PendingGrantCount == 0,
				"Saving, applying, previewing and deleting presets never creates a Pets total or queues a grant.");
			GD.Print($"DOG_PRESET_SMOKE_PASS: {_assertions} assertions");
		}
		catch (Exception exception) { exitCode = 1; GD.PushError($"DOG_PRESET_SMOKE_FAIL: {exception}"); }
		finally
		{
			_menu?.QueueFree();
			_backend?.Dispose();
			foreach (var fixture in _fixtures) fixture.Free();
			if (_wardrobe != null) { _wardrobe.Clear(); _wardrobe.SelectDog(0); _wardrobe.Save(); }
			System.IO.File.Delete(ProjectSettings.GlobalizePath(_outfitPath));
			System.IO.File.Delete(ProjectSettings.GlobalizePath(_presetPath));
			GetTree().Quit(exitCode);
		}
	}

	private DogPreset VerifySaveRoundTripAndUndo(AccessoryWardrobe wardrobe, SteamCosmeticDefinition dog,
		SteamCosmeticDefinition accessory)
	{
		ConfigureFullOutfit(wardrobe, dog, accessory);
		var expected = Capture(wardrobe);
		Require(wardrobe.SavePreset("  \t "), "An empty preset name receives a sensible default.");
		var automatic = wardrobe.Presets.Single();
		Require(!string.IsNullOrWhiteSpace(automatic.Name) && !automatic.Name.Any(char.IsControl), "Automatic preset names contain readable plain text.");
		Require(wardrobe.DeletePreset(automatic.Id), "The automatic-name fixture can be removed.");
		Require(wardrobe.SavePreset("Rainy walk"), "A complete owned dog outfit saves as a named preset.");
		var preset = wardrobe.Presets.Single();
		Require(preset.Name == "Rainy walk" && Guid.TryParse(preset.Id, out _) && preset.DogItemDefId == dog.ItemDefId,
			"The saved preset has its own ID, name and selected Steam dog.");
		Require(preset.Accessories.SequenceEqual(expected.Accessories) && preset.LayerOrder.SequenceEqual(expected.Layers),
			"The preset captures complete transforms, tint, text, background and the exact drawing order.");
		Require(wardrobe.CanApplyPreset(preset), "A preset containing owned Steam cosmetics can be applied.");
		Require(preset.Accessories is not AccessoryPlacement[] && preset.LayerOrder is not string[],
			"Published preset lists do not expose their mutable storage arrays.");
		if (preset.Accessories is IList<AccessoryPlacement> accessories)
		{
			Require(accessories.IsReadOnly, "Preset accessory collections reject external mutation.");
			try { accessories[0] = accessories[0] with { Scale = 2 }; throw new InvalidOperationException("Preset list was mutable."); }
			catch (NotSupportedException) { _assertions++; }
		}
		VerifyPreferenceOnlyStorage();
		var restored = LoadFixture();
		var restoredPreset = restored.Presets.Single();
		Require(restoredPreset.Id == preset.Id && restoredPreset.Name == preset.Name && restoredPreset.DogItemDefId == preset.DogItemDefId
			&& restoredPreset.Accessories.SequenceEqual(preset.Accessories) && restoredPreset.LayerOrder.SequenceEqual(preset.LayerOrder),
			"Restart restores the complete named outfit from local preferences.");
		Require(!restored.CanApplyPreset(restoredPreset) && restored.SelectedDogItemDefId == 0
			&& !restored.Equipped.Any(item => item.Id == accessory.AccessoryId),
			"Loading presets never recreates Steam ownership and keeps unowned cosmetics unavailable.");
		restored.SetSteamOwnership(new[] { dog.ItemDefId, accessory.ItemDefId });
		Require(restored.CanApplyPreset(restoredPreset), "Fresh confirmed ownership enables the restored preset.");

		ConfigureDifferentOutfit(wardrobe);
		var before = Capture(wardrobe);
		var changes = 0;
		void CountChange() => changes++;
		wardrobe.Changed += CountChange;
		Require(wardrobe.ApplyPreset(preset.Id), "Applying a saved preset succeeds after validation.");
		wardrobe.Changed -= CountChange;
		Require(changes == 1 && wardrobe.CanUndo, "Applying an outfit emits one complete change and creates one undo step.");
		RequireMatches(wardrobe, expected, "Apply replaces dog, complete accessories and drawing order together.");
		var appliedReload = LoadFixture();
		appliedReload.SetSteamOwnership(new[] { dog.ItemDefId, accessory.ItemDefId });
		RequireMatches(appliedReload, expected, "Applying an outfit persists complete cosmetic preferences for restart.");
		Require(wardrobe.Undo(), "The applied preset can be undone.");
		RequireMatches(wardrobe, before, "One undo restores the previous dog and every previous cosmetic preference.");
		Require(!wardrobe.CanUndo, "Applying one preset adds exactly one undo entry.");
		Require(wardrobe.ApplyPreset(preset.Id), "The same preset remains reusable after undo.");
		var placement = wardrobe.GetPlacement("DragonWing.jpg")!;
		wardrobe.Move(placement.Id, new Vector2(0.47f, 0.61f));
		Require(wardrobe.GetPlacement(placement.Id) is { } moved && moved.Tint == placement.Tint && moved.Scale == placement.Scale
			&& moved.RotationDegrees == placement.RotationDegrees, "Dragging a restored accessory retains the preset's tint and transform.");
		wardrobe.Remove(AccessoryWardrobe.TextAccessoryId);
		wardrobe.Equip(AccessoryWardrobe.TextAccessoryId, Vector2.Zero);
		Require(wardrobe.GetPlacement(AccessoryWardrobe.TextAccessoryId) is { Text: "Hello 🐾", BackgroundVisible: false },
			"Re-equipping restored text uses its saved message and background preference.");
		return preset;
	}

	private void VerifyAtomicOwnershipRejection(AccessoryWardrobe wardrobe, DogPreset preset, SteamCosmeticDefinition dog,
		SteamCosmeticDefinition accessory)
	{
		ConfigureDifferentOutfit(wardrobe);
		wardrobe.SetSteamOwnership(new[] { dog.ItemDefId });
		var before = Capture(wardrobe);
		Require(!wardrobe.CanApplyPreset(preset), "A missing owned Steam accessory disables its complete preset.");
		var changes = 0;
		void CountChange() => changes++;
		wardrobe.Changed += CountChange;
		Require(!wardrobe.ApplyPreset(preset.Id), "A missing accessory rejects the entire application.");
		wardrobe.Changed -= CountChange;
		RequireMatches(wardrobe, before, "Rejected application changes no dog, accessory, layer or preference.", includeHistory: true);
		Require(changes == 0, "Rejected application emits no partial cosmetic change.");
		wardrobe.SetSteamOwnership(new[] { accessory.ItemDefId });
		Require(!wardrobe.CanApplyPreset(preset) && !wardrobe.ApplyPreset(preset.Id), "Missing dog ownership also rejects the entire outfit.");
		RequireMatches(wardrobe, before, "A rejected dog application preserves the prior complete outfit.", includeHistory: true);
		wardrobe.BeginEdit("unfinished drag");
		wardrobe.Move("SquareCharm.png", new Vector2(0.18f, 0.23f));
		var duringEdit = Capture(wardrobe);
		Require(!wardrobe.ApplyPreset(preset.Id), "Ownership rejection remains safe during a live editing gesture.");
		RequireMatches(wardrobe, duringEdit, "A failed preset does not finish the gesture or consume undo history.", includeHistory: true);
		wardrobe.CancelEdit();
		RequireMatches(wardrobe, before, "Cancel still restores the unfinished gesture after a rejected preset.", includeHistory: true);
		wardrobe.SetSteamOwnership(new[] { dog.ItemDefId, accessory.ItemDefId });
		Require(wardrobe.CanApplyPreset(preset), "Returning confirmed ownership enables the retained preset.");
	}

	private void VerifyMalformedPresets(AccessoryWardrobe wardrobe, DogPreset preset)
	{
		var first = preset.Accessories[0];
		DogPreset WithPlacement(AccessoryPlacement item) => preset with { Accessories = new[] { item }.Concat(preset.Accessories.Skip(1)).ToArray() };
		var invalid = new (DogPreset Preset, string Reason)[]
		{
			(preset with { Id = "missing-guid" }, "invalid preset ID"),
			(preset with { Name = "" }, "empty name"),
			(preset with { Name = "line\nbreak" }, "control character in name"),
			(preset with { Name = new string('a', 65) }, "overlong name"),
			(preset with { DogItemDefId = -1 }, "negative dog definition"),
			(preset with { DogItemDefId = 2000 }, "an accessory definition used as a dog"),
			(preset with { DogItemDefId = int.MaxValue }, "unknown dog definition"),
			(WithPlacement(first with { Id = "missing.png" }), "unknown accessory"),
			(preset with { Accessories = preset.Accessories.Append(first).ToArray() }, "duplicate accessory"),
			(WithPlacement(first with { Position = new Vector2(float.NaN, 0) }), "nonfinite position"),
			(WithPlacement(first with { Position = new Vector2(-1, 0) }), "out of bounds image position"),
			(WithPlacement(first with { Scale = float.PositiveInfinity }), "nonfinite scale"),
			(WithPlacement(first with { Scale = 0.49f }), "undersized scale"),
			(WithPlacement(first with { Scale = 2.01f }), "oversized scale"),
			(WithPlacement(first with { RotationDegrees = float.NaN }), "nonfinite rotation"),
			(WithPlacement(first with { RotationDegrees = 181 }), "out of bounds rotation"),
			(WithPlacement(first with { Tint = new Color(float.NaN, 0, 0, 1) }), "nonfinite tint"),
			(WithPlacement(first with { Tint = new Color(1.01f, 0, 0, 1) }), "out of bounds tint"),
			(WithPlacement(first with { Tint = new Color(1, 1, 1, 0.5f) }), "transparent tint"),
			(WithPlacement(first with { Text = "Unexpected" }), "text on an image accessory"),
			(WithPlacement(first with { BackgroundVisible = false }), "text background field on an image accessory"),
			(preset with { LayerOrder = preset.LayerOrder.Skip(1).ToArray() }, "missing layer"),
			(preset with { LayerOrder = preset.LayerOrder.Append(preset.LayerOrder[0]).ToArray() }, "duplicate layer"),
			(preset with { LayerOrder = preset.LayerOrder.Select(layer => layer == AccessoryWardrobe.DogLayerId ? "@unknown" : layer).ToArray() }, "unknown layer")
		};
		var before = Capture(wardrobe);
		foreach (var (candidate, reason) in invalid)
			Require(!wardrobe.CanApplyPreset(candidate), $"Malformed presets are rejected for {reason}.");
		RequireMatches(wardrobe, before, "Malformed preset validation changes no cosmetic or undo state.", includeHistory: true);
	}

	private void VerifyHiddenCosmeticCapture(AccessoryWardrobe wardrobe, DogPreset preset, SteamCosmeticDefinition dog,
		SteamCosmeticDefinition accessory)
	{
		Require(wardrobe.ApplyPreset(preset.Id), "The owned fixture is restored before ownership-loss capture.");
		wardrobe.SetSteamOwnership(Array.Empty<int>());
		Require(wardrobe.SavePreset("Starter outfit"), "A visible Starter outfit can be saved after losing Steam cosmetics.");
		var starter = wardrobe.Presets[^1];
		Require(starter.DogItemDefId == 0 && !starter.Accessories.Any(item => item.Id == accessory.AccessoryId),
			"Saving current appearance captures Starter and excludes hidden unowned accessories.");
		var visible = starter.Accessories.Select(item => item.Id).Append(AccessoryWardrobe.DogLayerId).Order();
		Require(starter.LayerOrder.Order().SequenceEqual(visible) && starter.LayerOrder.Distinct().Count() == starter.LayerOrder.Count,
			"Saving a visible outfit excludes hidden accessory layers and retains exactly one dog layer.");
		Require(wardrobe.CanApplyPreset(starter), "Starter and local accessories remain usable without Steam ownership.");
		Require(wardrobe.DeletePreset(starter.Id), "Temporary starter fixture deletes cleanly.");
		wardrobe.SetSteamOwnership(new[] { dog.ItemDefId, accessory.ItemDefId });
	}

	private async Task VerifyMenu(AccessoryWardrobe wardrobe, DogPreset preset, SteamCosmeticDefinition dog,
		SteamCosmeticDefinition accessory)
	{
		_backend = new BackendPetClient();
		_menu = ResourceLoader.Load<PackedScene>("res://StatusWindow.tscn").Instantiate<StatusWindow>();
		AddChild(_menu);
		_menu.Configure(GetNode<PetSettings>("/root/PetSettings"));
		_menu.ConfigureSteamInventory(_backend, wardrobe, CancellationToken.None);
		_menu.GetNode<Button>("%ItemsTab").EmitSignal(BaseButton.SignalName.Pressed);
		_menu.ShowStatusWindow();
		await Settle();
		ConfigureDifferentOutfit(wardrobe);
		var localOutfit = Capture(wardrobe);
		var editor = _menu.GetNode<AccessoryEditor>("%AccessoryEditor");
		var name = editor.GetNode<LineEdit>("%PresetName");
		var save = editor.GetNode<Button>("%SavePreset");
		name.Text = "Evening stroll";
		var previousCount = wardrobe.Presets.Count;
		save.EmitSignal(BaseButton.SignalName.Pressed);
		await Settle();
		Require(wardrobe.Presets.Count == previousCount + 1, "The actual Items Save preset button stores one current outfit.");
		var localPreset = wardrobe.Presets[^1];
		Require(localPreset.Name == "Evening stroll" && localPreset.DogItemDefId == 0
			&& localPreset.Accessories.SequenceEqual(localOutfit.Accessories) && localPreset.LayerOrder.SequenceEqual(localOutfit.Layers),
			"Items saves the entered name, selected dog and every visible cosmetic exactly.");
		Require(name.Text.Length == 0 && editor.GetNode<Label>("%PresetSaveStatus").Text.Contains("saved", StringComparison.OrdinalIgnoreCase),
			"A successful menu save clears the name and confirms the saved preset.");
		await CaptureWindow(_menu, "res://.godot/dog-preset-save-smoke.png");
		editor.GetNode<Button>("%OpenPresets").EmitSignal(BaseButton.SignalName.Pressed);
		await Settle();
		var browser = _menu.FindChild("DogPresetsWindow", true, false) as Window
			?? GetTree().Root.FindChild("DogPresetsWindow", true, false) as Window;
		Require(browser != null && browser.Visible, "The Items preset browser opens as an independent window.");
		var grid = browser!.GetNode<GridContainer>("%PresetGrid");
		Require(grid.GetChildCount() == wardrobe.Presets.Count && !browser.GetNode<Label>("%EmptyPresets").Visible,
			"The browser displays one card for every saved outfit and hides the empty-state message.");
		var originalCard = FindCard(grid, preset);
		var originalApply = FindControl<Button>(originalCard, "ApplyPreset");
		var originalPreview = FindControl<DogPresetPreview>(originalCard, "PresetPreview");
		Require(!originalApply.Disabled && originalPreview.Size.X > 0 && originalPreview.Size.Y > 0,
			"An owned preset presents a selectable card with a laid-out outfit preview.");
		Require(originalPreview.DrawLayerIds.SequenceEqual(preset.LayerOrder) && originalPreview.DogBounds.Size.X > 0
			&& originalPreview.DogBounds.Size.Y > 0, "The card preview draws its saved dog and all accessories in their exact saved layer order.");
		Require(originalPreview.ContentBounds.Position.X < originalPreview.DogBounds.Position.X
			&& originalPreview.FittedContentBounds.Position.X >= 0 && originalPreview.FittedContentBounds.Position.Y >= 0
			&& originalPreview.FittedContentBounds.End.X <= originalPreview.Size.X && originalPreview.FittedContentBounds.End.Y <= originalPreview.Size.Y,
			"Preview framing includes outlying text and fits the complete saved outfit inside its card.");
		Require(FindControl<Label>(originalCard, "PresetDetails").Text.Length > 0,
			"Preset cards describe their stored dog or outfit.");
		await CaptureWindow(browser, "res://.godot/dog-presets-smoke.png");
		originalApply.EmitSignal(BaseButton.SignalName.Pressed);
		await Settle();
		Require(wardrobe.SelectedDogItemDefId == dog.ItemDefId && wardrobe.Equipped.SequenceEqual(preset.Accessories)
			&& wardrobe.LayerOrder.SequenceEqual(preset.LayerOrder), "Clicking a preset card applies its stored owned dog and complete outfit.");
		Require(wardrobe.Undo(), "Applying through a card creates one reversible outfit change.");
		RequireMatches(wardrobe, localOutfit, "Undo of a card selection restores the prior dog and complete local outfit.");
		wardrobe.ClearHistory();
		browser.Hide();
		_menu.GetNode<Button>("%DogsTab").EmitSignal(BaseButton.SignalName.Pressed);
		_menu.GetNode<Button>("%OpenDogPresets").EmitSignal(BaseButton.SignalName.Pressed);
		await Settle();
		Require(browser.Visible, "The Dogs page opens the same saved outfit browser.");
		wardrobe.SetSteamOwnership(new[] { dog.ItemDefId });
		await Settle();
		originalCard = FindCard(grid, preset);
		Require(FindControl<Button>(originalCard, "ApplyPreset").Disabled
			&& FindControl<Label>(originalCard, "PresetAvailability").Text.Length > 0,
			"Losing a required Steam accessory retains the card and explains why application is disabled.");
		Require(!FindControl<Button>(FindCard(grid, localPreset), "ApplyPreset").Disabled,
			"Starter presets using local accessories remain selectable when Steam ownership changes.");
		Require(FindControl<DogPresetPreview>(originalCard, "PresetPreview").DrawLayerIds.SequenceEqual(preset.LayerOrder),
			"An unowned preset remains fully previewable without equipping or hiding its stored accessories.");
		await CaptureWindow(browser, "res://.godot/dog-presets-unowned-smoke.png");
		var beforeRejectedCard = Capture(wardrobe);
		FindControl<Button>(originalCard, "ApplyPreset").EmitSignal(BaseButton.SignalName.Pressed);
		await Settle();
		RequireMatches(wardrobe, beforeRejectedCard, "A synthetic disabled-card activation still cannot partially apply an unowned outfit.", includeHistory: true);
		wardrobe.SetSteamOwnership(new[] { dog.ItemDefId, accessory.ItemDefId });
		await Settle();
		Require(!FindControl<Button>(FindCard(grid, preset), "ApplyPreset").Disabled,
			"Fresh ownership re-enables the retained saved preset without saving it again.");
		var localCard = FindCard(grid, localPreset);
		var beforeDelete = Capture(wardrobe);
		FindControl<Button>(localCard, "DeletePreset").EmitSignal(BaseButton.SignalName.Pressed);
		await Settle();
		Require(wardrobe.Presets.All(item => item.Id != localPreset.Id) && grid.GetChildCount() == wardrobe.Presets.Count,
			"A card's Delete button removes that saved outfit and refreshes the browser.");
		RequireMatches(wardrobe, beforeDelete, "Deleting through the browser leaves current cosmetics and history unchanged.", includeHistory: true);
		browser.Hide(); _menu.Hide();
	}

	private void VerifyDeletion(AccessoryWardrobe wardrobe, DogPreset preset)
	{
		var before = Capture(wardrobe);
		Require(!wardrobe.ApplyPreset("missing") && !wardrobe.DeletePreset("missing"), "Unknown preset IDs do not apply or delete another outfit.");
		RequireMatches(wardrobe, before, "Unknown IDs leave the outfit unchanged.", includeHistory: true);
		Require(wardrobe.DeletePreset(preset.Id) && wardrobe.Presets.All(item => item.Id != preset.Id), "Deleting removes only the selected preset.");
		RequireMatches(wardrobe, before, "Deleting a saved preset does not alter the currently displayed outfit.", includeHistory: true);
		Require(LoadFixture().Presets.All(item => item.Id != preset.Id), "Deleted presets remain absent after restart.");
	}

	private void ConfigureFullOutfit(AccessoryWardrobe wardrobe, SteamCosmeticDefinition dog, SteamCosmeticDefinition accessory)
	{
		wardrobe.Clear();
		wardrobe.SetSteamOwnership(new[] { dog.ItemDefId, accessory.ItemDefId });
		wardrobe.SelectDog(dog.ItemDefId);
		wardrobe.SetTint("DragonWing.jpg", new Color(0.2f, 0.4f, 0.6f, 1));
		wardrobe.SetTransform("DragonWing.jpg", 1.4f, -35);
		wardrobe.Equip("DragonWing.jpg", new Vector2(0.24f, 0.17f));
		wardrobe.SetText("Hello 🐾"); wardrobe.SetTextBackgroundVisible(false);
		wardrobe.SetTint(AccessoryWardrobe.TextAccessoryId, new Color(0.7f, 0.3f, 0.1f, 1));
		wardrobe.SetTransform(AccessoryWardrobe.TextAccessoryId, 1.1f, 18);
		wardrobe.Equip(AccessoryWardrobe.TextAccessoryId, new Vector2(-0.3f, 0.92f));
		wardrobe.SetTransform(accessory.AccessoryId, 0.7f, 65);
		wardrobe.Equip(accessory.AccessoryId, new Vector2(0.88f, 0.36f));
		wardrobe.MoveLayer("DragonWing.jpg", -1);
		wardrobe.ClearHistory();
	}
	private static void ConfigureDifferentOutfit(AccessoryWardrobe wardrobe)
	{
		wardrobe.Clear(); wardrobe.SelectDog(0);
		wardrobe.SetTint("DragonWing.jpg", Colors.White); wardrobe.SetTransform("DragonWing.jpg", 0.8f, 5);
		wardrobe.SetTint(AccessoryWardrobe.TextAccessoryId, Colors.White);
		wardrobe.SetTransform(AccessoryWardrobe.TextAccessoryId, 0.9f, -8);
		wardrobe.SetText("Before preset"); wardrobe.SetTextBackgroundVisible(true);
		wardrobe.Equip("SquareCharm.png", new Vector2(0.91f, 0.84f));
		wardrobe.ClearHistory();
	}
	private AccessoryWardrobe LoadFixture()
	{
		var fixture = new AccessoryWardrobe { StoragePath = _outfitPath, PresetStoragePath = _presetPath };
		_fixtures.Add(fixture); AddChild(fixture); return fixture;
	}
	private void VerifyPreferenceOnlyStorage()
	{
		var saved = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(_presetPath));
		foreach (var forbidden in new[] { "owned", "quantity", "balance", "session_token", "steamid", "publisher", "confirmed_pets" })
			Require(!saved.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Preset storage contains no {forbidden} authority or credentials.");
	}
	private Node FindCard(GridContainer grid, DogPreset preset)
	{
		var card = grid.GetChildren().FirstOrDefault(child => child.HasMeta("preset_id") && child.GetMeta("preset_id").AsString() == preset.Id);
		Require(card != null && card.FindChild("PresetName", true, false) is Label label && label.Text == preset.Name,
			$"The preset browser contains the named '{preset.Name}' card with its stable saved ID.");
		return card!;
	}
	private static T FindControl<T>(Node parent, string name) where T : Node => parent.FindChild(name, true, false) as T
		?? throw new InvalidOperationException($"Missing {typeof(T).Name} '{name}' in preset card.");
	private async Task CaptureWindow(Window window, string path)
	{
		if (DisplayServer.GetName() == "headless") return;
		await Settle(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		using var image = window.GetTexture().GetImage();
		Require(image != null && image.GetWidth() > 0 && image.GetHeight() > 0 && image.SavePng(path) == Error.Ok,
			"The preset interface captures a rendered preview for visual review.");
	}
	private static Outfit Capture(AccessoryWardrobe wardrobe) => new(
		(int)typeof(AccessoryWardrobe).GetField("_selectedDogItemDefId", PrivateInstance)!.GetValue(wardrobe)!,
		((List<AccessoryPlacement>)typeof(AccessoryWardrobe).GetField("_equipped", PrivateInstance)!.GetValue(wardrobe)!).ToArray(),
		wardrobe.LayerOrder.ToArray(), wardrobe.Catalog.Select(item => new Preference(item.Id, wardrobe.GetTint(item.Id),
			wardrobe.GetScale(item.Id), wardrobe.GetRotationDegrees(item.Id))).ToArray(), wardrobe.GetText(), wardrobe.GetTextBackgroundVisible(),
		((ICollection)typeof(AccessoryWardrobe).GetField("_history", PrivateInstance)!.GetValue(wardrobe)!).Count, wardrobe.UndoLabel);
	private void RequireMatches(AccessoryWardrobe wardrobe, Outfit expected, string message, bool includeHistory = false)
	{
		var actual = Capture(wardrobe);
		Require(actual.RawDog == expected.RawDog && actual.Accessories.SequenceEqual(expected.Accessories) && actual.Layers.SequenceEqual(expected.Layers)
			&& actual.Preferences.SequenceEqual(expected.Preferences) && actual.Text == expected.Text && actual.Background == expected.Background
			&& (!includeHistory || actual.HistoryDepth == expected.HistoryDepth && actual.UndoLabel == expected.UndoLabel), message);
	}
	private async Task Settle() { for (var i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
	private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _assertions++; }
}
