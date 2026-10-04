using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

// Run with Tests/Run-AccessorySmoke.ps1. All accessory saves stay in .godot.
public partial class AccessorySmokeTest : Node
{
	private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
	private static string OutputDirectory => OS.HasFeature("editor") ? "res://.godot" : "user://accessory-smoke";
	private readonly string _storagePath = $"{OutputDirectory}/accessory-smoke-{Guid.NewGuid():N}.cfg";
	private AccessoryWardrobe? _wardrobe;
	private DesktopPet? _pet;
	private PetSettings? _originalSettings;
	private int _assertions;

	public override void _Ready() => CallDeferred(nameof(Run));

	private async void Run()
	{
		var exitCode = 0;
		try
		{
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1", "Smoke tests require disabled Steam.");
			DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(OutputDirectory));
			_wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
			// The autoload may have read user cosmetics; redirect before any mutations.
			_wardrobe.StoragePath = _storagePath;
			_wardrobe.Clear();
			foreach (var definition in _wardrobe.Catalog.Where(item => item.CanRecolor))
				_wardrobe.SetTint(definition.Id, Colors.White);
			if (OS.GetCmdlineUserArgs().Contains("--landing-only")) await VerifyLandingFixture();
			else if (OS.GetCmdlineUserArgs().Contains("--layers-only")) { VerifyLayerMutationsAndPersistence(); await VerifyLayersFixture(); }
			else
			{
				VerifyCatalogAndMutations();
				VerifyColorMutations();
				VerifyTransformAndUndo();
				VerifyLayerMutationsAndPersistence();
				VerifyTextPersistence();
				VerifyPersistence();
				await VerifyOverlay();
			}
			GD.Print($"ACCESSORY_SMOKE_PASS: {_assertions} assertions");
		}
		catch (Exception exception)
		{
			exitCode = 1;
			GD.PushError($"ACCESSORY_SMOKE_FAIL: {exception}");
		}
		finally
		{
			if (_pet != null) SetField(_pet, "_settings", _originalSettings);
			if (_wardrobe != null)
			{
				_wardrobe.Clear();
				_wardrobe.Save(); // Clear dirty state before deleting the isolated save.
			}
			System.IO.File.Delete(ProjectSettings.GlobalizePath(_storagePath));
			GetTree().Quit(exitCode);
		}
	}

	private void VerifyCatalogAndMutations()
	{
		var wardrobe = _wardrobe!;
		foreach (var id in new[] { "SFlower1.png", "SFlower2.png", "SquareCharm.png" })
		{
			var definition = wardrobe.Find(id);
			Require(definition != null, $"Catalog discovers {id}.");
			Require(definition!.Texture is AtlasTexture atlas && atlas.Region.Size.X < 1024 && atlas.Region.Size.Y < 1024,
				$"Catalog crops transparent margins for {id}.");
			Require(Mathf.IsEqualApprox(Mathf.Max(definition.Size.X, definition.Size.Y), 0.26f), "Accessory maximum size uses dog height.");
			Require(!definition.CanRecolor, $"Existing colored artwork keeps its fixed color: {id}.");
		}
		Require(wardrobe.Catalog.Count == 7 && wardrobe.Find(AccessoryWardrobe.TextAccessoryId)?.IsText == true,
			"Catalog includes all six image accessories and the editable Text Box.");
		foreach (var id in new[] { "DragonWing.jpg", "FairyWing.jpg", "SafetyGlasses.jpg" })
		{
			var definition = wardrobe.Find(id);
			Require(definition != null && definition.CanRecolor, $"White JPEG accessory supports recoloring: {id}.");
			Require(definition!.Texture is AtlasTexture, $"JPEG accessory uses cropped texture bounds: {id}.");
			var atlas = (AtlasTexture)definition.Texture;
			Require(atlas.Region.Size.X < atlas.Atlas.GetWidth() || atlas.Region.Size.Y < atlas.Atlas.GetHeight(),
				$"JPEG background margins are cropped for {id}.");
			using var fullImage = atlas.Atlas.GetImage();
			Require(fullImage.GetPixel(0, 0).A <= 0.03f && fullImage.GetPixel(fullImage.GetWidth() - 1, fullImage.GetHeight() - 1).A <= 0.03f,
				$"JPEG black background corners become transparent: {id}.");
			using var image = definition.Texture.GetImage();
			var transparentPixels = 0;
			var whitePixels = 0;
			var shadedPixels = 0;
			for (var y = 0; y < image.GetHeight(); y += 3)
				for (var x = 0; x < image.GetWidth(); x += 3)
				{
					var pixel = image.GetPixel(x, y);
					if (pixel.A <= 0.03f) transparentPixels++;
					if (pixel.A > 0.9f && pixel.R > 0.75f && pixel.G > 0.75f && pixel.B > 0.75f) whitePixels++;
					if (pixel.A > 0.9f && pixel.R > 0.1f && pixel.R < 0.7f) shadedPixels++;
				}
			Require(transparentPixels > 10 && whitePixels > 10 && shadedPixels > 10,
				$"JPEG cutout preserves white detail and shading without an opaque background rectangle: {id}.");
			Require(Mathf.IsEqualApprox(Mathf.Max(definition.Size.X, definition.Size.Y), 0.26f), "Recolorable accessories use the shared dog-height sizing.");
		}
		Require(wardrobe.Catalog.Select(item => item.Id).Distinct().Count() == wardrobe.Catalog.Count, "Catalog IDs are unique.");
		Require(wardrobe.Find("Doggo.png") == null && wardrobe.Find("PetzHeart.png") == null, "Catalog excludes dog and feedback sprites.");

		var changes = 0;
		void CountChange() => changes++;
		wardrobe.Changed += CountChange;
		wardrobe.Equip("missing.png", Vector2.One);
		wardrobe.Move("SFlower1.png", Vector2.One);
		wardrobe.Equip("SFlower1.png", new Vector2(float.NaN, 0.5f));
		wardrobe.Equip("SFlower1.png", new Vector2(0.5f, float.PositiveInfinity));
		Require(wardrobe.Equipped.Count == 0 && changes == 0, "Invalid IDs, nonfinite positions and moving unequipped items do nothing.");
		wardrobe.Equip("SFlower1.png", new Vector2(-2, 3));
		Require(wardrobe.Equipped.Single().Position == new Vector2(0, 1), "Placement clamps to the dog bounds.");
		wardrobe.Equip("SFlower1.png", new Vector2(0, 1));
		Require(changes == 1, "Unchanged equip emits no redundant update.");
		wardrobe.Equip("SFlower1.png", new Vector2(0.3f, 0.4f));
		Require(wardrobe.Equipped.Count == 1, "Repositioning never duplicates an accessory.");
		wardrobe.Remove("missing.png");
		Require(changes == 2, "Removing an unknown accessory does nothing.");
		wardrobe.Remove("SFlower1.png");
		Require(wardrobe.Equipped.Count == 0 && changes == 3, "Removing equipped items updates the wardrobe.");
		wardrobe.Changed -= CountChange;
	}

	private void VerifyColorMutations()
	{
		var wardrobe = _wardrobe!;
		const string id = "DragonWing.jpg";
		var changes = 0;
		void CountChange() => changes++;
		wardrobe.Changed += CountChange;
		Require(wardrobe.GetTint(id) == Colors.White && wardrobe.GetTint("missing.jpg") == Colors.White,
			"Accessories without a chosen color default to white.");
		wardrobe.SetTint("missing.jpg", Colors.Red);
		wardrobe.SetTint("SFlower1.png", Colors.Red);
		wardrobe.SetTint(id, new Color(float.NaN, 0.5f, 0.5f));
		wardrobe.SetTint(id, new Color(0.5f, float.PositiveInfinity, 0.5f));
		wardrobe.SetTint(id, new Color(0.5f, 0.5f, 0.5f, float.NaN));
		Require(changes == 0 && wardrobe.GetTint(id) == Colors.White && wardrobe.GetTint("SFlower1.png") == Colors.White,
			"Unknown, fixed-color and nonfinite recolors do nothing.");
		wardrobe.SetTint(id, new Color(-2, 3, 0.4f, 0.1f));
		var tint = new Color(0, 1, 0.4f, 1);
		Require(wardrobe.GetTint(id).IsEqualApprox(tint) && wardrobe.Equipped.Count == 0,
			"Color can be chosen before placement, clamps RGB, and keeps accessory opacity.");
		wardrobe.SetTint(id, tint);
		Require(changes == 1, "Unchanged color emits no redundant update.");
		wardrobe.Equip(id, new Vector2(0.3f, 0.4f));
		Require(wardrobe.Equipped.Single().Tint.IsEqualApprox(tint), "Placing an accessory uses its selected color.");
		wardrobe.Move(id, Vector2.One);
		Require(wardrobe.Equipped.Single().Tint.IsEqualApprox(tint), "Dragging preserves the chosen color.");
		wardrobe.SetTint(id, Colors.CornflowerBlue);
		Require(wardrobe.Equipped.Single().Tint == Colors.CornflowerBlue, "Recoloring an equipped accessory updates its placement immediately.");
		wardrobe.Remove(id);
		wardrobe.Equip(id, Vector2.Zero);
		Require(wardrobe.Equipped.Single().Tint == Colors.CornflowerBlue, "Removing and re-equipping retains the chosen color.");
		wardrobe.Clear();
		Require(wardrobe.GetTint(id) == Colors.CornflowerBlue, "Taking off all accessories retains color preferences.");
		wardrobe.SetTint(id, Colors.White);
		wardrobe.Changed -= CountChange;
	}

	private void VerifyTransformAndUndo()
	{
		var wardrobe = _wardrobe!;
		const string id = "SafetyGlasses.jpg";
		wardrobe.Clear(); wardrobe.ClearHistory();
		wardrobe.SetTransform("missing", 2, 90);
		wardrobe.SetTransform(id, float.NaN, 10);
		wardrobe.SetTransform(id, 1, float.PositiveInfinity);
		Require(!wardrobe.CanUndo && wardrobe.GetScale(id) == 1 && wardrobe.GetRotationDegrees(id) == 0,
			"Unknown and nonfinite transforms do not create cosmetic changes or history.");
		wardrobe.SetTransform(id, 100, 999);
		Require(wardrobe.GetScale(id) == 2 && wardrobe.GetRotationDegrees(id) == 180, "Size and rotation clamp at the upper finite limits.");
		Require(wardrobe.Undo() && wardrobe.GetScale(id) == 1 && wardrobe.GetRotationDegrees(id) == 0, "Undo restores pre-placement transform preferences.");
		wardrobe.SetTransform(id, -9, -999);
		Require(wardrobe.GetScale(id) == 0.5f && wardrobe.GetRotationDegrees(id) == -180, "Size and rotation clamp at the lower finite limits.");
		wardrobe.SetTransform(id, 1.6f, 37);
		wardrobe.SetTint(id, Colors.CornflowerBlue);
		wardrobe.Equip(id, new Vector2(0.3f, 0.4f));
		var original = wardrobe.Equipped.Single();
		Require(original.Scale == 1.6f && original.RotationDegrees == 37 && original.Tint == Colors.CornflowerBlue,
			"New placements carry their chosen size, rotation and color.");
		wardrobe.ClearHistory();
		wardrobe.BeginEdit("move accessory");
		for (var index = 0; index < 50; index++) wardrobe.Move(id, new Vector2(index / 49f, 0.5f));
		wardrobe.CommitEdit();
		Require(wardrobe.CanUndo && wardrobe.Undo() && wardrobe.Equipped.Single() == original && !wardrobe.CanUndo,
			"One undo reverses an entire drag and preserves its transform and color.");
		wardrobe.BeginEdit("canceled drag"); wardrobe.Move(id, Vector2.One); wardrobe.CancelEdit();
		Require(wardrobe.Equipped.Single() == original && !wardrobe.CanUndo, "Cancel restores a drag without consuming undo history.");
		wardrobe.Remove(id); Require(wardrobe.Undo() && wardrobe.Equipped.Single() == original, "Undo restores a removed accessory with every cosmetic property.");
		wardrobe.Clear(); Require(wardrobe.Undo() && wardrobe.Equipped.Single() == original, "Undo restores a cleared outfit.");
		wardrobe.BeginEdit("color change"); wardrobe.SetTint(id, Colors.Red); wardrobe.SetTint(id, Colors.Green); wardrobe.CommitEdit();
		Require(wardrobe.Undo() && wardrobe.GetTint(id) == original.Tint, "One undo reverses the color-wheel gesture.");
		Require(wardrobe.Save(), "Transformed outfit saves.");
		var restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Single() == original && !restored.CanUndo, "Restart restores all cosmetic properties without persisting undo history.");
		restored.Free();
		wardrobe.Remove(id); Require(wardrobe.Save(), "Unequipped transform preferences save.");
		restored = LoadIsolatedWardrobe(); restored.Equip(id, Vector2.One);
		Require(restored.Equipped.Single().Scale == original.Scale && restored.Equipped.Single().RotationDegrees == original.RotationDegrees,
			"Re-equipping after restart retains size and rotation."); restored.Free();
		var config = new ConfigFile(); config.SetValue("placements", id, Vector2.Zero);
		config.SetValue("sizes", id, 999f); config.SetValue("rotations", id, -999f);
		Require(config.Save(_storagePath) == Error.Ok, "Out-of-range transform fixture saves.");
		restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Single().Scale == 2 && restored.Equipped.Single().RotationDegrees == -180,
			"Saved transforms are clamped on load."); restored.Free();
		config.SetValue("sizes", id, "bad size"); config.SetValue("rotations", id, new Vector2(1, 2));
		Require(config.Save(_storagePath) == Error.Ok, "Invalid transform fixture saves.");
		restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Single().Scale == 1 && restored.Equipped.Single().RotationDegrees == 0,
			"Invalid saved transform types use safe defaults."); restored.Free();
		wardrobe.Clear(); wardrobe.SetTint(id, Colors.White);
		foreach (var item in wardrobe.Catalog) wardrobe.SetTransform(item.Id, 1, 0);
		wardrobe.ClearHistory();
	}

	private void VerifyLayerMutationsAndPersistence()
	{
		var wardrobe = _wardrobe!; var dog = AccessoryWardrobe.DogLayerId; var a = "FairyWing.jpg"; var b = "SquareCharm.png"; var text = AccessoryWardrobe.TextAccessoryId;
		wardrobe.Clear(); wardrobe.ClearHistory();
		Require(wardrobe.LayerOrder.SequenceEqual(new[] { dog }) && wardrobe.GetLayerIndex("missing") == -1, "An empty outfit has exactly the dog layer and unknown IDs have no layer index.");
		wardrobe.Equip(a, new Vector2(0.4f, 0.4f)); wardrobe.Equip(text, new Vector2(0.5f, -0.2f)); wardrobe.Equip(b, new Vector2(0.6f, 0.6f));
		Require(wardrobe.LayerOrder.SequenceEqual(new[] { dog, a, text, b }), "New image and text accessories append above the dog in equip order.");
		Require(wardrobe.MoveLayer(a, -1) && wardrobe.LayerOrder.SequenceEqual(new[] { a, dog, text, b }), "Moving an accessory down crosses the dog layer by exactly one step.");
		var layers = wardrobe.LayerOrder.ToArray(); wardrobe.ClearHistory();
		var changes = 0; void CountChange() => changes++; wardrobe.Changed += CountChange;
		Require(!wardrobe.MoveLayer("missing", 1) && !wardrobe.MoveLayer("SafetyGlasses.jpg", 1) && !wardrobe.MoveLayer(a, -1) && !wardrobe.MoveLayer(b, 1)
			&& !wardrobe.MoveLayer(dog, 0) && !wardrobe.MoveLayer(dog, 2) && !wardrobe.MoveLayer(dog, -2), "Unknown, unequipped, boundary and invalid-direction layer moves do nothing.");
		Require(changes == 0 && !wardrobe.CanUndo && wardrobe.LayerOrder.SequenceEqual(layers), "Invalid layer actions do not emit changes or create undo history.");
		wardrobe.Changed -= CountChange;
		var outfit = wardrobe.Equipped.ToArray();
		Require(wardrobe.MoveLayer(dog, 1) && wardrobe.LayerOrder.SequenceEqual(new[] { a, text, dog, b }) && wardrobe.Equipped.SequenceEqual(outfit),
			"The dog can move between accessories without changing their placement, text, tint or fit.");
		Require(wardrobe.Undo() && wardrobe.LayerOrder.SequenceEqual(layers) && !wardrobe.CanUndo, "One undo restores a dog-layer move.");
		wardrobe.BeginEdit("layer group"); wardrobe.MoveLayer(dog, 1); wardrobe.MoveLayer(a, 1); wardrobe.CommitEdit();
		Require(wardrobe.Undo() && wardrobe.LayerOrder.SequenceEqual(layers) && !wardrobe.CanUndo, "A grouped layer gesture undoes in one step.");
		wardrobe.BeginEdit("cancel layer change"); wardrobe.MoveLayer(text, -1); wardrobe.CancelEdit();
		Require(wardrobe.LayerOrder.SequenceEqual(layers) && !wardrobe.CanUndo, "Cancel restores a layer transaction without adding history.");
		wardrobe.Move(a, new Vector2(0.65f, 0.3f)); wardrobe.SetTint(a, Colors.CornflowerBlue); wardrobe.SetTransform(a, 1.7f, 37);
		wardrobe.SetText("Layers stay put"); wardrobe.SetTextBackgroundVisible(false);
		Require(wardrobe.LayerOrder.SequenceEqual(layers), "Moving, recoloring, fitting and editing text preserve the chosen layer order.");
		outfit = wardrobe.Equipped.ToArray(); Require(wardrobe.Save(), "Layered outfit saves.");
		var restored = LoadIsolatedWardrobe();
		Require(restored.LayerOrder.SequenceEqual(layers) && restored.Equipped.SequenceEqual(outfit) && !restored.CanUndo, "Reload preserves the complete layered image/text outfit without saving undo history."); restored.Free();
		var config = new ConfigFile(); Require(config.Load(_storagePath) == Error.Ok && config.GetValue("layers", "order").AsStringArray().SequenceEqual(layers),
			"Layer order is saved as cosmetic IDs in its own config section.");
		wardrobe.ClearHistory(); wardrobe.Remove(a);
		Require(!wardrobe.LayerOrder.Contains(a) && wardrobe.LayerOrder.SequenceEqual(new[] { dog, text, b }), "Taking off an item removes only its layer.");
		Require(wardrobe.Undo() && wardrobe.LayerOrder.SequenceEqual(layers) && wardrobe.Equipped.SequenceEqual(outfit), "Undo restores a removed item at its original layer and with every cosmetic property.");
		wardrobe.ClearHistory(); wardrobe.Clear();
		Require(wardrobe.LayerOrder.SequenceEqual(new[] { dog }) && wardrobe.Undo() && wardrobe.LayerOrder.SequenceEqual(layers), "Clear all retains the dog and undo restores the complete layer order.");
		wardrobe.Remove(a); wardrobe.Equip(a, Vector2.One);
		Require(wardrobe.LayerOrder.SequenceEqual(new[] { dog, text, b, a }), "Re-equipping a removed accessory gives it a new top layer.");
		config = new ConfigFile(); config.SetValue("placements", a, Vector2.Zero); config.SetValue("placements", b, Vector2.One);
		Require(config.Save(_storagePath) == Error.Ok, "Legacy layer fixture saves."); restored = LoadIsolatedWardrobe();
		Require(restored.LayerOrder.SequenceEqual(new[] { dog, a, b }), "Legacy outfits default the dog below every item in their original equip order."); restored.Free();
		config.SetValue("layers", "order", new[] { b, b, "missing", "SafetyGlasses.jpg", a });
		Require(config.Save(_storagePath) == Error.Ok, "Malformed packed layer fixture saves."); restored = LoadIsolatedWardrobe();
		Require(restored.LayerOrder.SequenceEqual(new[] { dog, b, a }), "Duplicate, unknown and unequipped layers are removed; an omitted dog is restored below valid saved items."); restored.Free();
		config.SetValue("layers", "order", new Godot.Collections.Array { b, 99, dog, dog, "missing" });
		Require(config.Save(_storagePath) == Error.Ok, "Mixed layer array fixture saves."); restored = LoadIsolatedWardrobe();
		Require(restored.LayerOrder.SequenceEqual(new[] { b, dog, a }), "Mixed arrays retain valid saved layer positions and append any missing equipped item once."); restored.Free();
		config.SetValue("layers", "order", Vector2.One);
		Require(config.Save(_storagePath) == Error.Ok, "Invalid layer type fixture saves."); restored = LoadIsolatedWardrobe();
		Require(restored.LayerOrder.SequenceEqual(new[] { dog, a, b }), "An invalid saved layer type uses legacy dog-first ordering."); restored.Free();
		wardrobe.Clear(); wardrobe.SetTint(a, Colors.White); wardrobe.SetTransform(a, 1, 0); wardrobe.SetText(AccessoryWardrobe.DefaultText); wardrobe.SetTextBackgroundVisible(true); wardrobe.ClearHistory();
	}

	private void VerifyPersistence()
	{
		var wardrobe = _wardrobe!;
		wardrobe.Equip("SquareCharm.png", new Vector2(0.2f, 0.3f));
		wardrobe.Equip("SFlower1.png", new Vector2(0.8f, 0.1f));
		Require(wardrobe.Save(), "Isolated cosmetic save succeeds.");
		var restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.SequenceEqual(wardrobe.Equipped), "Save/reload preserves positions and layer order.");
		restored.Free();

		var config = new ConfigFile();
		Require(config.Load(_storagePath) == Error.Ok, "Saved cosmetic config is readable.");
		Require(config.GetSections().All(section => section is "placements" or "colors" or "sizes" or "rotations" or "text" or "layers"), "Cosmetic save contains only outfit preferences, without pets totals or grants.");
		config.SetValue("placements", "SquareCharm.png", new Vector2(-20, 5));
		config.SetValue("placements", "SFlower2.png", "invalid position");
		config.SetValue("placements", "removed-asset.png", Vector2.One);
		Require(config.Save(_storagePath) == Error.Ok, "Malformed-entry fixture saves.");
		restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Count == 2, "Unknown assets and invalid saved types are ignored.");
		Require(restored.Equipped.First(item => item.Id == "SquareCharm.png").Position == new Vector2(0, 1), "Saved coordinates are clamped on load.");
		restored.Free();

		wardrobe.Clear();
		Require(wardrobe.Save(), "Empty outfit saves.");
		restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Count == 0, "Removed items stay removed on reload.");
		restored.Free();

		VerifyColorPersistence();
	}

	private void VerifyTextPersistence()
	{
		var wardrobe = _wardrobe!; var id = AccessoryWardrobe.TextAccessoryId;
		Require(wardrobe.GetText() == AccessoryWardrobe.DefaultText, "Text Box starts with a readable default message.");
		wardrobe.SetText("a\nb\tc\r");
		Require(wardrobe.GetText() == "a b c ", "Pasted line breaks and tabs normalize to plain text.");
		wardrobe.SetText(string.Concat(Enumerable.Repeat("🐶", 80)));
		Require(System.Globalization.StringInfo.ParseCombiningCharacters(wardrobe.GetText()).Length == 64 && !char.IsHighSurrogate(wardrobe.GetText()[^1]),
			"The text limit preserves Unicode characters without splitting surrogate pairs.");
		wardrobe.SetText("Snacks, please!"); wardrobe.SetTextBackgroundVisible(false); wardrobe.SetTint(id, Colors.Gold); wardrobe.SetTransform(id, 1.4f, 12);
		wardrobe.Equip(id, new Vector2(0.5f, -0.2f));
		var outfit = wardrobe.GetPlacement(id)!;
		Require(outfit.Position.Y < 0 && outfit.Text == "Snacks, please!" && outfit.Tint == Colors.Gold && !outfit.BackgroundVisible,
			"A Text Box can be anchored above the dog with its message and text color.");
		wardrobe.Move(id, new Vector2(-99, 99));
		Require(wardrobe.GetPlacement(id)!.Position == new Vector2(AccessoryWardrobe.TextMinPosition.X, AccessoryWardrobe.TextMaxPosition.Y),
			"Text placement is bounded to the area near the dog.");
		wardrobe.Move(id, outfit.Position); wardrobe.ClearHistory();
		wardrobe.SetTextBackgroundVisible(true);
		Require(wardrobe.GetPlacement(id)!.BackgroundVisible && wardrobe.Undo() && wardrobe.GetPlacement(id) == outfit && !wardrobe.CanUndo,
			"Undo restores a text-background change without changing the message, fit or placement.");
		wardrobe.BeginEdit("text change"); wardrobe.SetText("Hello"); wardrobe.SetText("Hello, friend!"); wardrobe.CommitEdit();
		Require(wardrobe.Undo() && wardrobe.GetPlacement(id) == outfit, "Undo restores an entire text edit with its placement and color.");
		Require(wardrobe.Save(), "Text outfit saves.");
		var restored = LoadIsolatedWardrobe();
		Require(restored.GetPlacement(id) == outfit && restored.GetText() == outfit.Text, "Restart restores text, color, placement, size and rotation."); restored.Free();
		wardrobe.Remove(id); Require(wardrobe.Save(), "Unequipped text saves.");
		restored = LoadIsolatedWardrobe(); restored.Equip(id, outfit.Position);
		Require(restored.GetPlacement(id) == outfit, "Re-equipping retains the edited message and color after restart."); restored.Free();
		wardrobe.Clear(); wardrobe.SetText(AccessoryWardrobe.DefaultText); wardrobe.SetTextBackgroundVisible(true); wardrobe.SetTint(id, Colors.White); wardrobe.SetTransform(id, 1, 0); wardrobe.ClearHistory();
	}

	private void VerifyColorPersistence()
	{
		var wardrobe = _wardrobe!;
		var color = new Color(0.2f, 0.7f, 0.9f, 1);
		wardrobe.SetTint("FairyWing.jpg", color);
		wardrobe.Equip("FairyWing.jpg", new Vector2(0.6f, 0.25f));
		Require(wardrobe.Save(), "Colored outfit saves.");
		var restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Single().Tint.IsEqualApprox(color) && restored.GetTint("FairyWing.jpg").IsEqualApprox(color),
			"Save/reload preserves equipped accessory color.");
		restored.Free();
		wardrobe.Clear();
		Require(wardrobe.Save(), "Unequipped color preferences save.");
		restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Count == 0 && restored.GetTint("FairyWing.jpg").IsEqualApprox(color),
			"Colors reload even when no accessories are equipped.");
		restored.Equip("FairyWing.jpg", Vector2.One);
		Require(restored.Equipped.Single().Tint.IsEqualApprox(color), "Re-equipping after restart uses the saved color.");
		restored.Free();

		var config = new ConfigFile();
		config.SetValue("placements", "DragonWing.jpg", new Vector2(0.4f, 0.2f));
		Require(config.Save(_storagePath) == Error.Ok, "Legacy position-only config saves.");
		restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Single().Tint == Colors.White && restored.GetTint("DragonWing.jpg") == Colors.White && restored.GetTextBackgroundVisible(),
			"Legacy outfits without colors or a text-background preference load with safe readable defaults.");
		restored.Free();

		config.SetValue("colors", "DragonWing.jpg", new Color(-3, 4, 0.6f, 0.2f));
		config.SetValue("colors", "FairyWing.jpg", "invalid color");
		config.SetValue("colors", "SafetyGlasses.jpg", new Vector3(0.5f, 0.5f, 0.5f));
		config.SetValue("colors", "SFlower1.png", Colors.Red);
		config.SetValue("colors", "missing.jpg", Colors.Red);
		config.SetValue("text", "background_visible", "invalid flag");
		Require(config.Save(_storagePath) == Error.Ok, "Malformed saved-color fixture saves.");
		restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Single().Tint.IsEqualApprox(new Color(0, 1, 0.6f, 1)), "Saved colors clamp RGB and normalize alpha on load.");
		Require(restored.GetTint("FairyWing.jpg") == Colors.White && restored.GetTint("SafetyGlasses.jpg") == Colors.White
			&& restored.GetTint("SFlower1.png") == Colors.White && restored.GetTint("missing.jpg") == Colors.White,
			"Invalid saved colors, fixed artwork and missing assets keep the default tint.");
		Require(restored.GetTextBackgroundVisible(), "An invalid saved text-background type uses the visible default.");
		restored.Free();
		wardrobe.SetTint("FairyWing.jpg", Colors.White);
		Require(wardrobe.Save(), "Color persistence fixture restores the isolated empty outfit.");
	}

	private AccessoryWardrobe LoadIsolatedWardrobe()
	{
		var restored = new AccessoryWardrobe { StoragePath = _storagePath };
		AddChild(restored);
		return restored;
	}

	private async Task VerifyOverlay()
	{
		_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>();
		AddChild(_pet);
		_pet.SetProcess(false);
		// Avoid depending on or modifying the user's transparency/click-through settings.
		_originalSettings = GetField<PetSettings>(_pet, "_settings");
		SetField(_pet, "_settings", null);
		await SettleFrames();
		GetField<StatusWindow?>(_pet, "_statusWindow")?.Hide();
		FinishFall();
		var dog = _pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		var wardrobe = _wardrobe!;
		wardrobe.Equip("SFlower1.png", Vector2.Zero);
		wardrobe.Equip("SquareCharm.png", Vector2.One);
		var localBounds = GetField<Rect2>(_pet, "_visibleDogLocalRect");
		Require(dog.GetChildCount() == 2, "Equipping adds exactly two accessory sprites to the dog.");
		var firstSprites = dog.GetChildren().OfType<Sprite2D>().ToArray();
		foreach (var placement in wardrobe.Equipped)
		{
			var definition = wardrobe.Find(placement.Id)!;
			var sprite = firstSprites.Single(item => item.Texture == definition.Texture);
			Require(sprite.Position.IsEqualApprox(localBounds.Position + localBounds.Size * placement.Position), "Overlay placement uses normalized visible dog bounds.");
			Require((sprite.GetRect().Size * sprite.Scale).IsEqualApprox(definition.Size * localBounds.Size.Y), "Overlay size matches the shared preview sizing.");
		}

		wardrobe.Move("SFlower1.png", new Vector2(0.4f, 0.2f));
		Require(dog.GetChildren().OfType<Sprite2D>().All(firstSprites.Contains), "Dragging reuses existing accessory sprites.");
		Require(dog.GetChildCount() == 2, "Repeated placement does not duplicate rendered layers.");
		wardrobe.Move("SFlower1.png", Vector2.Zero);
		VerifyOverlayTint(dog);
		wardrobe.SetTransform("SFlower1.png", 2, 45);
		wardrobe.SetTransform("SquareCharm.png", 2, -135);
		VerifyOverlayBounds(dog);
		VerifyPetInput(dog);
		await VerifyHeartFeedback(dog);
		wardrobe.SetTransform("SFlower1.png", 1, 0);
		wardrobe.SetTransform("SquareCharm.png", 1, 0);
		await VerifyWindowEditor();
		wardrobe.Remove("SFlower1.png");
		Require(dog.GetChildCount() == 1, "Removing an accessory removes its rendered layer immediately.");
		wardrobe.Clear();
		Require(dog.GetChildCount() == 0, "Clear removes every rendered accessory.");
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private void VerifyOverlayTint(Sprite2D dog)
	{
		var wardrobe = _wardrobe!;
		const string id = "SafetyGlasses.jpg";
		var tint = new Color(0.9f, 0.3f, 0.6f, 1);
		wardrobe.SetTint(id, tint);
		wardrobe.Equip(id, new Vector2(0.75f, 0.2f));
		var sprite = dog.GetChildren().OfType<Sprite2D>().Single(item => item.Texture == wardrobe.Find(id)!.Texture);
		Require(sprite.Modulate.IsEqualApprox(tint), "Desktop accessory sprite uses the selected tint.");
		var originalScale = dog.Scale;
		var originalOpacity = dog.Modulate;
		var originalGlobalScale = sprite.GlobalScale;
		dog.Scale *= 1.25f;
		dog.Modulate = new Color(1, 1, 1, 0.35f);
		Require(sprite.GetParent() == dog && sprite.GlobalScale.IsEqualApprox(originalGlobalScale * 1.25f) && sprite.Modulate.IsEqualApprox(tint),
			"Tint remains intact while the accessory inherits dog scaling and transparency.");
		dog.Scale = originalScale;
		dog.Modulate = originalOpacity;
		wardrobe.SetTint(id, Colors.CornflowerBlue);
		Require(dog.GetChildren().Contains(sprite) && sprite.Modulate == Colors.CornflowerBlue, "Live recoloring updates the existing desktop sprite.");
		wardrobe.Move(id, Vector2.One);
		Require(sprite.Modulate == Colors.CornflowerBlue, "Repositioning the desktop sprite preserves tint.");
		wardrobe.Remove(id);
		wardrobe.SetTint(id, Colors.White);
		Require(dog.GetChildCount() == 2, "Recolor verification restores the original outfit.");
	}

	private void VerifyOverlayBounds(Sprite2D dog)
	{
		var (minX, maxX) = ((float, float))CallPrivate(_pet!, "GetWalkBounds")!;
		foreach (var direction in new[] { -1.0f, 1.0f })
		{
			foreach (var x in new[] { minX, maxX })
			{
				for (var phaseIndex = 0; phaseIndex < 24; phaseIndex++)
				{
					SetField(_pet!, "_direction", direction);
					SetField(_pet!, "_walkX", x);
					SetField(_pet!, "_stepPhase", phaseIndex * Mathf.Pi / 6);
					CallPrivate(_pet!, "AnimateDog");
					var windowSize = GetField<Vector2I>(_pet!, "_windowSize");
					foreach (var sprite in dog.GetChildren().OfType<Sprite2D>())
					{
						var rect = sprite.GetRect();
						foreach (var point in new[] { rect.Position, rect.End, new Vector2(rect.Position.X, rect.End.Y), new Vector2(rect.End.X, rect.Position.Y) })
						{
							var global = sprite.ToGlobal(point);
							Require(global.X >= -0.01f && global.X <= windowSize.X + 0.01f && global.Y >= -0.01f && global.Y <= windowSize.Y + 0.01f,
								"Accessories remain inside the overlay while walking, flipping and hopping.");
						}
					}
				}
			}
		}
	}

	private void VerifyPetInput(Sprite2D dog)
	{
		var backend = GetField<BackendPetClient>(_pet!, "_backend");
		var before = backend.PendingGrantCount;
		using var image = dog.Texture.GetImage();
		var solid = FindOpaquePixel(image);
		var solidGlobal = dog.ToGlobal(solid + new Vector2(0.5f, 0.5f) - dog.Texture.GetSize() * 0.5f);
		_pet!._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = solidGlobal });
		Require(backend.PendingGrantCount == before + 1, "One visible dog click enqueues exactly one grant.");
		_pet._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = solidGlobal });
		Require(backend.PendingGrantCount == before + 1, "Mouse release does not add another grant.");
		_pet._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = dog.ToGlobal(-dog.Texture.GetSize() * 0.5f) });
		Require(backend.PendingGrantCount == before + 1, "Transparent dog pixels never enqueue grants.");

		var foundAccessoryOnlyPixel = false;
		foreach (var sprite in dog.GetChildren().OfType<Sprite2D>())
		{
			using var accessoryImage = sprite.Texture.GetImage();
			for (var y = 0; y < accessoryImage.GetHeight() && !foundAccessoryOnlyPixel; y += 8)
			{
				for (var x = 0; x < accessoryImage.GetWidth(); x += 8)
				{
					if (accessoryImage.GetPixel(x, y).A <= 0.5f) continue;
					var point = sprite.ToGlobal(new Vector2(x + 0.5f, y + 0.5f) - sprite.Texture.GetSize() * 0.5f);
					if ((bool)CallPrivate(_pet, "IsVisibleDogPixel", point)!) continue;
					_pet._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = point });
					foundAccessoryOnlyPixel = true;
					break;
				}
			}
		}
		Require(foundAccessoryOnlyPixel, "Edge fixture contains a visible accessory pixel outside the dog.");
		Require(backend.PendingGrantCount == before + 1, "Accessory-only pixels never enqueue pet grants.");
	}

	private AccessoryEditingSession Session => GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession");
	private DesktopAccessoryControls Controls => GetField<DesktopAccessoryControls>(_pet!, "_accessoryControls");
	private Sprite2D Dog => _pet!.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
	private Vector2 At(Vector2 normalized)
	{
		var rect = GetField<Rect2>(_pet!, "_visibleDogLocalRect");
		return Dog.ToGlobal(rect.Position + rect.Size * normalized);
	}
	private void Mouse(Vector2 position, bool pressed) => _pet!._Input(new InputEventMouseButton
	{
		ButtonIndex = MouseButton.Left, Pressed = pressed, Position = position
	});
	private void DesktopKey(Key key, bool ctrl = false) => _pet!._UnhandledKeyInput(new InputEventKey
	{
		Keycode = key, CtrlPressed = ctrl, Pressed = true
	});
	private Node2D AccessoryNode(string id) => _pet!.AccessoryNode(id)!;
	private Vector2 AccessoryPixel(string id)
	{
		var node = AccessoryNode(id);
		if (node is PetTextAccessory) return node.GlobalPosition;
		var sprite = (Sprite2D)node;
		using var image = sprite.Texture.GetImage();
		return sprite.ToGlobal(FindOpaquePixel(image) + Vector2.One * 0.5f - sprite.Texture.GetSize() * 0.5f);
	}
	private void MoveDogTo(Vector2 position)
	{
		var grab = Controls.GetDogMoveRect().GetCenter();
		var start = GetField<Vector2>(_pet!, "_editPosition");
		Mouse(grab, true); Controls.UpdatePointer(grab + position - start); Mouse(grab + position - start, false);
	}
	private void FinishFall()
	{
		for (var frame = 0; frame < 240 && GetField<bool>(_pet!, "_fallingToGround"); frame++) _pet!._Process(1.0 / 60);
		Require(!GetField<bool>(_pet!, "_fallingToGround"), "The dog reaches its bottom walking area in a bounded fall.");
	}
	private async Task SettleFrames()
	{
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
	}

	private async Task VerifyWindowEditor()
	{
		var bridge = GetNode<NativeWindowBridge>("/root/NativeWindowBridge");
		bridge.EmitSignal(NativeWindowBridge.SignalName.StatusRequested);
		var window = GetField<StatusWindow>(_pet!, "_statusWindow");
		var settings = new PetSettings(); SetField(settings, "_hasSeenWelcome", true);
		SetField(_pet!, "_settings", settings); window.Configure(settings);
		CallPrivate(_pet!, "ApplyDogSettings", false, false);
		Require(window.Visible && Session.Active && GetField<bool>(_pet!, "_accessoryEditing"),
			"The tray Status request opens Items and enables editing on the desktop dog.");
		Require(window.GetViewport() != _pet!.GetViewport() && !Descendants(window).OfType<AccessoryPreview>().Any() &&
			!Descendants(GetTree().Root).OfType<AccessoryPreviewWindow>().Any(),
			"The catalog has its own viewport and creates no separate dog preview window.");
		var editor = FindDescendant<AccessoryEditor>(window)!;
		Require(GetField<AccessoryEditingSession>(editor, "_session") == Session, "The inspector and desktop share one editing session.");
		window.EmitSignal(Window.SignalName.CloseRequested);
		Require(!window.Visible && !Session.Active, "Closing Status hides it and exits editing.");
		FinishFall();
		using (var image = Dog.Texture.GetImage())
		{
			var point = Dog.ToGlobal(FindOpaquePixel(image) + Vector2.One * 0.5f - Dog.Texture.GetSize() * 0.5f);
			var backend = GetField<BackendPetClient>(_pet!, "_backend"); var grants = backend.PendingGrantCount;
			_pet!._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = point });
			Require(window.Visible && GetField<StatusWindow>(_pet!, "_statusWindow") == window && Session.Active,
				"Right-clicking the dog reopens the same Status window for direct editing.");
			Require(backend.PendingGrantCount == grants, "Opening Status with a right-click does not grant a pet.");
		}
		window.Hide(); bridge.EmitSignal(NativeWindowBridge.SignalName.StatusRequested);
		Require(window.Visible && Session.Active, "The tray reopens the hidden catalog.");
		await SettleFrames();
		await VerifyItemsEditingLifecycle(window);
		await VerifyGroundedWalkingAfterRealFall(window);
		VerifyDirectDesktopEditing(window);
		await VerifyDesktopLayers(window);
		VerifyColorPickerInteraction(window);
		VerifyUiTransformsAndUndo(window);
		await VerifyTextEditor(window);
		await VerifyCategoryRows(window);
		await VerifyWoodlandPresentation(window);
		await CaptureDesktopWorkspace(window);
		if (OS.GetCmdlineUserArgs().Contains("--hold-preview"))
		{
			GD.Print("ACCESSORY_PREVIEW_READY: close the Status window to finish the smoke test.");
			await ToSignal(window, Window.SignalName.CloseRequested);
		}
		Session.ClearSelection(); window.Hide(); FinishFall();
		SetField(_pet!, "_settings", null); settings.Free();
		_wardrobe!.Clear(); _wardrobe.Equip("SFlower1.png", Vector2.Zero); _wardrobe.Equip("SquareCharm.png", Vector2.One);
	}

	private async Task VerifyLandingFixture()
	{
		_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>(); AddChild(_pet); _pet.SetProcess(false);
		_originalSettings = GetField<PetSettings>(_pet, "_settings"); SetField(_pet, "_settings", null);
		CallPrivate(_pet, "OpenStatusWindow"); var window = GetField<StatusWindow>(_pet, "_statusWindow");
		await SettleFrames(); await VerifyGroundedWalkingAfterRealFall(window);
	}
	private async Task VerifyLayersFixture()
	{
		_pet = ResourceLoader.Load<PackedScene>("res://Main.tscn").Instantiate<DesktopPet>(); AddChild(_pet); _pet.SetProcess(false);
		_originalSettings = GetField<PetSettings>(_pet, "_settings");
		CallPrivate(_pet, "OpenStatusWindow"); await SettleFrames();
		await VerifyDesktopLayers(GetField<StatusWindow>(_pet, "_statusWindow"));
	}

	private async Task VerifyGroundedWalkingAfterRealFall(StatusWindow window)
	{
		if (DisplayServer.GetName() == "headless" || Engine.IsEmbeddedInEditor()) return;
		var pet = _pet!; var foot = GetField<Node2D>(pet, "_footAnchor"); var backend = GetField<BackendPetClient>(pet, "_backend");
		var grants = backend.PendingGrantCount; var originalFps = Engine.MaxFps;
		Engine.MaxFps = 60;
		async Task Frame()
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		}
		try
		{
			foreach (var transition in new[] { "tab", "hide" })
			{
				pet.SetProcess(false); window.ShowStatusWindow(); GetField<Button>(window, "_itemsTabButton").EmitSignal(BaseButton.SignalName.Pressed); Session.ClearSelection();
				var usable = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary);
				MoveDogTo(new Vector2(usable.Size.X * 0.35f, Mathf.Min(320, usable.Size.Y * 0.45f)));
				await Frame(); ReportLandingGeometry($"{transition}-editing");
				var editingFootScreen = foot.GlobalPosition + MainWindowScreenPosition();
				if (transition == "tab") GetField<Button>(window, "_dogsTabButton").EmitSignal(BaseButton.SignalName.Pressed); else window.Hide();
				Require(!Session.Active && GetField<bool>(pet, "_fallingToGround"), "Leaving Items starts a native-frame fall before the landing regression.");
				pet.SetProcess(true);
				for (var frame = 0; frame < 180 && GetField<bool>(pet, "_fallingToGround"); frame++) await Frame();
				Require(!GetField<bool>(pet, "_fallingToGround"), "The real-frame drop reaches its ground in a bounded time.");
				ReportLandingGeometry($"{transition}-landed");
				var landScreen = foot.GlobalPosition + MainWindowScreenPosition(); var walk = GetField<float>(pet, "_walkX");
				for (var frame = 0; frame < 90; frame++)
				{
					await Frame();
					var (_, bottom, _) = ((float, float, float))CallPrivate(pet, "GetAppearanceExtents")!;
					var ground = usable.End.Y - 2 - bottom; var screenFoot = foot.GlobalPosition + MainWindowScreenPosition();
					if (frame is 0 or 30 or 89 || screenFoot.Y < ground - 10.5f || screenFoot.Y > ground + 0.5f) ReportLandingGeometry($"{transition}-walking-{frame:D2}");
					Require(screenFoot.Y >= ground - 10.5f && screenFoot.Y <= ground + 0.5f,
						$"After landing, real walking frame {frame} stays at the usable-screen ground with only its normal 10px bounce (actual Y={screenFoot.Y}, ground={ground}).");
					var nativePosition = MainWindowScreenPosition(); var nativeSize = DisplayServer.WindowGetSize(0);
					Require(Mathf.Abs(nativePosition.Y + nativeSize.Y - usable.End.Y) <= 1,
						"The actual walking window remains aligned to the usable-screen bottom after native resize and position events.");
				}
				Require(landScreen.Y > editingFootScreen.Y + 100, "The dog lands below its former editing height in actual screen coordinates.");
				Require(GetField<float>(pet, "_walkX") != walk && GetField<float>(pet, "_stepPhase") > 0, "Walking and gait continue through real frames after landing.");
			}
			Require(backend.PendingGrantCount == grants, "Real-frame falling and continued ground walking do not add pending pet grants.");
		}
		finally
		{
			pet.SetProcess(false); Engine.MaxFps = originalFps;
			window.ShowStatusWindow(); GetField<Button>(window, "_itemsTabButton").EmitSignal(BaseButton.SignalName.Pressed); await SettleFrames();
		}
	}

	private Vector2 MainWindowScreenPosition()
	{
		if (OS.GetName() == "Windows")
		{
			var hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, 0);
			if (GetWindowRect(hwnd, out var rect)) return new Vector2(rect.Left, rect.Top);
		}
		return DisplayServer.WindowGetPosition(0);
	}
	private void ReportLandingGeometry(string stage)
	{
		var root = GetTree().Root; var foot = GetField<Node2D>(_pet!, "_footAnchor").GlobalPosition;
		var hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, 0);
		var native = OS.GetName() == "Windows" && GetWindowRect(hwnd, out var rect) ? $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})" : "unavailable";
		GD.Print($"LANDING_DIAG {stage}: mode={root.Mode}, nativeMode={DisplayServer.WindowGetMode(0)}, root={root.Position}/{root.Size}, display={DisplayServer.WindowGetPosition(0)}/{DisplayServer.WindowGetSize(0)}, cached={GetField<Vector2I>(_pet!, "_windowSize")}, foot={foot}, rootScreen={foot + (Vector2)root.Position}, nativeScreen={foot + MainWindowScreenPosition()}, nativeRect={native}");
	}

	private async Task VerifyItemsEditingLifecycle(StatusWindow window)
	{
		var pet = _pet!; var foot = GetField<Node2D>(pet, "_footAnchor"); var visual = GetField<Node2D>(pet, "_visualRoot");
		var backend = GetField<BackendPetClient>(pet, "_backend"); var grants = backend.PendingGrantCount;
		MoveDogTo(new Vector2(GetField<Vector2I>(pet, "_windowSize").X * 0.5f, 260));
		var phase = GetField<float>(pet, "_stepPhase"); var walk = GetField<float>(pet, "_walkX");
		var footTransform = foot.Transform; var dogTransform = Dog.GlobalTransform;
		for (var frame = 0; frame < 30; frame++) pet._Process(1.0 / 60);
		CallPrivate(pet, "SyncAccessories");
		Require(GetField<float>(pet, "_stepPhase") == phase && GetField<float>(pet, "_walkX") == walk &&
			foot.Transform == footTransform && Dog.GlobalTransform == dogTransform && visual.Rotation == 0,
			"Items keeps the dog upright and motionless at its editable position, including direct outfit synchronization.");
		var usable = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary);
		var editSurface = GetField<Vector2I>(pet, "_windowSize");
		Require(editSurface.X == usable.Size.X && editSurface.Y >= usable.Size.Y - 1 && editSurface.Y <= usable.Size.Y,
			"Direct editing covers the usable screen with at most a one-pixel native fullscreen-avoidance margin.");
		CallPrivate(pet, "SpawnFloatingHeart");
		var hearts = GetField<System.Collections.IList>(pet, "_floatingHearts");
		var heart = (Sprite2D)hearts[0]!.GetType().GetProperty("Sprite")!.GetValue(hearts[0])!; var heartPosition = heart.Position;
		pet._Process(0.12);
		Require(!GetTree().Paused && !heart.Position.IsEqualApprox(heartPosition) && Dog.GlobalTransform == dogTransform,
			"Items stillness leaves heart feedback and the scene running independently.");
		await VerifyNativeOverlayRegion("desktop-items-heart");
		CallPrivate(pet, "UpdateFloatingHearts", 10f); CallPrivate(pet, "UpdateDogMouseRegion");
		await VerifyNativeOverlayRegion("desktop-items-still");
		foreach (var tab in new[] { "_dogsTabButton", "_shopTabButton", "_settingsTabButton" })
		{
			var oldScreen = foot.GlobalPosition + (Vector2)GetTree().Root.Position;
			GetField<Button>(window, tab).EmitSignal(BaseButton.SignalName.Pressed);
			Require(window.Visible && !Session.Active && !GetField<bool>(pet, "_accessoryEditing") && GetField<bool>(pet, "_fallingToGround"),
				"Leaving Items starts the drop while the other menu page remains open.");
			Require((foot.GlobalPosition + (Vector2)GetTree().Root.Position).IsEqualApprox(oldScreen), "Leaving Items does not teleport the desktop dog.");
			pet._Process(0.1);
			Require((foot.GlobalPosition + (Vector2)GetTree().Root.Position).Y > oldScreen.Y, "The dog falls downward after leaving Items.");
			FinishFall(); var groundedWalk = GetField<float>(pet, "_walkX"); pet._Process(0.15);
			Require(GetField<float>(pet, "_walkX") != groundedWalk && GetField<float>(pet, "_stepPhase") > 0 && visual.Rotation != 0,
				"The dog walks and bounces while Dogs, Shop or Settings stays open.");
			GetField<Button>(window, "_itemsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
			Require(Session.Active && GetField<bool>(pet, "_accessoryEditing") && visual.Rotation == 0, "Returning to Items restores upright desktop editing.");
			MoveDogTo(new Vector2(GetField<Vector2I>(pet, "_windowSize").X * 0.5f, 260));
		}
		foreach (var close in new[] { false, true })
		{
			if (close) window.EmitSignal(Window.SignalName.CloseRequested); else window.Hide();
			Require(!Session.Active && GetField<bool>(pet, "_fallingToGround"), "Hiding or closing Items starts the drop and deactivates editing.");
			FinishFall(); var groundedWalk = GetField<float>(pet, "_walkX"); pet._Process(0.15);
			Require(GetField<float>(pet, "_walkX") != groundedWalk, "Walking resumes after a hidden or closed catalog's drop completes.");
			window.ShowStatusWindow(); MoveDogTo(new Vector2(GetField<Vector2I>(pet, "_windowSize").X * 0.5f, 260));
		}
		if (DisplayServer.GetName() != "headless")
		{
			window.Mode = Window.ModeEnum.Minimized; await SettleFrames(); window._Process(0);
			Require(window.Mode == Window.ModeEnum.Minimized && !Session.Active && GetField<bool>(pet, "_fallingToGround"),
				"Minimizing Items leaves editing and starts the drop even though Status remains visible.");
			FinishFall(); var groundedWalk = GetField<float>(pet, "_walkX"); pet._Process(0.15);
			Require(GetField<float>(pet, "_walkX") != groundedWalk, "A minimized catalog permits normal walking after the drop.");
			GetNode<NativeWindowBridge>("/root/NativeWindowBridge").EmitSignal(NativeWindowBridge.SignalName.StatusRequested);
			await SettleFrames();
			Require(window.Mode == Window.ModeEnum.Windowed && Session.Active, "Tray Status restores the minimized window and direct editing.");
		}
		MoveDogTo(new Vector2(GetField<Vector2I>(pet, "_windowSize").X * 0.5f, 360));
		Require(backend.PendingGrantCount == grants, "Editing lifecycle, falling, normal animation and independent hearts never generate pet grants.");
	}

	private void VerifyDirectDesktopEditing(StatusWindow window)
	{
		var editor = FindDescendant<AccessoryEditor>(window)!; var cards = GetField<Dictionary<string, Button>>(editor, "_cards");
		var wardrobe = _wardrobe!; var backend = GetField<BackendPetClient>(_pet!, "_backend"); var grants = backend.PendingGrantCount;
		wardrobe.Clear(); wardrobe.ClearHistory(); Session.ClearSelection();
		MoveDogTo(new Vector2(GetField<Vector2I>(_pet!, "_windowSize").X * 0.5f, 360));
		var start = GetField<Vector2>(_pet!, "_editPosition"); var grab = Controls.GetDogMoveRect().GetCenter();
		Require(Controls.IsInteractiveAt(grab) && Controls.IsInteractiveAt(Controls.GetDogResizeHandle()) && !Controls.IsInteractiveAt(Vector2.Zero),
			"The dog move label and resize handle catch input while distant transparent desktop space passes through.");
		Mouse(grab, true); Controls.UpdatePointer(grab + new Vector2(95, -70)); Mouse(grab + new Vector2(95, -70), false);
		Require(GetField<Vector2>(_pet!, "_editPosition").IsEqualApprox(start + new Vector2(95, -70)) && !Session.IsDragging && !wardrobe.CanUndo,
			"Dragging the dog changes its desktop position without creating cosmetic outfit history.");
		start = GetField<Vector2>(_pet!, "_editPosition"); grab = Controls.GetDogMoveRect().GetCenter();
		Mouse(grab, true); Controls.UpdatePointer(grab + new Vector2(75, 55)); DesktopKey(Key.Escape);
		Require(GetField<Vector2>(_pet!, "_editPosition").IsEqualApprox(start) && !Session.IsDragging,
			"Escape on the desktop rolls a dog drag back to its original position.");
		var body = At(new Vector2(0.5f, 0.6f));
		Require((bool)CallPrivate(_pet!, "IsVisibleDogPixel", body)!, "The direct dog-body gesture fixture targets opaque artwork.");
		Mouse(body, true); Controls.UpdatePointer(body + new Vector2(30, 25)); Mouse(body + new Vector2(30, 25), false);
		Require(GetField<Vector2>(_pet!, "_editPosition").IsEqualApprox(start + new Vector2(30, 25)), "Opaque dog pixels also allow moving the dog during Items editing.");
		foreach (var edge in new[] { new Vector2(-10000, -10000), new Vector2(10000, 10000), new Vector2(-10000, 10000), new Vector2(10000, -10000) })
		{
			MoveDogTo(edge); var bounds = Controls.GetRenderBounds()!.Value; var surface = new Rect2(Vector2.Zero, GetField<Vector2I>(_pet!, "_windowSize"));
			Require(bounds.Position.X >= 0 && bounds.Position.Y >= 0 && bounds.End.X <= surface.End.X && bounds.End.Y <= surface.End.Y,
				"Dog dragging clamps all artwork and local controls inside every usable-screen edge.");
		}
		MoveDogTo(new Vector2(GetField<Vector2I>(_pet!, "_windowSize").X * 0.5f, 360));
		start = GetField<Vector2>(_pet!, "_editPosition"); grab = Controls.GetDogMoveRect().GetCenter(); Mouse(grab, true);
		Controls.UpdatePointer(new Vector2(float.NaN, 0)); Controls.UpdatePointer(new Vector2(float.PositiveInfinity, 0));
		Require(GetField<Vector2>(_pet!, "_editPosition") == start && !Controls.IsInteractiveAt(new Vector2(float.NaN, 0)),
			"Nonfinite pointer samples cannot poison dog placement or interactive bounds.");
		GetField<Button>(editor, "_cancelButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(!Session.IsDragging && GetField<Vector2>(_pet!, "_editPosition") == start, "The menu Cancel action also rolls back a direct dog gesture.");
		var settings = GetField<PetSettings>(_pet!, "_settings"); var originalScale = settings.DogScale;
		var scaleHandle = Controls.GetDogResizeHandle(); var center = AccessoryGeometry.BoundsFromCorners(Dog, GetField<Rect2>(_pet!, "_visibleDogLocalRect")).GetCenter();
		Mouse(scaleHandle, true); Controls.UpdatePointer(center + (scaleHandle - center) * 1.4f); Mouse(center + (scaleHandle - center) * 1.4f, false);
		Require(Mathf.IsEqualApprox(settings.DogScale, originalScale * 1.4f), "The desktop dog resize handle applies its scale to PetSettings immediately.");
		var config = new ConfigFile(); Require(config.Load("user://pet_settings.cfg") == Error.Ok &&
			Mathf.IsEqualApprox((float)config.GetValue("dog", "dog_scale"), settings.DogScale), "A completed desktop dog resize persists the scale preference.");
		scaleHandle = Controls.GetDogResizeHandle(); center = AccessoryGeometry.BoundsFromCorners(Dog, GetField<Rect2>(_pet!, "_visibleDogLocalRect")).GetCenter();
		var savedScale = settings.DogScale; start = GetField<Vector2>(_pet!, "_editPosition");
		Mouse(scaleHandle, true); Controls.UpdatePointer(center + (scaleHandle - center) * 9); DesktopKey(Key.Escape);
		Require(settings.DogScale == savedScale && GetField<Vector2>(_pet!, "_editPosition") == start, "Escape restores both dog size and position after a resize.");
		foreach (var factor in new[] { 0.001f, 100f })
		{
			scaleHandle = Controls.GetDogResizeHandle(); center = AccessoryGeometry.BoundsFromCorners(Dog, GetField<Rect2>(_pet!, "_visibleDogLocalRect")).GetCenter();
			Mouse(scaleHandle, true); Controls.UpdatePointer(center + (scaleHandle - center) * factor); Mouse(center + (scaleHandle - center) * factor, false);
			Require(settings.DogScale == (factor < 1 ? PetSettings.MinDogScale : PetSettings.MaxDogScale), "Dog handle scaling obeys the saved 50–200% limits.");
		}
		_pet!.ResizeEditingDog(1);
		const string id = "SFlower2.png"; cards[id].EmitSignal(BaseButton.SignalName.Pressed);
		Require(Session.IsPlacing && Session.SelectedId == id, "Choosing an unequipped catalog item arms placement on the desktop dog.");
		Mouse(Vector2.Zero, true);
		Require(wardrobe.GetPlacement(id) == null && Session.IsPlacing, "Clicking outside the dog cannot place a selected image accessory.");
		var anchor = new Vector2(0.55f, 0.32f); Mouse(At(anchor), true); Mouse(At(anchor), false);
		var placement = wardrobe.GetPlacement(id)!;
		Require(placement.Position.IsEqualApprox(anchor) && !Session.IsPlacing && !Session.IsDragging, "A desktop click places exactly one accessory at its normalized dog position.");
		var restored = LoadIsolatedWardrobe(); Require(restored.GetPlacement(id) == placement, "Direct desktop placement saves the outfit."); restored.Free();
		wardrobe.ClearHistory(); grab = AccessoryPixel(id); Mouse(grab, true);
		Require(Session.IsDragging && Session.SelectedId == id, "An equipped accessory starts a direct drag from an opaque artwork pixel.");
		var delta = At(new Vector2(0.1f, 0.12f)) - At(Vector2.Zero); Controls.UpdatePointer(grab + delta); Mouse(grab + delta, false);
		Require(wardrobe.GetPlacement(id)!.Position.IsEqualApprox(anchor + new Vector2(0.1f, 0.12f)), "Direct accessory dragging preserves the original grab offset.");
		DesktopKey(Key.Z, true); Require(wardrobe.GetPlacement(id) == placement && !wardrobe.CanUndo, "Ctrl + Z on the desktop undoes the whole accessory drag in one step.");
		grab = AccessoryPixel(id); Mouse(grab, true); Controls.UpdatePointer(grab + delta); DesktopKey(Key.Escape);
		Require(wardrobe.GetPlacement(id) == placement && !wardrobe.CanUndo && !Session.IsDragging, "Escape cancels direct accessory dragging without adding history.");
		Session.SelectAccessory(id); var resize = Controls.GetAccessoryResizeHandle(); center = AccessoryNode(id).GlobalPosition;
		Require(Controls.IsInteractiveAt(resize) && Controls.IsInteractiveAt(Controls.GetAccessoryRotationHandle()) && Controls.GetSelectionCorners().Length == 4,
			"Selecting a desktop accessory exposes an outline plus interactive size and rotation handles.");
		Mouse(resize, true); Controls.UpdatePointer(center + (resize - center) * 1.5f); Mouse(center + (resize - center) * 1.5f, false);
		Require(Mathf.IsEqualApprox(wardrobe.GetScale(id), 1.5f), "The local accessory resize handle changes only that item's size.");
		DesktopKey(Key.Z, true); Require(wardrobe.GetPlacement(id) == placement && !wardrobe.CanUndo, "One undo reverses the local handle size gesture.");
		foreach (var direction in new[] { 1f, -1f })
		{
			SetField(_pet!, "_direction", direction); CallPrivate(_pet!, "AnimateDog"); Session.SelectAccessory(id);
			var rotate = Controls.GetAccessoryRotationHandle(); center = AccessoryNode(id).GlobalPosition;
			var turn = center + (rotate - center).Rotated(Mathf.DegToRad(55)); Mouse(rotate, true); Controls.UpdatePointer(turn); Mouse(turn, false);
			Require(Mathf.IsEqualApprox(wardrobe.GetRotationDegrees(id), direction * 55), "The local rotation handle follows the pointer correctly in either dog direction.");
			restored = LoadIsolatedWardrobe(); Require(restored.GetPlacement(id) == wardrobe.GetPlacement(id), "Local transform handles persist the edited fit."); restored.Free();
			DesktopKey(Key.Z, true); Require(wardrobe.GetPlacement(id) == placement, "Desktop undo restores the accessory's prior rotation.");
		}
		SetField(_pet!, "_direction", 1f); CallPrivate(_pet!, "AnimateDog");
		Session.SelectAccessory(id); DesktopKey(Key.Delete);
		Require(wardrobe.GetPlacement(id) == null && Session.SelectedId == null, "Desktop Delete removes the selected accessory.");
		restored = LoadIsolatedWardrobe(); Require(restored.GetPlacement(id) == null, "Desktop Delete persists removal."); restored.Free();
		DesktopKey(Key.Z, true); Require(wardrobe.GetPlacement(id) == placement, "Desktop undo restores a removed accessory with its placement.");
		foreach (var transition in new[] { "_dogsTabButton", "hide", "close" })
		{
			Session.SelectAccessory(id); grab = AccessoryPixel(id); Mouse(grab, true); Controls.UpdatePointer(grab + delta);
			if (transition == "hide") window.Hide(); else if (transition == "close") window.EmitSignal(Window.SignalName.CloseRequested);
			else GetField<Button>(window, transition).EmitSignal(BaseButton.SignalName.Pressed);
			Require(!Session.Active && !Session.IsDragging && wardrobe.GetPlacement(id) == placement,
				"Leaving Items or hiding/closing the browser cancels the active desktop accessory gesture.");
			window.ShowStatusWindow(); GetField<Button>(window, "_itemsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
		}
		cards["FairyWing.jpg"].EmitSignal(BaseButton.SignalName.Pressed); DesktopKey(Key.Escape);
		Require(!Session.IsPlacing && Session.SelectedId == null && wardrobe.GetPlacement("FairyWing.jpg") == null, "Escape cancels an unplaced catalog selection.");
		Require(backend.PendingGrantCount == grants, "Every direct editing click, handle, key, cancel and transition leaves the pending pet queue unchanged.");
		wardrobe.Clear(); wardrobe.Equip("SFlower1.png", Vector2.Zero); wardrobe.Equip("SquareCharm.png", Vector2.One); wardrobe.ClearHistory(); Session.ClearSelection();
		MoveDogTo(new Vector2(GetField<Vector2I>(_pet!, "_windowSize").X * 0.5f, 360));
	}

	private void RightClick(Vector2 point) => _pet!._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = point });
	private static float ColorDifference(Color a, Color b) => Mathf.Abs(a.R - b.R) + Mathf.Abs(a.G - b.G) + Mathf.Abs(a.B - b.B);
	private static Color TextureColorAt(Sprite2D sprite, Image image, Vector2 point)
	{
		var local = sprite.ToLocal(point) + sprite.Texture.GetSize() * 0.5f;
		var x = Mathf.FloorToInt(local.X); var y = Mathf.FloorToInt(local.Y);
		return x >= 0 && y >= 0 && x < image.GetWidth() && y < image.GetHeight() ? image.GetPixel(x, y) * sprite.Modulate : Colors.Transparent;
	}
	private Vector2 DistinctOverlap(Sprite2D bottom, Sprite2D top)
	{
		using var below = bottom.Texture.GetImage(); using var above = top.Texture.GetImage();
		var best = Vector2.Zero; var difference = -1f;
		for (var y = 2; y < above.GetHeight() - 2; y += Math.Max(1, above.GetHeight() / 50))
			for (var x = 2; x < above.GetWidth() - 2; x += Math.Max(1, above.GetWidth() / 50))
			{
				var color = above.GetPixel(x, y) * top.Modulate;
				if (color.A < 0.98f) continue;
				var point = top.ToGlobal(new Vector2(x + 0.5f, y + 0.5f) - top.Texture.GetSize() * 0.5f);
				var other = TextureColorAt(bottom, below, point); if (other.A < 0.98f) continue;
				var delta = ColorDifference(color, other); if (delta <= difference) continue;
				difference = delta; best = point;
			}
		Require(difference > 0.2f, "The layer pixel fixture contains distinctly colored opaque artwork at the same desktop point.");
		return best;
	}
	private async Task<Color> RenderedOverlayPixel(Vector2 point)
	{
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		using var image = _pet!.GetViewport().GetTexture().GetImage();
		return image.GetPixel(Mathf.Clamp(Mathf.FloorToInt(point.X), 0, image.GetWidth() - 1), Mathf.Clamp(Mathf.FloorToInt(point.Y), 0, image.GetHeight() - 1));
	}
	private void RestoreLayerOrder(IReadOnlyList<string> order)
	{
		for (var index = 0; index < order.Count; index++)
			while (_wardrobe!.GetLayerIndex(order[index]) > index) _wardrobe.MoveLayer(order[index], -1);
	}

	private async Task VerifyDesktopLayers(StatusWindow window)
	{
		var wardrobe = _wardrobe!; var editor = FindDescendant<AccessoryEditor>(window)!;
		var cards = GetField<Dictionary<string, Button>>(editor, "_cards"); var menu = GetField<AccessoryLayerMenu>(Controls, "_layerMenu");
		var up = GetField<Button>(editor, "_layerUpButton"); var down = GetField<Button>(editor, "_layerDownButton");
		var backend = GetField<BackendPetClient>(_pet!, "_backend"); var grants = backend.PendingGrantCount;
		var originalOutfit = wardrobe.Equipped.ToArray(); var originalLayers = wardrobe.LayerOrder.ToArray();
		var originalPreferences = wardrobe.Catalog.Select(item => new { item.Id, Tint = wardrobe.GetTint(item.Id), Scale = wardrobe.GetScale(item.Id), Rotation = wardrobe.GetRotationDegrees(item.Id) }).ToArray();
		var originalText = wardrobe.GetText(); var originalBackground = wardrobe.GetTextBackgroundVisible();
		var dogPosition = GetField<Vector2>(_pet!, "_editPosition"); var dogScale = GetField<PetSettings>(_pet!, "_settings").DogScale;
		var dog = AccessoryWardrobe.DogLayerId; const string charm = "SquareCharm.png", red = "SFlower1.png", white = "SFlower2.png";
		var text = AccessoryWardrobe.TextAccessoryId;
		try
		{
			Session.ClearSelection(); wardrobe.Clear(); wardrobe.SetTransform(charm, 1, 0); wardrobe.Equip(charm, new Vector2(0.42f, 0.58f));
			MoveDogTo(new Vector2(GetField<Vector2I>(_pet!, "_windowSize").X * 0.4f, 500)); wardrobe.ClearHistory();
			var charmNode = (Sprite2D)AccessoryNode(charm); var overlap = DistinctOverlap(Dog, charmNode);
			Require(charmNode.ZIndex > 0, "An accessory above the dog receives a foreground draw depth.");
			var front = DisplayServer.GetName() == "headless" ? Colors.White : await RenderedOverlayPixel(overlap);
			Mouse(overlap, true); Require(Session.SelectedId == charm && Session.IsDragging, "The visible foreground accessory wins desktop selection over opaque dog pixels."); DesktopKey(Key.Escape); Session.ClearSelection();
			RightClick(overlap);
			Require(menu.Visible && GetField<string>(menu, "_targetId") == charm && Session.SelectedId == charm && menu.GetItemCount() == 3,
				"Right-clicking the foreground artwork opens its layer menu through the actual desktop input route.");
			Require(menu.IsItemDisabled(menu.GetItemIndex(1)) && !menu.IsItemDisabled(menu.GetItemIndex(2)), "The top layer disables Move up and enables Move down.");
			Require(menu.GetViewport() != _pet!.GetViewport() && menu.World2D != _pet.GetViewport().World2D && !menu.TransparentBg,
				"The native layer popup owns its canvas world and opaque background separately from the transparent desktop dog.");
			await VerifyNativeOverlayRegion("layer-menu-open");
			if (DisplayServer.GetName() != "headless")
			{
				using var popupImage = menu.GetTexture().GetImage();
				Require(popupImage.SavePng($"{OutputDirectory}/layer-menu-window.png") == Error.Ok, "The separate native layer-menu viewport saves for visual review.");
			}
			menu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
			Require(!menu.Visible && wardrobe.LayerOrder.SequenceEqual(new[] { charm, dog }) && charmNode.ZIndex < 0,
				"Move down closes the popup and puts the item behind its parent dog.");
			Require(GetField<Label>(editor, "_saveStatusLabel").Text == "Outfit saved", "A successful layer-menu action reports the shared saved-outfit status.");
			Session.ClearSelection();
			var rear = DisplayServer.GetName() == "headless" ? Colors.Black : await RenderedOverlayPixel(overlap);
			Require(ColorDifference(front, rear) > 0.15f, "Reordering behind the dog changes the actual rendered overlapping pixel to the dog artwork.");
			await VerifyNativeOverlayRegion("accessory-behind-dog");
			Mouse(overlap, true); Require(Session.SelectedId == null && Session.IsDragging, "Opaque dog pixels occlude a lower accessory during selection and start a dog gesture."); DesktopKey(Key.Escape);
			RightClick(overlap); Require(GetField<string>(menu, "_targetId") == dog && Session.SelectedId == null, "Right-clicking a dog-occluded item opens the visible dog's menu.");
			menu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
			Require(wardrobe.LayerOrder.SequenceEqual(new[] { dog, charm }) && charmNode.ZIndex > 0, "Moving the dog below an item reveals that item immediately.");
			DesktopKey(Key.Z, true); Require(wardrobe.LayerOrder.SequenceEqual(new[] { charm, dog }), "Desktop undo restores the dog's previous layer.");
			DesktopKey(Key.Z, true); Require(wardrobe.LayerOrder.SequenceEqual(new[] { dog, charm }) && !wardrobe.CanUndo, "Another undo restores the accessory's previous layer without retaining synthetic drag history.");
			wardrobe.MoveLayer(charm, -1); cards[charm].EmitSignal(BaseButton.SignalName.Pressed);
			Require(GetField<Label>(editor, "_layerLabel").Text.Contains("Behind dog") && !up.Disabled && down.Disabled,
				"The inspector selects a dog-occluded item and shows its available layer action.");
			up.EmitSignal(BaseButton.SignalName.Pressed);
			Require(wardrobe.LayerOrder.SequenceEqual(new[] { dog, charm }) && up.Disabled && !down.Disabled, "The inspector can bring an obscured item back in front of the dog.");
			var restored = LoadIsolatedWardrobe(); Require(restored.LayerOrder.SequenceEqual(wardrobe.LayerOrder), "Inspector layer actions persist the recovered layer order."); restored.Free();

			Session.ClearSelection(); wardrobe.Clear(); wardrobe.Equip(red, new Vector2(0.52f, 0.58f)); wardrobe.Equip(white, new Vector2(0.52f, 0.58f));
			var shared = DistinctOverlap((Sprite2D)AccessoryNode(red), (Sprite2D)AccessoryNode(white));
			var whiteTop = DisplayServer.GetName() == "headless" ? Colors.White : await RenderedOverlayPixel(shared);
			Mouse(shared, true); Require(Session.SelectedId == white, "The topmost of two overlapping opaque accessories wins selection."); DesktopKey(Key.Escape); Session.ClearSelection();
			wardrobe.MoveLayer(white, -1);
			var redTop = DisplayServer.GetName() == "headless" ? Colors.Black : await RenderedOverlayPixel(shared);
			Require(ColorDifference(whiteTop, redTop) > 0.15f, "Accessory-to-accessory reordering changes the real rendered overlapping color.");
			Mouse(shared, true); Require(Session.SelectedId == red, "Reordering accessories also changes which artwork receives the drag."); DesktopKey(Key.Escape); Session.ClearSelection();
			wardrobe.ClearHistory(); var beforeDrag = wardrobe.GetPlacement(red)!;
			var delta = At(new Vector2(0.05f, 0.07f)) - At(Vector2.Zero); Mouse(shared, true); Controls.UpdatePointer(shared + delta); RightClick(shared + delta);
			Require(!Session.IsDragging && menu.Visible && GetField<string>(menu, "_targetId") == red && wardrobe.GetPlacement(red)!.Position.IsEqualApprox(beforeDrag.Position + new Vector2(0.05f, 0.07f)),
				"Right-click during an accessory drag commits its current position and opens that moved item's layer menu without rollback.");
			menu.HideMenu(); DesktopKey(Key.Z, true); Require(wardrobe.GetPlacement(red) == beforeDrag && !wardrobe.CanUndo, "The committed drag still undoes once after its context menu closes.");

			wardrobe.SetText("Layer test"); wardrobe.SetTextBackgroundVisible(true); wardrobe.Equip(text, new Vector2(0.78f, 0.55f)); Session.ClearSelection();
			var textPoint = AccessoryNode(text).GlobalPosition;
			Require((bool)CallPrivate(_pet!, "IsVisibleDogPixel", textPoint)!, "The text layer fixture overlaps opaque dog artwork.");
			Mouse(textPoint, true); Require(Session.SelectedId == text, "Text Box participates as a selectable foreground layer."); DesktopKey(Key.Escape); Session.ClearSelection();
			while (wardrobe.CanMoveLayer(text, -1)) wardrobe.MoveLayer(text, -1);
			Require(AccessoryNode(text).ZIndex < 0, "Text can move behind the dog with the same layer depth rules as images.");
			Mouse(textPoint, true); Require(Session.SelectedId == null, "A Text Box behind opaque dog pixels cannot intercept the dog's drag."); DesktopKey(Key.Escape);
			cards[text].EmitSignal(BaseButton.SignalName.Pressed); up.EmitSignal(BaseButton.SignalName.Pressed);
			Require(wardrobe.GetLayerIndex(text) > wardrobe.GetLayerIndex(dog), "The inspector can recover behind-dog text without needing a visible text pixel.");
			Session.ClearSelection(); wardrobe.Equip("SafetyGlasses.jpg", new Vector2(0.75f, 0.26f)); var stableLayers = wardrobe.LayerOrder.ToArray();
			wardrobe.Move("SafetyGlasses.jpg", new Vector2(0.72f, 0.3f)); wardrobe.SetTransform("SafetyGlasses.jpg", 1.5f, -20); wardrobe.SetTint("SafetyGlasses.jpg", Colors.CornflowerBlue);
			_pet!.ResizeEditingDog(1.25f); MoveDogTo(GetField<Vector2>(_pet!, "_editPosition") + new Vector2(20, -20));
			Require(wardrobe.LayerOrder.SequenceEqual(stableLayers), "Accessory movement, fit/color edits and direct dog movement/resizing preserve every layer.");
			await VerifyNativeOverlayRegion("layers-text-and-images");

			var menuPoint = AccessoryPixel("SafetyGlasses.jpg"); RightClick(menuPoint);
			Require(menu.Visible && GetField<string>(menu, "_targetId") == "SafetyGlasses.jpg", "Right-click uses the actual tinted, resized and rotated artwork's hit shape.");
			var unchanged = wardrobe.LayerOrder.ToArray(); menu.EmitSignal(PopupMenu.SignalName.IdPressed, 99L);
			Require(!menu.Visible && wardrobe.LayerOrder.SequenceEqual(unchanged), "An invalid context action cannot change the layer order.");
			RightClick(menuPoint); menu._UnhandledKeyInput(new InputEventKey { Pressed = true, Keycode = Key.Escape });
			Require(!menu.Visible && wardrobe.LayerOrder.SequenceEqual(unchanged), "Escape closes the context menu without changing cosmetics.");
			RightClick(menuPoint); RightClick(Vector2.Zero); Require(!menu.Visible, "Right-clicking distant transparent desktop space closes an open layer menu.");
			RightClick(menuPoint); wardrobe.Remove("SafetyGlasses.jpg"); Require(!menu.Visible, "Removing an open menu's target closes the stale context menu.");
			foreach (var transition in new[] { "tab", "hide", "minimize" })
			{
				if (transition == "minimize" && DisplayServer.GetName() == "headless") continue;
				Session.SelectAccessory(red); menu.ShowFor(red, new Vector2I((int)At(Vector2.One).X, (int)At(Vector2.One).Y)); Require(menu.Visible, "The layer popup opens for the lifecycle fixture.");
				if (transition == "tab") GetField<Button>(window, "_dogsTabButton").EmitSignal(BaseButton.SignalName.Pressed);
				else if (transition == "hide") window.Hide(); else { window.Mode = Window.ModeEnum.Minimized; await SettleFrames(); window._Process(0); }
				Require(!Session.Active && !menu.Visible, "Switching tabs, hiding or minimizing Items closes the native layer menu.");
				window.ShowStatusWindow(); GetField<Button>(window, "_itemsTabButton").EmitSignal(BaseButton.SignalName.Pressed); await SettleFrames();
			}
			Require(backend.PendingGrantCount == grants, "Layer menus, order changes, hidden-item recovery, drags and popup lifecycle never enqueue pet grants.");
		}
		finally
		{
			menu.HideMenu(); window.ShowStatusWindow(); GetField<Button>(window, "_itemsTabButton").EmitSignal(BaseButton.SignalName.Pressed); Session.ClearSelection(); wardrobe.Clear();
			foreach (var item in originalPreferences) { wardrobe.SetTint(item.Id, item.Tint); wardrobe.SetTransform(item.Id, item.Scale, item.Rotation); }
			wardrobe.SetText(originalText); wardrobe.SetTextBackgroundVisible(originalBackground);
			foreach (var item in originalOutfit) wardrobe.Equip(item.Id, item.Position);
			RestoreLayerOrder(originalLayers); _pet!.ResizeEditingDog(dogScale); _pet.MoveEditingDog(dogPosition); wardrobe.ClearHistory(); wardrobe.Save();
		}
		Require(wardrobe.Equipped.SequenceEqual(originalOutfit) && wardrobe.LayerOrder.SequenceEqual(originalLayers), "Layer integration checks restore the original cosmetic outfit and its order.");
	}

	private async Task VerifyNativeOverlayRegion(string name)
	{
		if (DisplayServer.GetName() == "headless" || Engine.IsEmbeddedInEditor()) return;
		var pet = _pet!;
		// Most fixtures disable automatic processing; refresh selection/handle changes
		// at the point where the real desktop's next process frame would do so.
		CallPrivate(pet, "UpdateDogMouseRegion");
		var dog = pet.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		var appearance = AccessoryGeometry.BoundsFromCorners(dog, GetField<Rect2>(pet, "_appearanceLocalRect"));
		if (Controls.GetRenderBounds() is Rect2 controls) appearance = appearance.Merge(controls);
		foreach (var entry in GetField<System.Collections.IList>(pet, "_floatingHearts"))
		{
			var heart = (Sprite2D)entry!.GetType().GetProperty("Sprite")!.GetValue(entry)!;
			appearance = appearance.Merge(AccessoryGeometry.BoundsFromCorners(heart, heart.GetRect()));
		}
		var visible = appearance.Grow(2).Intersection(new Rect2(Vector2.Zero, GetField<Vector2I>(pet, "_windowSize")));
		var region = GetField<Rect2I?>(pet, "_lastMouseRegion")!.Value;
		Require(region.Position.X <= visible.Position.X && region.Position.Y <= visible.Position.Y && region.End.X >= visible.End.X && region.End.Y >= visible.End.Y,
			"The integer render region rounds outward and includes filtered dog, accessory, direct editing controls and heart edges.");
		CallPrivate(pet, "UpdateDogMouseRegion");
		Require(GetField<Rect2I?>(pet, "_lastMouseRegion") == region, "An unchanged appearance keeps the same cached native render region.");
		if (OS.GetName() == "Windows")
		{
			var hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, 0);
			var handle = CreateRectRgn(0, 0, 0, 0);
			try
			{
				Require(GetWindowRgn(hwnd, handle) != 0 && GetRgnBox(handle, out var native) != 0 &&
					new Rect2I(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top) == region,
					"The actual Windows render clip matches the stable padded Godot region.");
			}
			finally { DeleteObject(handle); }
			var bridge = GetNode<NativeWindowBridge>("/root/NativeWindowBridge");
			var styles = GetWindowLongPtr(hwnd, -20).ToInt64();
			for (var refresh = 0; refresh < 3; refresh++) bridge.ApplyDesktopPetWindowStyles();
			Require(GetWindowLongPtr(hwnd, -20).ToInt64() == styles && (styles & (0x80L | 0x80000L | 0x08000000L)) == (0x80L | 0x80000L | 0x08000000L),
				"Repeated native style refresh retains the overlay's tool-window, layered and no-activation flags.");
		}
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		using var image = pet.GetViewport().GetTexture().GetImage();
		var outsideAlpha = 0;
		for (var y = 0; y < image.GetHeight(); y++) for (var x = 0; x < image.GetWidth(); x++)
			if (!region.HasPoint(new Vector2I(x, y)) && image.GetPixel(x, y).A > 1f / 255) outsideAlpha++;
		Require(outsideAlpha == 0, "Transparent GPU margins contain no menu-colored pixels outside the dog, controls and heart render region.");
		var crop = region.Intersection(new Rect2I(Vector2I.Zero, new Vector2I(image.GetWidth(), image.GetHeight())));
		using var cropped = image.GetRegion(crop);
		Require(cropped.SavePng($"{OutputDirectory}/{name}-overlay.png") == Error.Ok, "Cropped native transparent-overlay regression capture saves.");
	}

	private async Task VerifyCategoryRows(StatusWindow window)
	{
		var editor = FindDescendant<AccessoryEditor>(window)!;
		var rows = GetField<Dictionary<string, AccessoryCategoryRow>>(editor, "_categoryRows");
		var cards = GetField<Dictionary<string, Button>>(editor, "_cards");
		var outfit = _wardrobe!.Equipped.ToArray();
		var canUndo = _wardrobe.CanUndo;
		var backend = GetField<BackendPetClient>(_pet!, "_backend");
		var grants = backend.PendingGrantCount;
		var expectedCounts = new Dictionary<string, int>
		{
			["Wings"] = 2, ["Collars"] = 0, ["Glasses"] = 1, ["Decorations"] = 3, ["Text"] = 1
		};
		Require(rows.Keys.SequenceEqual(expectedCounts.Keys), "The browser stacks Wings, Collars, Glasses, Decorations and Text in order.");
		Require(!rows["Collars"].Expanded && !rows["Collars"].Scroll.Visible, "The empty Collars shelf starts collapsed.");
		var displayedCards = new HashSet<Button>();
		foreach (var (category, row) in rows)
		{
			var rowCards = row.Cards.GetChildren().OfType<Button>().ToArray();
			Require(rowCards.Length == expectedCounts[category] && rowCards.All(displayedCards.Add),
				$"{category} has the correct accessory count without duplicated cards.");
			Require(row.Scroll.HorizontalScrollMode == ScrollContainer.ScrollMode.Auto && row.Scroll.VerticalScrollMode == ScrollContainer.ScrollMode.Disabled,
				$"{category} uses a single horizontal shelf instead of a vertical grid.");
			var expanded = row.Expanded;
			row.HeaderButton.ButtonPressed = !expanded;
			await SettleFrames();
			Require(row.Expanded == !expanded && row.Scroll.Visible == !expanded,
				$"The {category} header toggles its own shelf.");
			row.HeaderButton.ButtonPressed = expanded;
			await SettleFrames();
		}
		Require(displayedCards.SetEquals(cards.Values), "Every catalog card appears in exactly one category shelf.");
		rows["Collars"].HeaderButton.ButtonPressed = true;
		await SettleFrames();
		Require(rows["Collars"].Cards.GetChildren().OfType<Label>().Any(label => label.Text.Contains("No accessories", StringComparison.OrdinalIgnoreCase)),
			"Opening the empty category shows an honest empty message.");
		rows["Collars"].HeaderButton.ButtonPressed = false;

		var originalSize = window.Size;
		window.Size = window.MinSize;
		await SettleFrames();
		var decorations = rows["Decorations"];
		Require(decorations.Scroll.GetHScrollBar().MaxValue > decorations.Scroll.GetHScrollBar().Page,
			"The narrow browser exposes horizontal scrolling for overflowing accessory cards.");
		decorations.Scroll.ScrollHorizontal = 80;
		await SettleFrames();
		Require(decorations.Scroll.ScrollHorizontal > 0 && decorations.Cards.GetGlobalRect().Position.X < decorations.Scroll.GetGlobalRect().Position.X,
			"Scrolling the shelf reveals later cards without wrapping them into another row.");
		decorations.Scroll.ScrollHorizontal = 0;
		window.Size = originalSize;
		await SettleFrames();
		Require(Session.Active && _wardrobe.Equipped.SequenceEqual(outfit) && _wardrobe.CanUndo == canUndo && backend.PendingGrantCount == grants,
			"Category toggles, browser resizing and shelf scrolling keep direct editing, cosmetics, undo history and pet grants unchanged.");
	}

	private async Task VerifyWoodlandPresentation(StatusWindow window)
	{
		var editor = FindDescendant<AccessoryEditor>(window)!;
		var picker = GetField<ColorPickerButton>(editor, "_colorPicker");
		var tabs = new[] { "_dogsTabButton", "_itemsTabButton", "_shopTabButton", "_settingsTabButton" }
			.Select(field => GetField<Button>(window, field)).ToArray();
		var originalSettings = GetField<PetSettings?>(window, "_settings");
		var testSettings = new PetSettings();
		SetField(testSettings, "_hasSeenWelcome", true);
		var backend = GetField<BackendPetClient>(_pet!, "_backend");
		var grantsBefore = backend.PendingGrantCount;
		var outfitBefore = _wardrobe!.Equipped.ToArray();
		async Task Settle()
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		async Task Capture(Window target, string name)
		{
			if (DisplayServer.GetName() == "headless") return;
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			using var image = target.GetTexture().GetImage();
			Require(image.SavePng($"{OutputDirectory}/{name}.png") == Error.Ok, $"Native {name} screenshot saves.");
		}
		void HeaderFits()
		{
			var area = new Rect2(Vector2.Zero, window.GetVisibleRect().Size);
			foreach (var control in tabs.Cast<Control>().Append(GetField<Label>(window, "_petsValueLabel")))
			{
				var rect = control.GetGlobalRect();
				Require(rect.Position.X >= 0 && rect.Position.Y >= 0 && rect.End.X <= area.End.X + 1 && rect.End.Y <= area.End.Y + 1,
					"Navigation and confirmed pets remain visible at the tested window size.");
			}
		}
		try
		{
			foreach (var scale in new[] { 1f, 0.75f, 1.35f })
			{
				SetField(testSettings, "_uiScale", scale);
				window.Configure(testSettings);
				await Settle();
				Require(window.Theme.GetStylebox("pressed", "TabButton") is StyleBoxTexture { Texture: not null },
					"Refreshing UI scale keeps the themed tab artwork.");
				HeaderFits();
				window.Size = window.MinSize;
				await Settle();
				HeaderFits();
				Require(GetField<Control>(editor, "_inspector").Size.X > 100 && GetField<ScrollContainer>(editor, "_catalogScroll").Size.X > 120,
					"The portrait browser keeps both accessory inspector and category shelves usable at each UI scale.");
			}
			SetField(testSettings, "_uiScale", 1f);
			window.Configure(testSettings);
			await Settle();
			Require(window.Size == new Vector2I(640, 780), "Reducing UI scale restores the portrait browser size instead of retaining its old larger minimum.");
			tabs[0].EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			var dogsPage = GetField<Control>(window, "_dogsPage");
			Require(dogsPage.IsVisibleInTree(), "The Dogs tab shows the current dog.");
			Require(Descendants(dogsPage).OfType<TextureRect>().Count(item => item.Texture != null) == 1,
				"Dogs displays the single original dog without extra breed choices.");
			await Capture(window, "woodland-dogs");
			tabs[2].EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			var shopPage = GetField<Control>(window, "_shopPage");
			Require(shopPage.IsVisibleInTree() && Descendants(shopPage).OfType<Label>().Any(label => label.Text.Contains("coming soon", StringComparison.OrdinalIgnoreCase)),
				"Shop honestly explains that it is coming soon.");
			Require(!Descendants(shopPage).OfType<Button>().Any(), "Shop has no placeholder purchasing controls.");
			await Capture(window, "woodland-shop");
			tabs[3].EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			Require(GetField<Control>(window, "_settingsPage").IsVisibleInTree(), "The themed Settings tab opens.");
			var slider = GetField<HSlider>(window, "_dogScaleSlider");
			window.Size = new Vector2I(650, 700);
			var sizeBeforePreference = window.Size;
			var positionBeforePreference = window.Position;
			slider.Value = 1.2;
			Require(Mathf.IsEqualApprox(testSettings.DogScale, 1.2f), "The themed settings slider still updates its preference.");
			Require(window.Size == sizeBeforePreference && window.Position == positionBeforePreference, "Changing dog size preserves the menu's dimensions and position.");
			window.Hide(); window.ShowStatusWindow();
			Require(window.Size == sizeBeforePreference && window.Position == positionBeforePreference, "Reopening preserves the user's menu dimensions and position.");
			window.EmitSignal(Window.SignalName.CloseRequested);
			var restoredWindow = ResourceLoader.Load<PackedScene>("res://StatusWindow.tscn").Instantiate<StatusWindow>();
			restoredWindow.Visible = false; AddChild(restoredWindow); restoredWindow.Configure(testSettings); restoredWindow.ShowStatusWindow();
			Require(restoredWindow.Size == sizeBeforePreference && restoredWindow.Position == positionBeforePreference,
				"A new menu window restores saved dimensions and position from local preferences.");
			restoredWindow.Free(); window.ShowStatusWindow();
			Require(GetField<Label>(window, "_dogScaleValueLabel").Text == "120%", "Dog size uses one readable percentage.");
			Require(FindDescendant<SpinBox>(GetField<Control>(window, "_settingsPage")) == null, "Basic settings have no duplicate numeric fields.");
			var details = Descendants(GetField<Control>(window, "_settingsPage")).OfType<Control>().Single(node => node.Name == "ConnectionDetails");
			var detailsButton = Descendants(GetField<Control>(window, "_settingsPage")).OfType<Button>().Single(button => button.Text == "Show technical details");
			Require(!details.Visible, "Technical connection details begin collapsed.");
			detailsButton.ButtonPressed = true; Require(details.Visible, "Connection details can be expanded when needed.");
			detailsButton.ButtonPressed = false;
			window.UpdateStatus(1234, 2, "Pet grant pending retry", "Steam initialized", true, true);
			Require(GetField<Label>(window, "_petsValueLabel").Text == "Pets: 1,234" && GetField<Label>(window, "_pendingLabel").Text == "2 clicks waiting to sync",
				"Confirmed pets and waiting clicks remain separate and use clear labels.");
			Require(GetField<Label>(window, "_connectionSummary").Text.Contains("interrupted"), "Interrupted syncing is distinguished from an active connection.");
			window.UpdateStatus(null, 0, "not authenticated", "Steam not initialized", false, false);
			Require(GetField<Label>(window, "_petsValueLabel").Text == "Pets: Steam unavailable" && !GetField<Label>(window, "_pendingLabel").Visible,
				"An unknown balance is never displayed as zero and empty waiting status is hidden.");
			CallPrivate(_pet!, "UpdateStatusWindow");
			window.Size = new Vector2I(640, 780);
			await Settle();
			await Capture(window, "woodland-settings");
			tabs[1].EmitSignal(BaseButton.SignalName.Pressed);
			await Settle();
			Session.ChooseAccessory("DragonWing.jpg");
			var popup = picker.GetPopup();
			popup.PopupCentered();
			await Settle();
			Require(popup.Visible && picker.GetPicker().IsVisibleInTree(), "The native color wheel opens from the themed control.");
			Require(!picker.GetPicker().SlidersVisible && !picker.GetPicker().ColorModesVisible && !picker.GetPicker().HexVisible,
				"The default color wheel hides numeric and color-mode controls.");
			Require(popup.Theme.GetStylebox("panel", "PopupPanel") is StyleBoxTexture { Texture: not null },
				"The native color wheel keeps the parchment artwork.");
			await Capture(popup, "woodland-color-picker");
			var advanced = GetField<Button>(editor, "_advancedColorButton");
			advanced.ButtonPressed = true;
			Require(picker.GetPicker().SlidersVisible && picker.GetPicker().ColorModesVisible && picker.GetPicker().HexVisible,
				"Advanced restores numeric color controls without changing the selected color.");
			var pickerNodes = new Queue<Node>();
			pickerNodes.Enqueue(picker.GetPicker());
			var themedModeButtons = 0;
			while (pickerNodes.Count > 0)
			{
				var node = pickerNodes.Dequeue();
				if (node is Button modeButton && modeButton.Text is "RGB" or "HSV" or "Linear")
				{
					Require(modeButton.GetThemeStylebox("normal") is StyleBoxTexture { Texture: not null },
						"The built-in color mode control uses the stitched UI surface.");
					themedModeButtons++;
				}
				foreach (var child in node.GetChildren(true)) pickerNodes.Enqueue(child);
			}
			Require(themedModeButtons == 3, "All three native color modes keep their labels and receive the UI theme.");
			popup.Hide();
			Require(_wardrobe.Equipped.SequenceEqual(outfitBefore) && backend.PendingGrantCount == grantsBefore,
				"Theming, tab switches, scale changes and picker opening leave cosmetics and pet grants unchanged.");
		}
		finally
		{
			Session.ClearSelection();
			window.Configure(originalSettings);
			tabs[1].EmitSignal(BaseButton.SignalName.Pressed);
			testSettings.Free();
			await Settle();
		}
	}

	private void VerifyColorPickerInteraction(StatusWindow window)
	{
		var editor = FindDescendant<AccessoryEditor>(window)!;
		var session = Session;
		var cards = GetField<Dictionary<string, Button>>(editor, "_cards");
		var thumbnails = GetField<Dictionary<string, TextureRect>>(editor, "_cardTextures");
		var controls = GetField<HBoxContainer>(editor, "_colorControls");
		var pickerButton = GetField<ColorPickerButton>(editor, "_colorPicker");
		var resetButton = GetField<Button>(editor, "_resetColorButton");
		var wardrobe = _wardrobe!;
		var backend = GetField<BackendPetClient>(_pet!, "_backend");
		var grantsBefore = backend.PendingGrantCount;
		Require(!controls.Visible, "Color controls start hidden without an accessory selection.");
		cards["SFlower1.png"].EmitSignal(BaseButton.SignalName.Pressed);
		Require(!controls.Visible, "Selecting fixed-color artwork keeps color controls hidden.");
		cards["DragonWing.jpg"].EmitSignal(BaseButton.SignalName.Pressed);
		Require(controls.Visible && pickerButton.Color == Colors.White && !pickerButton.GetPicker().EditAlpha,
			"Selecting white artwork shows a color picker with opacity editing disabled.");
		Require(pickerButton.GetPicker().PickerShape == ColorPicker.PickerShapeType.HsvWheel, "The native picker exposes an HSV color wheel.");
		var tint = new Color(0.2f, 0.75f, 0.9f, 1);
		pickerButton.Color = tint;
		pickerButton.EmitSignal(ColorPickerButton.SignalName.ColorChanged, tint);
		Require(wardrobe.GetTint("DragonWing.jpg").IsEqualApprox(tint) && wardrobe.Equipped.Count == 2 && thumbnails["DragonWing.jpg"].Modulate.IsEqualApprox(tint),
			"Changing the picker updates the placement preference and catalog thumbnail before equipping.");
		pickerButton.EmitSignal(ColorPickerButton.SignalName.PopupClosed);
		var restored = LoadIsolatedWardrobe();
		Require(restored.GetTint("DragonWing.jpg").IsEqualApprox(tint) && restored.Equipped.Count == 2,
			"Closing the color wheel saves color chosen before placement.");
		restored.Free();

		var target = At(new Vector2(0.65f, 0.3f));
		Mouse(target, true); Mouse(target, false);
		Require(wardrobe.Equipped.Single(item => item.Id == "DragonWing.jpg").Tint.IsEqualApprox(tint), "Direct desktop placement uses the color selected in the wheel.");
		var dog = _pet!.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		var sprite = dog.GetChildren().OfType<Sprite2D>().Single(item => item.Texture == wardrobe.Find("DragonWing.jpg")!.Texture);
		Require(sprite.Modulate.IsEqualApprox(tint), "Direct desktop placement applies its chosen color to the desktop dog.");
		var nextTint = new Color(0.85f, 0.25f, 0.6f, 1);
		pickerButton.Color = nextTint;
		pickerButton.EmitSignal(ColorPickerButton.SignalName.ColorChanged, nextTint);
		Require(sprite.Modulate.IsEqualApprox(nextTint) && wardrobe.Equipped.Single(item => item.Id == "DragonWing.jpg").Tint.IsEqualApprox(nextTint),
			"Changing color after placement updates the live desktop outfit immediately.");
		pickerButton.EmitSignal(ColorPickerButton.SignalName.PopupClosed);
		restored = LoadIsolatedWardrobe();
		Require(restored.Equipped.Single(item => item.Id == "DragonWing.jpg").Tint.IsEqualApprox(nextTint), "Closing the picker persists the equipped accessory color.");
		restored.Free();
		resetButton.EmitSignal(BaseButton.SignalName.Pressed);
		Require(wardrobe.GetTint("DragonWing.jpg") == Colors.White && pickerButton.Color == Colors.White && sprite.Modulate == Colors.White,
			"Reset white updates the picker, equipped accessory and desktop sprite together.");
		restored = LoadIsolatedWardrobe();
		Require(restored.GetTint("DragonWing.jpg") == Colors.White, "Reset white persists its default color.");
		restored.Free();
		wardrobe.Remove("DragonWing.jpg");
		session.ClearSelection();
		Require(!controls.Visible && wardrobe.Equipped.Count == 2 && backend.PendingGrantCount == grantsBefore,
			"Picker interactions leave pet grants unchanged and restore the original outfit.");
	}

	private void VerifyUiTransformsAndUndo(StatusWindow window)
	{
		var editor = FindDescendant<AccessoryEditor>(window)!;
		var session = Session;
		var wardrobe = _wardrobe!;
		const string id = "SFlower1.png";
		var original = wardrobe.GetPlacement(id)!;
		GetField<Dictionary<string, Button>>(editor, "_cards")[id].EmitSignal(BaseButton.SignalName.Pressed);
		Require(!session.IsPlacing && GetField<VBoxContainer>(editor, "_transformControls").Visible,
			"Selecting an equipped card exposes size and rotation without placing again.");
		wardrobe.ClearHistory();
		var size = GetField<Slider>(editor, "_sizeSlider");
		var rotation = GetField<Slider>(editor, "_rotationSlider");
		Require(size is VSlider && rotation is VSlider, "The accessory sidebar uses vertical size and rotation sliders.");
		size.EmitSignal(Slider.SignalName.DragStarted);
		size.Value = 160; size.Value = 185;
		size.EmitSignal(Slider.SignalName.DragEnded, true);
		Require(wardrobe.GetPlacement(id)!.Scale == 1.85f && GetField<Label>(editor, "_sizeValueLabel").Text == "185%",
			"Size slider applies its bounded percentage to the selected accessory.");
		rotation.Value = 37;
		var dog = _pet!.GetNode<Sprite2D>("FootAnchor/VisualRoot/PetSprite");
		var definition = wardrobe.Find(id)!;
		var sprite = dog.GetChildren().OfType<Sprite2D>().Single(item => item.Texture == definition.Texture);
		var dogBounds = GetField<Rect2>(_pet, "_visibleDogLocalRect");
		Require(Mathf.IsEqualApprox(sprite.RotationDegrees, 37) && (sprite.GetRect().Size * sprite.Scale).IsEqualApprox(definition.Size * dogBounds.Size.Y * 1.85f),
			"The desktop sprite matches the editor's size and rotation immediately.");
		var restored = LoadIsolatedWardrobe();
		Require(restored.GetPlacement(id) == wardrobe.GetPlacement(id), "Slider adjustments persist without an extra save action."); restored.Free();
		Require(Controls.IsInteractiveAt(AccessoryPixel(id)), "Rotated and scaled desktop artwork remains selectable by opaque pixels.");
		GetField<Button>(editor, "_undoButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(wardrobe.GetRotationDegrees(id) == 0 && wardrobe.GetScale(id) == 1.85f, "Undo button restores the previous rotation.");
		CallPrivate(editor, "HandleShortcut", new InputEventKey { Keycode = Key.Z, CtrlPressed = true, Pressed = true });
		Require(wardrobe.GetPlacement(id) == original && !wardrobe.CanUndo, "Ctrl + Z reverses the entire size-slider drag in one step.");
		session.ChooseAccessory(id);
		GetField<Button>(editor, "_removeButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(wardrobe.GetPlacement(id) == null, "Remove takes off the selected accessory.");
		GetField<Button>(editor, "_undoButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(wardrobe.GetPlacement(id) == original, "Undo button restores removal.");
		var outfit = wardrobe.Equipped.ToArray();
		GetField<Button>(editor, "_clearButton").EmitSignal(BaseButton.SignalName.Pressed);
		GetField<Button>(editor, "_undoButton").EmitSignal(BaseButton.SignalName.Pressed);
		Require(wardrobe.Equipped.SequenceEqual(outfit), "Undo button restores Clear all with the original layer order.");
		session.ClearSelection(); wardrobe.ClearHistory();
	}

	private async Task VerifyTextEditor(StatusWindow window)
	{
		var editor = FindDescendant<AccessoryEditor>(window)!; var wardrobe = _wardrobe!; var id = AccessoryWardrobe.TextAccessoryId;
		var backend = GetField<BackendPetClient>(_pet!, "_backend"); var grants = backend.PendingGrantCount;
		GetField<Dictionary<string, Button>>(editor, "_cards")[id].EmitSignal(BaseButton.SignalName.Pressed);
		await SettleFrames();
		Require(GetField<HBoxContainer>(editor, "_textControls").Visible && Session.IsPlacing, "Selecting Text Box shows its message editor and arms direct near-dog placement.");
		var input = GetField<LineEdit>(editor, "_textInput");
		var emojiText = string.Concat(Enumerable.Repeat("\U0001F436", 65));
		input.Text = emojiText; input.EmitSignal(LineEdit.SignalName.TextChanged, input.Text);
		Require(input.Text == wardrobe.GetText() && System.Globalization.StringInfo.ParseCombiningCharacters(input.Text).Length == 64,
			"The text field and saved message share a Unicode-safe 64-character limit.");
		input.Text = emojiText; input.EmitSignal(LineEdit.SignalName.TextChanged, input.Text);
		Require(input.Text == wardrobe.GetText(), "Pasting an overlong message resynchronizes the text field when the saved message is unchanged.");
		input.Text = "Good dog!"; input.EmitSignal(LineEdit.SignalName.TextChanged, input.Text); input.EmitSignal(LineEdit.SignalName.TextSubmitted, input.Text);
		var anchor = new Vector2(0.5f, -0.25f); Mouse(At(anchor), true); Mouse(At(anchor), false);
		Require(wardrobe.GetPlacement(id)?.Position.IsEqualApprox(anchor) == true, "A Text Box can be placed above the desktop dog without requiring an opaque dog pixel or being intercepted by its move label.");
		var textBox = Dog.GetChildren().OfType<PetTextAccessory>().Single();
		Require(textBox.Text == input.Text && textBox.GetParent() == Dog, "The desktop Text Box attaches to the real dog and displays the edited message.");
		var textBounds = AccessoryGeometry.BoundsFromCorners(textBox, new Rect2(-PetTextAccessory.ReferenceSize * 0.5f, PetTextAccessory.ReferenceSize));
		Require(!Controls.GetDogMoveRect().Intersects(textBounds), "The Move dog label stays clear of above-dog text artwork.");
		var moveGrab = Controls.GetDogMoveRect().GetCenter(); var dogStart = GetField<Vector2>(_pet!, "_editPosition"); var textStart = textBox.GlobalPosition;
		Mouse(moveGrab, true); Controls.UpdatePointer(moveGrab + new Vector2(35, 20)); Mouse(moveGrab + new Vector2(35, 20), false);
		Require(GetField<Vector2>(_pet!, "_editPosition").IsEqualApprox(dogStart + new Vector2(35, 20)) && textBox.GlobalPosition.IsEqualApprox(textStart + new Vector2(35, 20)) && wardrobe.GetPlacement(id)!.Position.IsEqualApprox(anchor),
			"The visible Move dog label drags the dog and equipped text together without moving the text's anchor.");
		MoveDogTo(dogStart); Session.SelectAccessory(id);
		input.Text = "Snacks, please!"; input.EmitSignal(LineEdit.SignalName.TextChanged, input.Text); input.EmitSignal(LineEdit.SignalName.TextSubmitted, input.Text);
		Require(textBox.Text == input.Text, "Editing an equipped Text Box updates its desktop message immediately.");
		var picker = GetField<ColorPickerButton>(editor, "_colorPicker"); picker.Color = Colors.Gold;
		picker.EmitSignal(ColorPickerButton.SignalName.ColorChanged, Colors.Gold); picker.EmitSignal(ColorPickerButton.SignalName.PopupClosed);
		Require(textBox.TextColor == Colors.Gold, "The shared color wheel changes the desktop text color immediately.");
		var background = GetField<CheckBox>(editor, "_textBackgroundCheck"); var beforeToggle = wardrobe.GetPlacement(id)!;
		Require(background.ButtonPressed && textBox.BackgroundVisible, "Text Box starts with its readable background enabled.");
		background.ButtonPressed = false;
		Require(!wardrobe.GetTextBackgroundVisible() && !wardrobe.GetPlacement(id)!.BackgroundVisible && !textBox.BackgroundVisible &&
			wardrobe.GetPlacement(id)!.Text == beforeToggle.Text && wardrobe.GetPlacement(id)!.Tint == beforeToggle.Tint && wardrobe.GetPlacement(id)!.Position == beforeToggle.Position,
			"The background checkbox hides only the box surface, preserving its text, color and position.");
		var restored = LoadIsolatedWardrobe(); Require(restored.GetPlacement(id) == wardrobe.GetPlacement(id), "The text-background checkbox saves with the local outfit."); restored.Free();
		background.ButtonPressed = true;
		Require(textBox.BackgroundVisible && wardrobe.GetTextBackgroundVisible(), "The checkbox restores the text background immediately.");
		input.Text = "Stay cute!"; input.EmitSignal(LineEdit.SignalName.TextChanged, input.Text);
		Require(GetField<bool>(editor, "_textEditing"), "An unsubmitted message remains one active text-edit gesture before a desktop drag.");
		var beforeDrag = wardrobe.GetPlacement(id)!; var grab = textBox.GlobalPosition;
		Mouse(grab, true);
		Require(Session.IsDragging && !GetField<bool>(editor, "_textEditing"), "Starting direct desktop dragging commits the inspector's earlier text gesture before creating drag history.");
		Controls.UpdatePointer(grab + At(new Vector2(0.1f, 0.1f)) - At(Vector2.Zero)); DesktopKey(Key.Escape);
		Require(!Session.IsDragging && wardrobe.GetPlacement(id) == beforeDrag && wardrobe.GetText() == "Stay cute!",
			"Desktop Escape rolls back text placement while retaining the message edited in the catalog window.");
		restored = LoadIsolatedWardrobe(); Require(restored.GetPlacement(id) == beforeDrag, "Canceling desktop text dragging saves the retained message and restored anchor."); restored.Free();
		wardrobe.ClearHistory();
		input.Text = "One more"; input.EmitSignal(LineEdit.SignalName.TextChanged, input.Text);
		input.Text = "One more treat!"; input.EmitSignal(LineEdit.SignalName.TextChanged, input.Text); input.EmitSignal(LineEdit.SignalName.TextSubmitted, input.Text);
		Require(wardrobe.CanUndo && wardrobe.Undo() && wardrobe.GetText() == "Stay cute!" && !wardrobe.CanUndo,
			"Typing after desktop dragging forms a new message gesture that undoes in one step.");
		Require(wardrobe.Save(), "The text undo fixture saves.");
		window.Hide(); FinishFall();
		foreach (var direction in new[] { -1f, 1f })
		{
			SetField(_pet!, "_direction", direction); SetField(_pet!, "_stepPhase", 0f); CallPrivate(_pet!, "AnimateDog");
			var oldPosition = textBox.GlobalPosition; var right = textBox.ToGlobal(Vector2.Right) - textBox.GlobalPosition;
			Require(right.X > 0, "Text stays readable when the dog changes direction.");
			SetField(_pet!, "_stepPhase", Mathf.Pi * 0.5f); CallPrivate(_pet!, "AnimateDog");
			Require(!textBox.GlobalPosition.IsEqualApprox(oldPosition), "Equipped text follows the dog's resumed bounce.");
		}
		wardrobe.SetTransform(id, 2, 45);
		foreach (var position in new[] { AccessoryWardrobe.TextMinPosition, AccessoryWardrobe.TextMaxPosition })
		{
			wardrobe.Move(id, position); var (minX, maxX) = ((float, float))CallPrivate(_pet!, "GetWalkBounds")!;
			foreach (var direction in new[] { -1f, 1f }) foreach (var x in new[] { minX, maxX })
			{
				SetField(_pet!, "_direction", direction); SetField(_pet!, "_walkX", x); CallPrivate(_pet!, "AnimateDog");
				var bounds = AccessoryGeometry.BoundsFromCorners(textBox, new Rect2(-PetTextAccessory.ReferenceSize * 0.5f, PetTextAccessory.ReferenceSize));
				var size = GetField<Vector2I>(_pet!, "_windowSize");
				Require(bounds.Position.X >= -0.01f && bounds.Position.Y >= -0.01f && bounds.End.X <= size.X + 0.01f && bounds.End.Y <= size.Y + 0.01f,
					"Rotated Text Boxes stay inside the walking overlay at both edges and directions.");
			}
		}
		wardrobe.SetTransform(id, 1, 0); wardrobe.Move(id, anchor); SetField(_pet!, "_direction", 1f);
		window.ShowStatusWindow(); MoveDogTo(new Vector2(GetField<Vector2I>(_pet!, "_windowSize").X * 0.5f, 360)); Session.SelectAccessory(id);
		background.ButtonPressed = false;
		await VerifyNativeOverlayRegion("desktop-text-no-background");
		background.ButtonPressed = true;
		Mouse(textBox.GlobalPosition, true); Mouse(textBox.GlobalPosition, false);
		Require(backend.PendingGrantCount == grants, "Text-only clicks, editing and background changes leave the pending pet queue unchanged.");
		wardrobe.Remove(id); wardrobe.SetText(AccessoryWardrobe.DefaultText); wardrobe.SetTextBackgroundVisible(true); wardrobe.SetTint(id, Colors.White); wardrobe.SetTransform(id, 1, 0); Session.ClearSelection();
		Require(!Dog.GetChildren().OfType<PetTextAccessory>().Any(), "Removing Text Box removes its desktop drawing node.");
	}

	private async Task CaptureDesktopWorkspace(StatusWindow browser)
	{
		if (DisplayServer.GetName() == "headless") return;
		var wardrobe = _wardrobe!; var outfit = wardrobe.Equipped.ToArray(); var canUndo = wardrobe.CanUndo;
		var grants = GetField<BackendPetClient>(_pet!, "_backend").PendingGrantCount;
		var position = GetField<Vector2>(_pet!, "_editPosition"); Session.ClearSelection();
		wardrobe.BeginEdit("desktop wardrobe screenshot");
		try
		{
			wardrobe.Clear(); wardrobe.SetTint("FairyWing.jpg", new Color(0.92f, 0.55f, 0.88f, 1));
			wardrobe.SetTransform("FairyWing.jpg", 1.8f, 0); wardrobe.Equip("FairyWing.jpg", new Vector2(0.4f, 0.42f));
			wardrobe.SetTint("SafetyGlasses.jpg", Colors.White); wardrobe.SetTransform("SafetyGlasses.jpg", 1.25f, 0);
			wardrobe.Equip("SafetyGlasses.jpg", new Vector2(0.8f, 0.29f));
			_pet!.MoveEditingDog(new Vector2(GetField<Vector2I>(_pet!, "_windowSize").X * 0.3f, 360));
			Session.ChooseAccessory("SafetyGlasses.jpg"); await SettleFrames(); CallPrivate(_pet!, "UpdateDogMouseRegion");
			await VerifyNativeOverlayRegion("desktop-accessory-handles");
			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			using var browserImage = browser.GetTexture().GetImage(); using var overlay = _pet.GetViewport().GetTexture().GetImage();
			var bounds = Controls.GetRenderBounds()!.Value.Grow(18); var rect = new Rect2I(new Vector2I(Mathf.FloorToInt(bounds.Position.X), Mathf.FloorToInt(bounds.Position.Y)),
				new Vector2I(Mathf.CeilToInt(bounds.Size.X), Mathf.CeilToInt(bounds.Size.Y))).Intersection(new Rect2I(Vector2I.Zero, GetField<Vector2I>(_pet, "_windowSize")));
			using var dogImage = overlay.GetRegion(rect); browserImage.Convert(Image.Format.Rgba8); dogImage.Convert(Image.Format.Rgba8);
			Require(browserImage.SavePng($"{OutputDirectory}/wardrobe-browser-preview.png") == Error.Ok && dogImage.SavePng($"{OutputDirectory}/desktop-accessory-preview.png") == Error.Ok,
				"Native catalog and cropped direct-desktop editing screenshots save.");
			var browserOrigin = new Vector2I(dogImage.GetWidth() + 20, 0); var dogOrigin = new Vector2I(0, 80);
			using var combined = Image.CreateEmpty(browserOrigin.X + browserImage.GetWidth(), Math.Max(browserImage.GetHeight(), dogOrigin.Y + dogImage.GetHeight()), false, Image.Format.Rgba8);
			combined.Fill(Color.FromHtml("#26372c"));
			combined.BlitRect(dogImage, new Rect2I(Vector2I.Zero, new Vector2I(dogImage.GetWidth(), dogImage.GetHeight())), dogOrigin);
			combined.BlitRect(browserImage, new Rect2I(Vector2I.Zero, new Vector2I(browserImage.GetWidth(), browserImage.GetHeight())), browserOrigin);
			Require(combined.GetPixel(dogOrigin.X, dogOrigin.Y) == dogImage.GetPixel(0, 0) && combined.GetPixel(browserOrigin.X, browserOrigin.Y) == browserImage.GetPixel(0, 0),
				"The review composite preserves original native pixels from the real desktop dog and catalog.");
			Require(combined.SavePng($"{OutputDirectory}/wardrobe-workspace-preview.png") == Error.Ok, "The direct-desktop wardrobe composite saves for visual review.");
		}
		finally
		{
			// ClearSelection commits inspector edits; finish the fixture transaction first.
			wardrobe.CancelEdit(); Session.ClearSelection(); _pet!.MoveEditingDog(position); await SettleFrames();
		}
		Require(wardrobe.Equipped.SequenceEqual(outfit) && wardrobe.CanUndo == canUndo && GetField<BackendPetClient>(_pet!, "_backend").PendingGrantCount == grants,
			"The visual fixture restores the actual outfit and undo history without changing pet grants.");
	}


	private async Task VerifyHeartFeedback(Sprite2D dog)
	{
		CallPrivate(_pet!, "UpdateFloatingHearts", 10f);
		SetField(_pet!, "_walkX", GetField<Vector2I>(_pet!, "_windowSize").X * 0.5f); SetField(_pet!, "_stepPhase", 0f); CallPrivate(_pet!, "AnimateDog");
		var backend = GetField<BackendPetClient>(_pet!, "_backend"); var grants = backend.PendingGrantCount;
		using var dogImage = dog.Texture.GetImage();
		var pixel = dog.ToGlobal(FindOpaquePixel(dogImage) + Vector2.One * 0.5f - dog.Texture.GetSize() * 0.5f);
		_pet!._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pixel });
		var hearts = GetField<System.Collections.IList>(_pet, "_floatingHearts");
		Require(hearts.Count == 1 && backend.PendingGrantCount == grants + 1, "One valid dog click produces one heart and exactly one pending grant.");
		var sprite = (Sprite2D)hearts[0]!.GetType().GetProperty("Sprite")!.GetValue(hearts[0])!;
		var start = sprite.Position; var initialScale = sprite.Scale;
		CallPrivate(_pet, "UpdateFloatingHearts", 0.12f);
		var peakScale = sprite.Scale;
		Require(peakScale.X > initialScale.X && Mathf.Max(sprite.GetRect().Size.X * peakScale.X, sprite.GetRect().Size.Y * peakScale.Y) >= 40 && sprite.Modulate.A > 0.95f,
			"The heart pops to an obvious visible size before it fades.");
		for (var index = 0; index < 14; index++)
		{
			CallPrivate(_pet, "UpdateFloatingHearts", 0.1f);
			var bounds = AccessoryGeometry.BoundsFromCorners(sprite, sprite.GetRect()); var window = GetField<Vector2I>(_pet, "_windowSize");
			Require(bounds.Position.Y >= 0 && bounds.End.Y <= window.Y && bounds.Position.X >= 0 && bounds.End.X <= window.X,
				"The complete heart trajectory fits inside the transparent overlay window.");
			if (index == 3 && DisplayServer.GetName() != "headless")
			{
				CallPrivate(_pet, "UpdateDogMouseRegion");
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				using var capture = _pet.GetViewport().GetTexture().GetImage();
				var crop = new Rect2I(new Vector2I((int)GetField<Node2D>(_pet, "_footAnchor").Position.X - 140, 0), new Vector2I(280, window.Y));
				using var image = capture.GetRegion(crop);
				Require(image.SavePng($"{OutputDirectory}/heart-feedback-preview.png") == Error.Ok, "Native heart feedback preview saves.");
			}
		}
		Require(sprite.Position.Y < start.Y - 50 && Mathf.Abs(sprite.Position.X - start.X) > 20 && sprite.Scale.X < peakScale.X && sprite.Modulate.A < 0.5f,
			"The heart slowly moves upward and away while becoming smaller and fading.");
		_pet._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = sprite.GlobalPosition });
		Require(backend.PendingGrantCount == grants + 1, "Clicking heart-only pixels does not grant another pet.");
		CallPrivate(_pet, "UpdateFloatingHearts", 1f);
		Require(hearts.Count == 0, "Finished hearts are removed.");
		for (var index = 0; index < 15; index++) CallPrivate(_pet, "SpawnFloatingHeart");
		Require(hearts.Count == 8, "Rapid petting keeps the visual feedback bounded.");
		CallPrivate(_pet, "UpdateFloatingHearts", 10f);
		var (minX, maxX) = ((float, float))CallPrivate(_pet, "GetWalkBounds")!;
		foreach (var direction in new[] { -1f, 1f }) foreach (var x in new[] { minX, maxX })
		{
			SetField(_pet, "_direction", direction); SetField(_pet, "_walkX", x); CallPrivate(_pet, "AnimateDog");
			CallPrivate(_pet, "SpawnFloatingHeart");
			var edgeHeart = (Sprite2D)hearts[0]!.GetType().GetProperty("Sprite")!.GetValue(hearts[0])!;
			foreach (var delta in new[] { 0.12f, 0.3f, 0.6f, 0.6f })
			{
				CallPrivate(_pet, "UpdateFloatingHearts", delta);
				var bounds = AccessoryGeometry.BoundsFromCorners(edgeHeart, edgeHeart.GetRect()); var window = GetField<Vector2I>(_pet, "_windowSize");
				Require(bounds.Position.X >= 0 && bounds.Position.Y >= 0 && bounds.End.X <= window.X && bounds.End.Y <= window.Y,
					"Floating hearts remain inside the overlay at both walk edges and facing directions.");
			}
			CallPrivate(_pet, "UpdateFloatingHearts", 10f);
		}
		SetField(_pet, "_direction", 1f); SetField(_pet, "_walkX", GetField<Vector2I>(_pet, "_windowSize").X * 0.5f); CallPrivate(_pet, "AnimateDog");
	}

	private static System.Collections.Generic.IEnumerable<Node> Descendants(Node root)
	{
		foreach (var child in root.GetChildren())
		{
			yield return child;
			foreach (var nested in Descendants(child)) yield return nested;
		}
	}

	private static T? FindDescendant<T>(Node root) where T : Node
	{
		foreach (var child in root.GetChildren())
		{
			if (child is T match) return match;
			var nested = FindDescendant<T>(child);
			if (nested != null) return nested;
		}
		return null;
	}

	private static Vector2 FindOpaquePixel(Image image)
	{
		for (var y = 0; y < image.GetHeight(); y++)
			for (var x = 0; x < image.GetWidth(); x++)
				if (image.GetPixel(x, y).A > 0.9f) return new Vector2(x, y);
		throw new InvalidOperationException("Dog texture has no opaque pixels.");
	}

	private void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
		_assertions++;
	}

	private static T GetField<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
	private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
	private static object? CallPrivate(object target, string name, params object[] args) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);
	[StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
	[DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
	[DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
	[DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
	[DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect bounds);
	[DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
	[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
