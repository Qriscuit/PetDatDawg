using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

// Runs both as an editor tool probe and as a native scene through Run-UiAuthoringSmoke.ps1.
[Tool]
public partial class UiAuthoringSmokeTest : Node
{
	private const string OutputDirectory = "res://.godot/ui-authoring-smoke";
	private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
	private readonly List<Node> _fixtures = new();
	private readonly List<string> _editedFloatingScenePaths = new();
	private readonly string _editedScenePath = $"{OutputDirectory}/edited-menu-{Guid.NewGuid():N}.tscn";
	private readonly string _editedAppearancePath = $"{OutputDirectory}/edited-appearance-{Guid.NewGuid():N}.tres";
	private int _assertions;

	public override void _Ready()
	{
		if (Engine.IsEditorHint())
		{
			if (OS.GetCmdlineUserArgs().Contains("--ui-authoring-editor-check")) CallDeferred(nameof(RunEditorProbe));
			return;
		}
		CallDeferred(nameof(Run));
	}

	private void RunEditorProbe()
	{
		var exitCode = 0;
		try
		{
			Require(Engine.IsEditorHint(), "The tool probe runs inside the real Godot editor.");
			VerifyTemplates();
			var windowCount = DisplayServer.GetWindowList().Length;
			var status = LoadScene<StatusWindow>("res://StatusWindow.tscn");
			status.Visible = false; status.InitialTab = 3;
			var authoredSize = status.Size; var authoredTheme = status.Theme;
			AddFixture(status);
			Require(status.Size == authoredSize && status.Theme == authoredTheme && !status.Visible,
				"Editor Ready preserves authored window properties without opening a native menu.");
			Require(GetField<object?>(status, "_settings") == null && GetField<object?>(status, "_editingSession") == null
				&& GetField<object?>(status, "_patrolRoute") == null, "Editor preview never attaches gameplay or preference models.");
			Require(status.GetNode<Control>("%SettingsPage").Visible && !status.GetNode<Control>("%ItemsPage").Visible,
				"InitialTab previews the authored Settings page in the editor.");
			status.InitialTab = 0;
			Require(status.GetNode<Control>("%DogsPage").Visible && !status.GetNode<Control>("%SettingsPage").Visible,
				"Changing InitialTab updates the editor preview without gameplay.");
			VerifyDogCollectionStructure(status);
			var editor = status.GetNode<AccessoryEditor>("%AccessoryEditor");
			Require(GetField<object?>(editor, "_wardrobe") == null && GetField<object?>(editor, "_session") == null,
				"Accessory inspector's editor preview does not subscribe to gameplay.");
			foreach (var path in new[] { "res://UI/PatrolRouteToolbar.tscn", "res://UI/AccessoryLayerMenu.tscn" })
			{
				var popup = LoadScene<Window>(path); popup.Visible = false;
				var size = popup.Size; var theme = popup.Theme;
				AddFixture(popup);
				Require(!popup.Visible && popup.Size == size && popup.Theme == theme, $"Editor Ready preserves {path} without opening it.");
			}
			Require(DisplayServer.GetWindowList().Length == windowCount, "Editor tool previews create no native windows.");
			GD.Print($"UI_AUTHORING_EDITOR_PASS: {_assertions} assertions");
		}
		catch (Exception exception) { exitCode = 1; GD.PushError($"UI_AUTHORING_EDITOR_FAIL: {exception}"); }
		finally { FreeFixtures(); GC.Collect(); GC.WaitForPendingFinalizers(); GetTree().Quit(exitCode); }
	}

	private async void Run()
	{
		var exitCode = 0;
		try
		{
			Require(System.Environment.GetEnvironmentVariable("PDD_DISABLE_STEAM") == "1"
				&& System.Environment.GetEnvironmentVariable("PDD_DISABLE_TRAY") == "1", "UI smoke disables Steam and tray.");
			Require(OS.GetUserDataDir().Contains("ui-authoring-smoke-home", StringComparison.OrdinalIgnoreCase),
				"The runner redirects settings to an isolated workspace profile.");
			Require(Engine.GetVersionInfo()["string"].AsString().StartsWith("4.6.3", StringComparison.Ordinal), "UI smoke uses Godot 4.6.3.");
			DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(OutputDirectory));
			System.IO.File.Delete(ProjectSettings.GlobalizePath("user://menu_layout.cfg"));
			VerifyTemplates();
			var wardrobe = GetNode<AccessoryWardrobe>("/root/AccessoryWardrobe");
			wardrobe.StoragePath = $"{OutputDirectory}/outfit.cfg";
			var route = GetNode<PatrolRoute>("/root/PatrolRoute"); route.StoragePath = $"{OutputDirectory}/route.cfg";
			var settings = GetNode<PetSettings>("/root/PetSettings"); settings.SetUiScale(1); settings.SetFontScale(1);
			settings.SetStationary(false); settings.SetBounceEnabled(true);
			await VerifySavedSceneOverrides(settings);
			await VerifyMenuScaleAndScrolling(settings);
			await VerifyFontScaling(settings);
			await VerifyMovementSettings(settings, route);
			await VerifyCounterAndMenuSettings(settings);
			await VerifyAccessoryTemplateOverrides(wardrobe);
			VerifyDesktopAppearanceOverrides();
			await VerifyFloatingTemplates(wardrobe, route);
			await VerifyTextTemplateOnDesktop(wardrobe);
			GD.Print($"UI_AUTHORING_SMOKE_PASS: {_assertions} assertions");
		}
		catch (Exception exception) { exitCode = 1; GD.PushError($"UI_AUTHORING_SMOKE_FAIL: {exception}"); }
		finally
		{
			FreeFixtures();
			System.IO.File.Delete(ProjectSettings.GlobalizePath(_editedScenePath));
			System.IO.File.Delete(ProjectSettings.GlobalizePath(_editedAppearancePath));
			foreach (var path in _editedFloatingScenePaths) System.IO.File.Delete(ProjectSettings.GlobalizePath(path));
			GetTree().Quit(exitCode);
		}
	}

	private void VerifyTemplates()
	{
		Require(ProjectSettings.GetSetting("gui/theme/custom").AsString() == "res://UI/Theme/DefaultTheme.tres",
			"The project uses the authored shared default theme.");
		Require(ResourceLoader.Load<Theme>("res://UI/Theme/DefaultTheme.tres") != null, "The shared theme loads.");
		var paths = ScenePaths("res://UI").Prepend("res://StatusWindow.tscn").ToArray();
		Require(paths.Length >= 6, "The status menu and reusable UI templates are authored scenes.");
		var templateReferences = 0;
		foreach (var path in paths)
		{
			var scene = ResourceLoader.Load<PackedScene>(path);
			Require(scene != null && scene.CanInstantiate(), $"Authored scene loads: {path}");
			var node = scene!.Instantiate();
			foreach (var item in Descendants(node).Prepend(node))
			{
				var properties = item.GetPropertyList();
				foreach (var property in properties)
				{
					var usage = (PropertyUsageFlags)property["usage"].AsInt64();
					if ((usage & PropertyUsageFlags.Editor) == 0 || (usage & PropertyUsageFlags.Storage) == 0) continue;
					using var value = item.Get(property["name"].AsStringName());
					if (value.VariantType != Variant.Type.Object || value.AsGodotObject() is not PackedScene template) continue;
					templateReferences++;
					Require(template.CanInstantiate(), $"Exported template is usable on {item.Name}.");
				}
			}
			node.Free();
		}
		Require(templateReferences >= 3, "The accessory browser exposes reusable card, category and color-control templates.");
	}

	private async Task VerifySavedSceneOverrides(PetSettings settings)
	{
		var authored = LoadScene<StatusWindow>("res://StatusWindow.tscn");
		authored.Visible = false; authored.InitialTab = 3;
		authored.ShowDetailsText = "Connection details"; authored.HideDetailsText = "Close details";
		authored.SetRouteText = "Draw my route"; authored.EditRouteText = "Change my route";
		authored.PetsTotalFormat = "Confirmed: {count}"; authored.ServiceStatusFormat = "My service: {status}";
		authored.ConnectedText = "Everything connected"; authored.NoPatrolRouteText = "Ready to draw a route";
		authored.StationaryPatrolText = "My dog is resting";
		authored.GetNode<CheckBox>("%Stationary").Text = "My resting preference";
		authored.GetNode<CheckBox>("%BounceEnabled").Text = "My bounce preference";
		authored.GetNode<CheckBox>("%FlipHorizontally").Text = "My mirror preference";
		var authoredTraversal = authored.GetNode<OptionButton>("%PatrolTraversalMode");
		authoredTraversal.SetItemText(authoredTraversal.GetItemIndex((int)PatrolTraversalMode.Loop), "My loop");
		authoredTraversal.SetItemText(authoredTraversal.GetItemIndex((int)PatrolTraversalMode.BackAndForth), "My return walk");
		authored.GetNode<Label>("%PatrolRouteSaveStatus").Text = "My save-failure explanation";
		authored.GetNode<Label>("%EmptyDogPresets").Text = "My saved companions will appear here";
		authored.GetNode<Button>("%OpenDogPresets").TooltipText = "My preset-management explanation";
		authored.GetNode<GridContainer>("%DogPresetGrid").AddThemeConstantOverride("h_separation", 19);
		authored.GetNode<GridContainer>("%DogPresetGrid").AddThemeConstantOverride("v_separation", 21);
		authored.GetNode<GridContainer>("%DogGrid").AddThemeConstantOverride("h_separation", 23);
		authored.Size = new Vector2I(688, 730); authored.MinSize = new Vector2I(400, 500);
		authored.ContentScaleFactor = 1.1f;
		var theme = (Theme)authored.Theme.Duplicate(true);
		theme.ResourceName = "AuthoredUiSmokeTheme"; theme.DefaultFontSize = 17;
		theme.SetColor("font_color", "Label", Colors.DarkSlateBlue); authored.Theme = theme;
		var layout = authored.GetNode<VBoxContainer>("Background/Margin/Layout");
		layout.AddThemeConstantOverride("separation", 17);
		var button = authored.GetNode<Button>("%DogsTab");
		button.Text = "My dogs"; button.CustomMinimumSize = new Vector2(102, 39);
		button.AddThemeFontSizeOverride("font_size", 21);
		button.AddThemeFontOverride("font", new SystemFont { ResourceName = "AuthoredUiSmokeFont", FontNames = new[] { "Segoe UI" }, FontWeight = 700 });
		button.AddThemeStyleboxOverride("normal", new StyleBoxFlat { ResourceName = "AuthoredUiSmokeStyle", BgColor = Colors.MediumPurple });
		// Unique names keep bindings working when a designer moves controls within their scene.
		button.Owner = null; button.GetParent().RemoveChild(button);
		authored.GetNode<Control>("Background/Margin/Layout/Header/Content/BrandRow").AddChild(button);
		button.Owner = authored; button.UniqueNameInOwner = true;
		var packed = new PackedScene();
		Require(packed.Pack(authored) == Error.Ok && ResourceSaver.Save(packed, _editedScenePath) == Error.Ok,
			"Designer changes can be packed and saved as a real scene.");
		authored.Free();
		var reloaded = ResourceLoader.Load<PackedScene>(_editedScenePath, "", ResourceLoader.CacheMode.Ignore).Instantiate<StatusWindow>();
		reloaded.Visible = false;
		AssertStatusOverrides(reloaded, "after save/reload");
		AddFixture(reloaded); reloaded.Configure(settings); reloaded.ShowStatusWindow();
		await Settle();
		AssertStatusOverrides(reloaded, "after runtime Ready");
		Require(reloaded.Size == new Vector2I(688, 730) && Mathf.IsEqualApprox(reloaded.ContentScaleFactor, 1.1f),
			"Runtime startup keeps the authored menu dimensions and content factor.");
		Require(reloaded.GetNode<Control>("%SettingsPage").IsVisibleInTree(), "Authored InitialTab controls runtime startup.");
		Require(GetField<AccessoryEditor>(reloaded, "_accessoryEditor").GetChildCount() > 0, "The authored accessory inspector binds its live controls.");
		reloaded.UpdateStatus(null, "Connecting", "Offline");
		Require(reloaded.GetNode<Label>("%PatrolRouteSaveStatus").Text == "My save-failure explanation",
			"Live status refresh preserves authored save explanations.");
		Require(reloaded.GetNode<Button>("%EditPatrolRoute").Text == "Draw my route", "Route captions use their Inspector-exported text.");
		reloaded.UpdateStatus(1234, "Ready", "Ready", true, true);
		Require(reloaded.GetNode<Label>("%PetsValue").Text == "Confirmed: 1,234"
			&& reloaded.GetNodeOrNull<Label>("%PendingPets") == null
			&& reloaded.GetNode<Label>("%BackendStatus").Text == "My service: Ready"
			&& reloaded.GetNode<Label>("%ConnectionSummary").Text == "Everything connected"
			&& reloaded.GetNode<Label>("%PatrolRouteSummary").Text == "Ready to draw a route",
			"Saved live-copy formats substitute data while preserving Inspector-authored presentation.");
		reloaded.UpdateStatus(1234, "Ready", "Ready", true, true, inputReady: false, inputStatus: "Keyboard monitor unavailable");
		Require(reloaded.GetNode<Label>("%PetsValue").Text == "Confirmed: 1,234"
			&& reloaded.GetNode<Label>("%ConnectionSummary").Text.Contains("Desktop input is unavailable", StringComparison.Ordinal)
			&& reloaded.GetNode<Label>("%SteamStatus").Text.Contains("Keyboard monitor unavailable", StringComparison.Ordinal),
			"Unavailable desktop input surfaces a connection warning and technical detail while preserving the confirmed Pets balance.");
		reloaded.UpdateStatus(1234, "Ready", "Ready", true, true);
		Require(reloaded.GetNode<Label>("%ConnectionSummary").Text == "Everything connected"
			&& !reloaded.GetNode<Label>("%SteamStatus").Text.Contains("Keyboard monitor unavailable", StringComparison.Ordinal),
			"Callers using the original status defaults retain their authored connected caption and clear stale input warnings.");
		var detailsToggle = reloaded.GetNode<Button>("%ConnectionDetailsToggle");
		detailsToggle.ButtonPressed = true;
		Require(detailsToggle.Text == "Close details", "Expanded connection details use their Inspector-exported caption.");
		detailsToggle.ButtonPressed = false;
		Require(detailsToggle.Text == "Connection details", "Collapsed connection details use their Inspector-exported caption.");
		var originalVisibility = settings.DogTransparency;
		settings.SetDogTransparency(originalVisibility > 0.8f ? 0.6f : 1f);
		await Settle();
		AssertStatusOverrides(reloaded, "after an ordinary preference change");
		Require(reloaded.Size == new Vector2I(688, 730), "Dog preferences do not overwrite authored menu geometry.");
		settings.SetUiScale(0.75f); await Settle();
		AssertStatusOverrides(reloaded, "after menu scaling");
		Require(Mathf.IsEqualApprox(reloaded.ContentScaleFactor, 0.825f) && reloaded.Size == new Vector2I(688, 730)
			&& reloaded.MinSize == new Vector2I(400, 500),
			"Menu size scales authored content while preserving native dimensions, minimum size and theme.");
		settings.SetUiScale(1); settings.SetDogTransparency(originalVisibility); await Settle();
		Require(reloaded.Size == new Vector2I(688, 730) && Mathf.IsEqualApprox(reloaded.ContentScaleFactor, 1.1f),
			"Returning menu scale to 100% restores the content factor and preserves native size.");
		reloaded.GetNode<Button>("%DogsTab").EmitSignal(BaseButton.SignalName.Pressed);
		Require(reloaded.GetNode<Control>("%DogsPage").Visible, "The reparented unique-name tab still runs its bound action.");
		VerifyDogCollectionStructure(reloaded);
		await Capture(reloaded, "authored-dogs");
		reloaded.GetNode<Button>("%SettingsTab").EmitSignal(BaseButton.SignalName.Pressed);
		await Capture(reloaded, "authored-settings");
		reloaded.GetNode<Button>("%ItemsTab").EmitSignal(BaseButton.SignalName.Pressed);
		await Capture(reloaded, "authored-items");
		reloaded.Hide(); reloaded.SetProcess(false);
	}

	private async Task VerifyMenuScaleAndScrolling(PetSettings settings)
	{
		System.IO.File.Delete(ProjectSettings.GlobalizePath("user://menu_layout.cfg"));
		settings.SetUiScale(1); settings.DismissWelcome();
		var menu = LoadScene<StatusWindow>("res://StatusWindow.tscn");
		menu.Visible = false; menu.InitialTab = 3;
		AddFixture(menu); menu.Configure(settings); menu.ShowStatusWindow();
		await Settle();
		var size = menu.Size; var minimum = menu.MinSize; var position = menu.Position;
		var scroll = menu.GetNode<ScrollContainer>("%SettingsPage");
		var sliders = new[] { menu.GetNode<HSlider>("%DogTransparency"), menu.GetNode<HSlider>("%DogScale"), menu.GetNode<HSlider>("%UiScale"), menu.GetNode<HSlider>("%FontScale") };
		foreach (var scale in new[] { PetSettings.MinUiScale, 1f, PetSettings.MaxUiScale })
		{
			settings.SetUiScale(scale); await Settle();
			Require(Mathf.IsEqualApprox(menu.ContentScaleFactor, scale)
				&& menu.Size == size && menu.MinSize == minimum && menu.Position == position,
				$"At {scale:P0}, menu scaling changes content without moving or resizing the native window.");
			Require(menu.GetVisibleRect().Grow(1).Encloses(menu.GetNode<Control>("Background").GetGlobalRect()),
				$"The menu layout fits the fixed window at {scale:P0}.");
			foreach (var slider in sliders)
			{
				Require(!slider.Scrollable, $"{slider.Name} keeps mouse-wheel input reserved for scrolling the options.");
				scroll.EnsureControlVisible(slider); await Settle();
				var visible = slider.GetGlobalRect().Intersection(scroll.GetGlobalRect());
				Require(visible.HasArea(), $"The {slider.Name} control remains reachable at {scale:P0}.");
				var before = scroll.ScrollVertical; var bar = scroll.GetVScrollBar();
				var direction = before < bar.MaxValue - bar.Page - 1 ? MouseButton.WheelDown : MouseButton.WheelUp;
				var transparency = settings.DogTransparency; var dogScale = settings.DogScale;
				PushMouseButton(menu, visible.GetCenter(), direction, true); await Settle();
				Require(Mathf.IsEqualApprox(settings.UiScale, scale) && Mathf.IsEqualApprox(settings.DogScale, dogScale)
					&& Mathf.IsEqualApprox(settings.FontScale, 1) && Mathf.IsEqualApprox(settings.DogTransparency, transparency) && menu.Size == size && menu.Position == position,
					$"Wheeling over {slider.Name} scrolls without changing any scale, visibility or window geometry.");
				if (bar.MaxValue > bar.Page + 1)
					Require(direction == MouseButton.WheelDown ? scroll.ScrollVertical > before : scroll.ScrollVertical < before,
						$"Mouse-wheel input over {slider.Name} reaches the options scroll container.");
			}
			scroll.ScrollVertical = int.MaxValue; await Settle();
			var atBottom = scroll.ScrollVertical;
			for (var i = 0; i < 6; i++) PushMouseButton(menu, scroll.GetGlobalRect().GetCenter(), MouseButton.WheelDown, true);
			await Settle();
			Require(scroll.ScrollVertical == atBottom && Mathf.IsEqualApprox(settings.UiScale, scale)
				&& menu.Size == size && menu.MinSize == minimum && menu.Position == position,
				$"Continuing to scroll at the end of options stays stable at {scale:P0}.");
		}
		menu.Size = minimum; await Settle();
		Require(menu.Size == minimum && menu.GetVisibleRect().Grow(1).Encloses(menu.GetNode<Control>("Background").GetGlobalRect())
			&& new[] { "%DogsTab", "%ItemsTab", "%ShopTab", "%SettingsTab" }.All(path => menu.GetVisibleRect().Grow(1).Encloses(menu.GetNode<Control>(path).GetGlobalRect())),
			"At maximum UI scale, the minimum native window still contains every tab and the options layout.");
		menu.GetNode<Button>("%ItemsTab").EmitSignal(BaseButton.SignalName.Pressed); await Settle();
		var itemsScroll = menu.GetNode<ScrollContainer>("%ItemsScroll");
		var editor = menu.GetNode<AccessoryEditor>("%AccessoryEditor");
		Require(Ancestor<ScrollContainer>(editor) == itemsScroll && itemsScroll.HorizontalScrollMode == ScrollContainer.ScrollMode.ShowNever
			&& editor.GetNode<BoxContainer>("Workspace").Vertical && editor.GetGlobalRect().Position.X >= itemsScroll.GetGlobalRect().Position.X - 1
			&& editor.GetGlobalRect().End.X <= itemsScroll.GetGlobalRect().End.X + 1,
			"At maximum scale in the minimum window, Items uses a vertical workspace that fits the available width.");
		itemsScroll.EnsureControlVisible(editor.GetNode<Control>("%AccessoryCatalogScroll")); await Settle();
		Require(editor.GetNode<Control>("%AccessoryCatalogScroll").GetGlobalRect().Intersection(itemsScroll.GetGlobalRect()).HasArea()
			&& menu.Size == minimum && menu.GetVisibleRect().Grow(1).Encloses(menu.GetNode<Control>("Background").GetGlobalRect()),
			"The enlarged Items catalog remains reachable by scrolling without resizing the native window.");
		await Capture(menu, "scaled-items-minimum");
		menu.GetNode<Button>("%SettingsTab").EmitSignal(BaseButton.SignalName.Pressed); await Settle();
		menu.Size = size; await Settle();
		settings.SetUiScale(1); await Settle();
		var uiScaleSlider = menu.GetNode<HSlider>("%UiScale");
		scroll.EnsureControlVisible(uiScaleSlider); await Settle();
		var click = uiScaleSlider.GetGlobalRect().Position + uiScaleSlider.Size * new Vector2(0.7f, 0.5f);
		PushMouseButton(menu, click, MouseButton.Left, true);
		PushMouseButton(menu, click, MouseButton.Left, false); await Settle();
		Require(settings.UiScale > 1.3f && menu.Size == size && menu.Position == position,
			"Clicking the menu-size slider still changes content scale without changing native geometry.");
		settings.SetUiScale(1); await Settle();
		// Resizing the native window remains independent from changing the saved content scale.
		var manualSize = size + new Vector2I(64, 40);
		if (DisplayServer.GetName() != "headless")
		{
			var usable = DisplayServer.ScreenGetUsableRect(menu.CurrentScreen);
			position = usable.Position + new Vector2I(24, 56); menu.Position = position;
			manualSize = manualSize.Min(usable.Size - new Vector2I(48, 96)).Max(minimum);
		}
		menu.Size = manualSize; await Settle();
		var resized = menu.Size;
		settings.SetUiScale(1.25f); await Settle();
		Require(menu.Size == resized && menu.MinSize == minimum && menu.Position == position,
			"Changing menu scale preserves a player-resized native window.");
		menu.EmitSignal(Window.SignalName.CloseRequested); await Settle();
		settings.SetUiScale(PetSettings.MinUiScale); await Settle();
		menu.ShowStatusWindow(); await Settle();
		Require(menu.Size == resized && menu.Position == position, "Reopening the menu preserves manual geometry after a scale change.");
		menu.EmitSignal(Window.SignalName.CloseRequested); await Settle();
		var saved = new ConfigFile();
		Require(saved.Load("user://menu_layout.cfg") == Error.Ok && saved.GetValue("menu", "layout_version").AsInt32() == 3
			&& saved.GetValue("menu", "size").AsVector2I() == resized, "Menu layout saves native dimensions independently from content scale.");
		settings.SetUiScale(PetSettings.MaxUiScale);
		var restored = LoadScene<StatusWindow>("res://StatusWindow.tscn"); restored.Visible = false; restored.InitialTab = 3;
		AddFixture(restored); restored.Configure(settings); restored.ShowStatusWindow(); await Settle();
		Require(restored.Size == resized && restored.Position == position && restored.MinSize == minimum
			&& Mathf.IsEqualApprox(restored.ContentScaleFactor, PetSettings.MaxUiScale),
			"A fresh menu restores saved manual geometry even with a different saved UI scale.");
		await Capture(restored, "scaled-settings");
		await VerifyPresetMenuScaling(settings, restored);
		restored.Hide(); menu.Hide(); menu.SetProcess(false); restored.SetProcess(false); settings.SetUiScale(1);
	}

	private async Task VerifyPresetMenuScaling(PetSettings settings, Window owner)
	{
		var outfitPath = $"{OutputDirectory}/menu-scale-outfit.cfg";
		var presetPath = $"{OutputDirectory}/menu-scale-presets.cfg";
		System.IO.File.Delete(ProjectSettings.GlobalizePath(outfitPath));
		System.IO.File.Delete(ProjectSettings.GlobalizePath(presetPath));
		var wardrobe = new AccessoryWardrobe { StoragePath = outfitPath, PresetStoragePath = presetPath };
		AddFixture(wardrobe);
		Require(wardrobe.SavePreset("Scaled menu outfit"), "A local starter-dog preset is available for the scaled browser check.");
		var browser = LoadScene<DogPresetsWindow>("res://DogPresetsWindow.tscn"); browser.Visible = false;
		AddFixture(browser); browser.Configure(wardrobe);
		var minimum = browser.MinSize; browser.Size = minimum; browser.ShowPresets(owner); await Settle();
		var scroll = browser.GetNode<ScrollContainer>("%ContentScroll");
		var grid = browser.GetNode<GridContainer>("%PresetGrid");
		Require(browser.Size == minimum && browser.MinSize == minimum && Mathf.IsEqualApprox(browser.ContentScaleFactor, PetSettings.MaxUiScale)
			&& browser.GetVisibleRect().Grow(1).Encloses(browser.GetNode<Control>("Background").GetGlobalRect())
			&& grid.Columns == 1 && grid.GetGlobalRect().End.X <= scroll.GetGlobalRect().End.X + 1,
			"The preset browser fits one column at 175% in its unchanged 520 by 400 native minimum.");
		foreach (var control in new Control[] { browser.GetNode<LineEdit>("%PresetName"), browser.GetNode<Button>("%SavePreset"), grid.GetChild(0).GetNode<Button>("%DeletePreset") })
		{
			scroll.EnsureControlVisible(control); await Settle();
			Require(scroll.GetGlobalRect().Grow(1).Encloses(control.GetGlobalRect()),
				$"The preset browser's {control.Name} control remains reachable by scrolling at maximum scale.");
		}
		await Capture(browser, "scaled-presets-minimum");
		browser.Size = minimum + new Vector2I(64, 40); await Settle();
		var resized = browser.Size; browser.Hide(); settings.SetUiScale(PetSettings.MinUiScale); browser.ShowPresets(owner); await Settle();
		Require(browser.Size == resized && browser.MinSize == minimum && Mathf.IsEqualApprox(browser.ContentScaleFactor, PetSettings.MinUiScale),
			"Reopening presets with a different UI scale preserves manual size and authored minimum.");
		browser.Hide();
	}

	private async Task VerifyFontScaling(PetSettings settings)
	{
		settings.SetUiScale(1); settings.SetFontScale(1);
		var menu = LoadScene<StatusWindow>("res://StatusWindow.tscn"); menu.Visible = false; menu.InitialTab = 3;
		var authoredText = new Label { Text = "Authored custom text", Visible = false };
		authoredText.AddThemeFontSizeOverride("font_size", 21); menu.GetNode<Control>("%DogsPage").AddChild(authoredText);
		AddFixture(menu); menu.Configure(settings); menu.ShowStatusWindow(); await Settle();
		var size = menu.Size; var minimum = menu.MinSize; var position = menu.Position;
		var theme = menu.Theme; var themeSize = theme.DefaultFontSize;
		var value = menu.GetNode<Label>("%FontScaleValue");
		var inherited = menu.GetNode<Button>("%SettingsTab"); var inheritedSize = inherited.GetThemeFontSize("font_size");
		var inheritedOverride = inherited.HasThemeFontSizeOverride("font_size");
		var slider = menu.GetNode<HSlider>("%FontScale"); var scroll = menu.GetNode<ScrollContainer>("%SettingsPage");
		Require(slider.MinValue == PetSettings.MinFontScale && slider.MaxValue == PetSettings.MaxFontScale && !slider.Scrollable,
			"Font size has a separate 75–150% wheel-safe slider.");
		foreach (var scale in new[] { PetSettings.MinFontScale, PetSettings.MaxFontScale, 1f, PetSettings.MaxFontScale })
		{
			settings.SetFontScale(scale); await Settle();
			Require(authoredText.GetThemeFontSize("font_size") == Mathf.RoundToInt(21 * scale)
				&& inherited.GetThemeFontSize("font_size") == Mathf.RoundToInt(inheritedSize * scale)
				&& value.GetThemeFontSize("font_size") == Mathf.RoundToInt(13 * scale),
				$"Font scaling uses authored and inherited text sizes without compounding at {scale:P0}.");
			Require(menu.Theme == theme && theme.DefaultFontSize == themeSize && menu.Size == size && menu.MinSize == minimum
				&& menu.Position == position && Mathf.IsEqualApprox(menu.ContentScaleFactor, 1) && Mathf.IsEqualApprox(settings.UiScale, 1),
				"Font size leaves shared themes, menu scale and native window geometry unchanged.");
			Require(value.Text == $"{Mathf.RoundToInt(scale * 100)}%", "Font size shows the saved percentage.");
		}
		var dynamicLabel = new Label { Text = "New runtime card", Visible = false };
		dynamicLabel.AddThemeFontSizeOverride("font_size", 17); menu.GetNode<Control>("%DogsPage").AddChild(dynamicLabel); await Settle();
		Require(dynamicLabel.GetThemeFontSize("font_size") == Mathf.RoundToInt(17 * PetSettings.MaxFontScale),
			"Controls created after a font change receive the same scale from their original size.");
		var popup = menu.GetNode<OptionButton>("%PetCounterPosition").GetPopup();
		var basePopup = LoadScene<StatusWindow>("res://StatusWindow.tscn"); basePopup.Visible = false; AddFixture(basePopup);
		Require(popup.GetThemeFontSize("font_size") == Mathf.RoundToInt(basePopup.GetNode<OptionButton>("%PetCounterPosition").GetPopup().GetThemeFontSize("font_size") * PetSettings.MaxFontScale),
			"Option popups use the chosen font size too.");
		basePopup.SetProcess(false);
		foreach (var tab in new[] { "%DogsTab", "%ItemsTab", "%ShopTab", "%SettingsTab" })
		{
			menu.GetNode<Button>(tab).EmitSignal(BaseButton.SignalName.Pressed); await Settle();
			Require(menu.GetVisibleRect().Grow(1).Encloses(menu.GetNode<Control>("Background").GetGlobalRect()) && menu.Size == size,
				$"The {tab} page fits the unchanged window with 150% fonts.");
		}
		menu.Size = minimum; settings.SetUiScale(PetSettings.MaxUiScale); await Settle();
		foreach (var tab in new[] { "%DogsTab", "%ItemsTab", "%ShopTab", "%SettingsTab" })
		{
			menu.GetNode<Button>(tab).EmitSignal(BaseButton.SignalName.Pressed); await Settle();
			if (tab == "%ShopTab" && !menu.GetVisibleRect().Grow(1).Encloses(menu.GetNode<Control>("Background").GetGlobalRect()))
			{
				await Capture(menu, "shop-overflow-diagnostic");
				foreach (var name in new[] { "Background", "%ShopPage", "%ShopScroll", "%ShopBoxGrid", "%DigBoxOdds", "%ShopBones" })
				{
					var control = menu.GetNode<Control>(name);
					GD.Print($"SHOP_LAYOUT_DIAGNOSTIC {name}: rect={control.GetGlobalRect()}; minimum={control.GetCombinedMinimumSize()}; viewport={menu.GetVisibleRect()}; native={menu.Size}");
				}
			}
			Require(menu.GetVisibleRect().Grow(1).Encloses(menu.GetNode<Control>("Background").GetGlobalRect())
				&& menu.Size == minimum && menu.MinSize == minimum && menu.Position == position,
				$"The {tab} page fits the minimum window at both 150% fonts and 175% menu size.");
			if (tab == "%ShopTab")
			{
				var shopScroll = menu.GetNode<ScrollContainer>("%ShopScroll");
				shopScroll.EnsureControlVisible(menu.GetNode<Button>("%RefreshInventory")); await Settle();
				await Capture(menu, "large-fonts-shop-minimum");
				Require(shopScroll.GetGlobalRect().Grow(1).Encloses(menu.GetNode<Button>("%RefreshInventory").GetGlobalRect()),
					$"Shop actions remain reachable by scrolling when both text and menu scale are maximized. Viewport {shopScroll.GetGlobalRect()}, button {menu.GetNode<Button>("%RefreshInventory").GetGlobalRect()}.");
			}
		}
		scroll.ScrollVertical = int.MaxValue; await Settle();
		Require(scroll.GetGlobalRect().Grow(1).Encloses(menu.GetNode<Button>("%QuitPetDaDog").GetGlobalRect()),
			"The bottom of Settings remains reachable when font size and menu size are both maximized.");
		await Capture(menu, "large-fonts-settings-minimum");
		settings.SetUiScale(1); menu.Size = size; await Settle();
		scroll.EnsureControlVisible(slider); await Settle();
		var visible = slider.GetGlobalRect().Intersection(scroll.GetGlobalRect());
		PushMouseButton(menu, visible.GetCenter(), MouseButton.WheelDown, true); await Settle();
		Require(Mathf.IsEqualApprox(settings.FontScale, PetSettings.MaxFontScale) && Mathf.IsEqualApprox(settings.UiScale, 1),
			"Wheeling over the font slider scrolls the page without changing either saved scale.");
		scroll.EnsureControlVisible(slider); await Settle();
		var click = slider.GetGlobalRect().Position + slider.Size * new Vector2(0.62f, 0.5f);
		PushMouseButton(menu, click, MouseButton.Left, true); PushMouseButton(menu, click, MouseButton.Left, false); await Settle();
		Require(settings.FontScale > 1 && settings.FontScale < 1.4f && Mathf.IsEqualApprox(settings.UiScale, 1)
			&& menu.Size == size && menu.Position == position, "Clicking the font slider changes text without changing menu scale or native geometry.");
		settings.SetFontScale(1.25f); settings.SetUiScale(0.85f); await Settle();
		var reloaded = new PetSettings(); AddFixture(reloaded);
		Require(Mathf.IsEqualApprox(reloaded.FontScale, 1.25f) && Mathf.IsEqualApprox(reloaded.UiScale, 0.85f),
			"Font size and menu size reload as independent preferences.");
		var wardrobe = new AccessoryWardrobe { StoragePath = $"{OutputDirectory}/font-scale-outfit.cfg", PresetStoragePath = $"{OutputDirectory}/font-scale-presets-{Guid.NewGuid():N}.cfg" };
		AddFixture(wardrobe); Require(wardrobe.SavePreset("Readable outfit"), "Font smoke creates a local preset fixture.");
		var browser = LoadScene<DogPresetsWindow>("res://DogPresetsWindow.tscn"); browser.Visible = false; AddFixture(browser); browser.Configure(wardrobe);
		var presetSize = browser.Size; var presetMin = browser.MinSize;
		var presetBase = browser.GetNode<LineEdit>("%PresetName").GetThemeFontSize("font_size");
		browser.ShowPresets(menu); await Settle();
		Require(browser.GetNode<LineEdit>("%PresetName").GetThemeFontSize("font_size") == Mathf.RoundToInt(presetBase * 1.25f)
			&& browser.Size == presetSize && browser.MinSize == presetMin && Mathf.IsEqualApprox(browser.ContentScaleFactor, 0.85f),
			"Preset windows apply independent font and menu scales while preserving native dimensions.");
		Require(wardrobe.SavePreset("Another readable outfit"), "A preset can be added after opening the scaled browser."); await Settle();
		Require(browser.GetNode<GridContainer>("%PresetGrid").GetChildren().All(card => card.GetNode<Label>("%PresetDetails").GetThemeFontSize("font_size") == 14),
			"New preset cards get 125% text exactly once.");
		browser.Hide(); settings.SetUiScale(1); settings.SetFontScale(1); await Settle();
		Require(authoredText.GetThemeFontSize("font_size") == 21 && value.GetThemeFontSize("font_size") == 13
			&& inherited.HasThemeFontSizeOverride("font_size") == inheritedOverride && dynamicLabel.GetThemeFontSize("font_size") == 17,
			"Returning to 100% restores authored overrides and inherited theme fonts exactly.");
		settings.SetFontScale(float.NaN); Require(settings.FontScale == 1, "An invalid font value cannot corrupt the saved preference.");
		settings.SetFontScale(10); Require(settings.FontScale == PetSettings.MaxFontScale, "Font size clamps to its supported maximum.");
		settings.SetFontScale(0); Require(settings.FontScale == PetSettings.MinFontScale, "Font size clamps to its supported minimum.");
		settings.SetFontScale(1); await Settle(); menu.Hide(); menu.SetProcess(false);
	}

	private static void PushMouseButton(Window window, Vector2 position, MouseButton button, bool pressed)
	{
		using var input = new InputEventMouseButton { Position = position, GlobalPosition = position, ButtonIndex = button, Pressed = pressed, Factor = 1 };
		window.PushInput(input, true);
	}

	private void AssertStatusOverrides(StatusWindow window, string stage)
	{
		Require(!window.TransparentBg && !window.Transparent, $"The authored menu's opaque viewport and native window survive {stage}.");
		Require(window.Theme.ResourceName == "AuthoredUiSmokeTheme" && window.Theme.DefaultFontSize == 17
			&& window.Theme.GetColor("font_color", "Label") == Colors.DarkSlateBlue, $"Authored theme survives {stage}.");
		Require(window.GetNode<VBoxContainer>("Background/Margin/Layout").GetThemeConstant("separation") == 17, $"Authored spacing survives {stage}.");
		var button = window.GetNode<Button>("%DogsTab");
		Require(button.Text == "My dogs" && button.CustomMinimumSize == new Vector2(102, 39), $"Authored button text and dimensions survive {stage}.");
		Require(button.GetThemeFontSize("font_size") == 21 && button.GetThemeFont("font").ResourceName == "AuthoredUiSmokeFont", $"Authored font overrides survive {stage}.");
		Require(button.GetThemeStylebox("normal") is StyleBoxFlat style && style.BgColor == Colors.MediumPurple,
			$"Authored StyleBox override survives {stage}.");
		Require(button.GetParent().Name == "BrandRow", $"Authored hierarchy changes survive {stage}.");
		Require(window.GetNode<CheckBox>("%Stationary").Text == "My resting preference"
			&& window.GetNode<CheckBox>("%BounceEnabled").Text == "My bounce preference"
			&& window.GetNode<CheckBox>("%FlipHorizontally").Text == "My mirror preference"
			&& window.StationaryPatrolText == "My dog is resting", $"Authored movement captions survive {stage}.");
		var traversal = window.GetNode<OptionButton>("%PatrolTraversalMode");
		Require(traversal.GetItemText(traversal.GetItemIndex((int)PatrolTraversalMode.Loop)) == "My loop"
			&& traversal.GetItemText(traversal.GetItemIndex((int)PatrolTraversalMode.BackAndForth)) == "My return walk",
			$"Authored traversal choices retain their captions and stable IDs {stage}.");
		Require(window.GetNode<Label>("%EmptyDogPresets").Text == "My saved companions will appear here"
			&& window.GetNode<Button>("%OpenDogPresets").TooltipText == "My preset-management explanation",
			$"Authored saved-dog guidance and management tooltip survive {stage}.");
		Require(window.GetNode<GridContainer>("%DogPresetGrid").GetThemeConstant("h_separation") == 19
			&& window.GetNode<GridContainer>("%DogPresetGrid").GetThemeConstant("v_separation") == 21
			&& window.GetNode<GridContainer>("%DogGrid").GetThemeConstant("h_separation") == 23,
			$"Authored saved-outfit and owned-dog grid spacing survives {stage}.");
	}

	private void VerifyDogCollectionStructure(StatusWindow window)
	{
		var presets = window.GetNode<GridContainer>("%DogPresetGrid"); var dogs = window.GetNode<GridContainer>("%DogGrid");
		var scroll = Ancestor<ScrollContainer>(presets);
		Require(scroll != null && Ancestor<ScrollContainer>(dogs) == scroll && scroll.HorizontalScrollMode == ScrollContainer.ScrollMode.ShowNever,
			"The authored Dogs page places saved outfits and owned dogs in one vertical scroll container.");
		Require(window.GetNodeOrNull<OptionButton>("%DogChoices") == null && window.GetNodeOrNull<TextureRect>("%DogPortrait") == null,
			"Dogs selection is authored as preview-card collections rather than the previous portrait and dropdown.");
		Require(window.GetNode<Label>("%EmptyDogPresets") != null && window.GetNode<Label>("%DogPresetStatus") != null,
			"The embedded saved-outfit collection exposes authored empty guidance and result text.");
	}
	private static T? Ancestor<T>(Node node) where T : Node
	{
		for (var parent = node.GetParent(); parent != null; parent = parent.GetParent()) if (parent is T result) return result;
		return null;
	}

	private async Task VerifyMovementSettings(PetSettings settings, PatrolRoute route)
	{
		var originalStationary = settings.Stationary;
		var originalBounce = settings.BounceEnabled;
		var originalFlip = settings.FlipHorizontally;
		try
		{
			settings.SetStationary(false); settings.SetBounceEnabled(true); settings.SetFlipHorizontally(false);
			using (var legacy = new ConfigFile())
			{
				Require(legacy.Load("user://pet_settings.cfg") == Error.Ok, "The isolated settings fixture loads for legacy preference coverage.");
				if (legacy.HasSectionKey("dog", "flip_horizontally")) legacy.EraseSectionKey("dog", "flip_horizontally");
				Require(legacy.Save("user://pet_settings.cfg") == Error.Ok, "A legacy settings fixture without a mirror preference saves.");
			}
			var legacySettings = new PetSettings(); AddFixture(legacySettings);
			Require(!legacySettings.FlipHorizontally, "Existing settings without a mirror preference preserve the original unflipped appearance.");
			route.ClearRoute(); route.SetTraversalMode(PatrolTraversalMode.Loop);
			var status = LoadScene<StatusWindow>("res://StatusWindow.tscn");
			status.Visible = false; status.InitialTab = 3;
			AddFixture(status); status.Configure(settings);
			var stationary = status.GetNode<CheckBox>("%Stationary");
			var bounce = status.GetNode<CheckBox>("%BounceEnabled");
			var flip = status.GetNode<CheckBox>("%FlipHorizontally");
			var traversal = status.GetNode<OptionButton>("%PatrolTraversalMode");
			Require(!stationary.ButtonPressed && bounce.ButtonPressed && !flip.ButtonPressed, "Movement controls reflect the existing walk, bounce and unflipped defaults.");
			stationary.ButtonPressed = true; bounce.ButtonPressed = false; flip.ButtonPressed = true;
			Require(settings.Stationary && !settings.BounceEnabled && settings.FlipHorizontally, "Movement and mirror controls update independent saved preferences.");
			Require(status.GetNode<Label>("%PatrolRouteSummary").Text == status.StationaryPatrolText,
				"Without a route the status explains that automatic walking is paused.");
			var reloadedSettings = new PetSettings(); AddFixture(reloadedSettings);
			Require(reloadedSettings.Stationary && !reloadedSettings.BounceEnabled && reloadedSettings.FlipHorizontally, "Stationary, bounce and mirror preferences survive a settings reload.");
			settings.SetFlipHorizontally(false);
			Require(!flip.ButtonPressed && stationary.ButtonPressed && !bounce.ButtonPressed,
				"Changing the mirror model refreshes its checkbox while preserving independent movement preferences.");
			Require(route.BeginEdit(), "The movement smoke can start an isolated route edit.");
			route.AddPoint(new Vector2(0.2f, 0.4f)); route.AddPoint(new Vector2(0.5f, 0.6f)); route.AddPoint(new Vector2(0.8f, 0.4f));
			Require(route.FinishEdit(), "The movement smoke creates three distinct stops.");
			var backAndForthIndex = traversal.GetItemIndex((int)PatrolTraversalMode.BackAndForth);
			traversal.Select(backAndForthIndex); traversal.EmitSignal(OptionButton.SignalName.ItemSelected, backAndForthIndex);
			Require(route.TraversalMode == PatrolTraversalMode.BackAndForth
				&& status.GetNode<Label>("%PatrolRouteSummary").Text.Contains("1 → 2 → 3 → 2 → 1", StringComparison.Ordinal),
				"Back and forth saves the selected mode and shows the actual reverse visit order.");
			var reloadedRoute = new PatrolRoute { StoragePath = route.StoragePath }; AddFixture(reloadedRoute);
			Require(reloadedRoute.TraversalMode == PatrolTraversalMode.BackAndForth && reloadedRoute.Points.Count == 3,
				"The movement mode and route reload together.");
			stationary.ButtonPressed = false;
			Require(route.Enabled && route.Points.Count == 3 && status.GetNode<Label>("%PatrolRouteSummary").Text.Contains(status.PatrolEnabledText, StringComparison.Ordinal),
				"Resuming walking keeps the saved patrol enabled and restores its active summary.");
			route.SetTraversalMode(PatrolTraversalMode.Loop);
			Require(traversal.GetSelectedId() == (int)PatrolTraversalMode.Loop
				&& status.GetNode<Label>("%PatrolRouteSummary").Text.Contains("1 → 2 → 3 → 1", StringComparison.Ordinal),
				"A model change refreshes the dropdown and loop summary without replaying a UI action.");
			await Settle(); status.Hide(); status.SetProcess(false);
		}
		finally
		{
			if (route.IsEditing) route.CancelEdit();
			route.ClearRoute(); route.SetTraversalMode(PatrolTraversalMode.Loop);
			settings.SetStationary(originalStationary); settings.SetBounceEnabled(originalBounce); settings.SetFlipHorizontally(originalFlip);
		}
	}

	private async Task VerifyCounterAndMenuSettings(PetSettings settings)
	{
		var originalCounter = settings.ShowPetCounter; var originalPosition = settings.PetCounterPosition;
		var originalRightClick = settings.OpenMenuOnRightClick;
		var originalKey = settings.MenuHotkeyVirtualKey; var originalModifiers = settings.MenuHotkeyModifiers;
		StatusWindow? menu = null;
		try
		{
			using (var legacy = new ConfigFile())
			{
				Require(legacy.Load("user://pet_settings.cfg") == Error.Ok, "The isolated counter/menu legacy settings fixture loads.");
				foreach (var key in new[] { "show_pet_counter", "pet_counter_position", "open_menu_on_right_click", "menu_hotkey_virtual_key", "menu_hotkey_modifiers" })
					if (legacy.HasSectionKey("dog", key)) legacy.EraseSectionKey("dog", key);
				Require(legacy.Save("user://pet_settings.cfg") == Error.Ok, "Legacy preferences without counter or menu-shortcut keys save.");
			}
			var legacySettings = new PetSettings(); AddFixture(legacySettings);
			Require(!legacySettings.ShowPetCounter && legacySettings.PetCounterPosition == PetCounterPlacement.DogFeet
				&& legacySettings.OpenMenuOnRightClick && legacySettings.MenuHotkeyVirtualKey == 0x44 && legacySettings.MenuHotkeyModifiers == 3,
				"Legacy preferences retain right-click menus, hide the optional counter and default the global menu shortcut to Ctrl+Alt+D.");
			settings.SetShowPetCounter(false); settings.SetPetCounterPosition(PetCounterPlacement.DogFeet);
			settings.SetOpenMenuOnRightClick(true); settings.SetMenuHotkey(0x44, 3);
			menu = LoadScene<StatusWindow>("res://StatusWindow.tscn"); menu.Visible = false; menu.InitialTab = 3;
			AddFixture(menu); menu.Configure(settings); menu.ShowStatusWindow(); await Settle();
			var show = menu.GetNode<CheckBox>("%ShowPetCounter");
			var position = menu.GetNode<OptionButton>("%PetCounterPosition");
			var rightClick = menu.GetNode<CheckBox>("%OpenMenuOnRightClick");
			var record = menu.GetNode<Button>("%RecordMenuHotkey"); var reset = menu.GetNode<Button>("%ResetMenuHotkey");
			Require(!show.ButtonPressed && position.GetSelectedId() == 0 && rightClick.ButtonPressed && position.ItemCount == 5,
				"Settings initially shows the optional counter disabled, its five available placements and enabled right-click menu.");
			show.ButtonPressed = true;
			var corner = position.GetItemIndex((int)PetCounterPlacement.BottomRight);
			position.Select(corner); position.EmitSignal(OptionButton.SignalName.ItemSelected, corner);
			rightClick.ButtonPressed = false;
			Require(settings.ShowPetCounter && settings.PetCounterPosition == PetCounterPlacement.BottomRight && !settings.OpenMenuOnRightClick,
				"Counter visibility, corner placement and dog right-click preferences update through the authored controls.");
			var loaded = new PetSettings(); AddFixture(loaded);
			Require(loaded.ShowPetCounter && loaded.PetCounterPosition == PetCounterPlacement.BottomRight && !loaded.OpenMenuOnRightClick,
				"Counter and right-click menu preferences survive a new settings load.");
			record.EmitSignal(BaseButton.SignalName.Pressed);
			Require(GetField<bool>(menu, "_recordingMenuHotkey"), "The global menu shortcut button starts recording its next key combination.");
			menu._Input(new InputEventKey { Pressed = true, Echo = true, Keycode = Key.M, CtrlPressed = true });
			menu._Input(new InputEventKey { Pressed = false, Keycode = Key.M, CtrlPressed = true });
			Require(GetField<bool>(menu, "_recordingMenuHotkey") && settings.MenuHotkeyVirtualKey == 0x44,
				"Held-key repeats and key releases cannot become a newly recorded global shortcut.");
			menu._Input(new InputEventKey { Pressed = true, Keycode = Key.Ctrl, CtrlPressed = true });
			menu._Input(new InputEventKey { Pressed = true, Keycode = Key.Q });
			Require(GetField<bool>(menu, "_recordingMenuHotkey") && settings.MenuHotkeyVirtualKey == 0x44 && settings.MenuHotkeyModifiers == 3,
				"Modifier-only and unmodified typing cannot replace the global menu shortcut while recording.");
			menu._Input(new InputEventKey { Pressed = true, Keycode = Key.Escape });
			Require(!GetField<bool>(menu, "_recordingMenuHotkey") && settings.MenuHotkeyVirtualKey == 0x44 && settings.MenuHotkeyModifiers == 3,
				"Escape cancels shortcut recording and retains the previous saved combination.");
			record.EmitSignal(BaseButton.SignalName.Pressed);
			menu._Input(new InputEventKey { Pressed = true, Keycode = Key.M, CtrlPressed = true, ShiftPressed = true });
			Require(!GetField<bool>(menu, "_recordingMenuHotkey") && settings.MenuHotkeyVirtualKey == 0x4D
				&& settings.MenuHotkeyModifiers == (MenuShortcut.Control | MenuShortcut.Shift),
				"A valid modified key combination finishes recording and saves the global menu shortcut.");
			loaded = new PetSettings(); AddFixture(loaded);
			Require(loaded.MenuHotkeyVirtualKey == 0x4D && loaded.MenuHotkeyModifiers == (MenuShortcut.Control | MenuShortcut.Shift),
				"The recorded global shortcut survives a new settings load.");
			settings.SetMenuHotkey(0xC0, MenuShortcut.Alt);
			Require(settings.MenuHotkeyVirtualKey == 0x4D && settings.MenuHotkeyModifiers == (MenuShortcut.Control | MenuShortcut.Shift),
				"The reserved click-through shortcut cannot replace the independent menu shortcut.");
			var bridge = GetNode<NativeWindowBridge>("/root/NativeWindowBridge");
			var statusField = typeof(NativeWindowBridge).GetField("_menuHotkeyStatus", PrivateInstance)!;
			var originalStatus = statusField.GetValue(bridge);
			try
			{
				statusField.SetValue(bridge, "Fixture shortcut conflict: another application is using this combination.");
				menu._Process(0);
				Require(menu.GetNode<Label>("%MenuHotkeyStatus").Text.Contains("Fixture shortcut conflict", StringComparison.Ordinal)
					&& settings.MenuHotkeyVirtualKey == 0x4D && settings.MenuHotkeyModifiers == (MenuShortcut.Control | MenuShortcut.Shift),
					"A native shortcut conflict is visible in Settings while retaining the requested combination for correction or reset.");
			}
			finally { statusField.SetValue(bridge, originalStatus); }
			reset.EmitSignal(BaseButton.SignalName.Pressed);
			Require(settings.MenuHotkeyVirtualKey == 0x44 && settings.MenuHotkeyModifiers == 3,
				"Reset restores the default Ctrl+Alt+D global menu shortcut.");
			record.EmitSignal(BaseButton.SignalName.Pressed); menu.Hide(); menu._Process(0);
			Require(!GetField<bool>(menu, "_recordingMenuHotkey"), "Hiding Settings cancels shortcut recording instead of capturing later background typing.");
			settings.SetPetCounterPosition((PetCounterPlacement)99);
			Require(settings.PetCounterPosition == PetCounterPlacement.BottomRight, "Invalid counter placement IDs cannot overwrite a valid saved corner.");
		}
		finally
		{
			menu?.Hide(); menu?.SetProcess(false);
			settings.SetShowPetCounter(originalCounter); settings.SetPetCounterPosition(originalPosition);
			settings.SetOpenMenuOnRightClick(originalRightClick); settings.SetMenuHotkey(originalKey, originalModifiers);
		}
	}

	private async Task VerifyAccessoryTemplateOverrides(AccessoryWardrobe wardrobe)
	{
		wardrobe.Clear();
		var editor = LoadScene<AccessoryEditor>("res://UI/AccessoryEditor.tscn");
		editor.Theme = (Theme)ResourceLoader.Load<Theme>("res://UI/Theme/DefaultTheme.tres").Duplicate(true);
		editor.Theme.ResourceName = "LocalInspectorTheme";
		editor.Theme.SetColor("font_color", "Label", Colors.DarkMagenta);
		editor.GetNode<VBoxContainer>("%AccessoryInspector").AddThemeConstantOverride("separation", 16);
		var remove = editor.GetNode<Button>("%RemoveAccessory");
		remove.Text = "Take this off"; remove.AddThemeFontSizeOverride("font_size", 18);
		var card = editor.CardScene!.Instantiate<AccessoryCard>();
		card.AvailableStatusText = "Choose my item"; card.EquippedStatusText = "Wearing my item";
		card.AdditionalCopyStatusText = "Place another of my items";
		card.ItemPlacementStatusText = "Place my item"; card.TextPlacementStatusText = "Place my words";
		card.ChooseTooltipFormat = "Custom choice: {name} in {category}";
		card.CustomMinimumSize = new Vector2(148, 126);
		card.GetNode<Label>("%CardName").AddThemeFontSizeOverride("font_size", 19);
		card.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Colors.LightPink });
		editor.CardScene = PackTemplate(card);
		var row = editor.CategoryRowScene!.Instantiate<AccessoryCategoryRow>();
		row.DisplayTitle = "My collection"; row.HeaderFormat = "{title}: {category} ({count})";
		row.AddThemeConstantOverride("separation", 15);
		row.GetNode<ScrollContainer>("%CategoryScroll").CustomMinimumSize = new Vector2(0, 147);
		editor.CategoryRowScene = PackTemplate(row);
		var shelves = editor.GetNode<VBoxContainer>("%AccessoryShelves");
		foreach (var child in shelves.GetChildren().OfType<AccessoryCategoryRow>().ToArray()) { shelves.RemoveChild(child); child.Free(); }
		var colors = editor.ColorControlsScene!.Instantiate<AccessoryColorControls>();
		colors.GetNode<Button>("%DoneColor").Text = "Keep my color";
		colors.GetNode<HBoxContainer>("%QuickColors").AddThemeConstantOverride("separation", 9);
		editor.ColorControlsScene = PackTemplate(colors);
		var host = new Window { Visible = false, Size = new Vector2I(760, 740), Theme = ResourceLoader.Load<Theme>("res://UI/Theme/DefaultTheme.tres") };
		AddFixture(host); host.AddChild(editor);
		await Settle();
		var cards = GetField<Dictionary<string, Button>>(editor, "_cards");
		Require(cards.Count == wardrobe.Catalog.Count && cards.Values.All(value => value.CustomMinimumSize == new Vector2(148, 126)),
			"Live catalog population keeps the exported card template's authored dimensions.");
		Require(cards.Values.All(value => value.GetNode<Label>("%CardName").GetThemeFontSize("font_size") == 19
			&& value.GetThemeStylebox("normal") is StyleBoxFlat style && style.BgColor == Colors.LightPink),
			"Live cards preserve exported template fonts and StyleBoxes.");
		Require(cards.All(pair => pair.Value.GetNode<Label>("%CardStatus").Text == (wardrobe.CanEquip(pair.Key) ? "Choose my item" : "Unlock from a box")
			&& pair.Value.TooltipText.StartsWith("Custom choice: ", StringComparison.Ordinal)),
			"Catalog refresh preserves exported card state captions and tooltips.");
		var rows = GetField<Dictionary<string, AccessoryCategoryRow>>(editor, "_categoryRows");
		Require(rows.Count == AccessoryCategories.OrderedNames.Length && rows.Values.All(value => value.GetThemeConstant("separation") == 15
			&& value.Scroll.CustomMinimumSize == new Vector2(0, 147)), "Generated shelves preserve their exported category template layout.");
		Require(rows.All(pair => pair.Value.Category == pair.Key && pair.Value.HeaderButton.Text.StartsWith($"My collection: {pair.Key} (", StringComparison.Ordinal)),
			"Authored category titles and formats survive while catalog identities still populate the correct shelves.");
		var colorControls = Descendants(editor.GetNode<ColorPickerButton>("%AccessoryColorPicker").GetPicker()).OfType<AccessoryColorControls>().Single();
		Require(editor.GetNode<ColorPickerButton>("%AccessoryColorPicker").GetPopup().Theme == editor.Theme,
			"The color popup inherits the nearest explicitly authored inspector theme.");
		Require(colorControls.GetNode<Button>("%DoneColor").Text == "Keep my color"
			&& colorControls.GetNode<HBoxContainer>("%QuickColors").GetThemeConstant("separation") == 9,
			"Color popup helpers preserve the exported control template's text and spacing.");
		var session = GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession"); session.SetActive(true);
		session.ChooseAccessory(AccessoryWardrobe.TextAccessoryId);
		Require(cards[AccessoryWardrobe.TextAccessoryId].GetNode<Label>("%CardStatus").Text == "Place my words",
			"Placement state uses the authored text-card caption.");
		wardrobe.Equip(wardrobe.Catalog[0].Id, new Vector2(0.5f, 0.5f)); session.SelectAccessory(wardrobe.Catalog[0].Id);
		Require(cards[wardrobe.Catalog[0].Id].GetNode<Label>("%CardStatus").Text == "Place another of my items",
			"A repeatable equipped accessory uses the authored additional-copy caption.");
		Require(remove.Text == "Take this off" && remove.GetThemeFontSize("font_size") == 18
			&& editor.GetNode<VBoxContainer>("%AccessoryInspector").GetThemeConstant("separation") == 16,
			"Model-driven inspector refresh preserves authored static text, font and spacing.");
		session.SetActive(false); host.Hide();
	}
	private PackedScene PackTemplate(Node node)
	{
		var scene = new PackedScene();
		Require(scene.Pack(node) == Error.Ok, "An edited reusable template packs successfully.");
		node.Free(); return scene;
	}

	private void VerifyDesktopAppearanceOverrides()
	{
		var look = (DesktopAppearance)DesktopAppearance.Default.Duplicate(true);
		look.ResourceName = "DesignerDesktopAppearance";
		look.HandleRadius = 23; look.HandleBorderWidth = 4;
		look.MoveDogSize = new Vector2(137, 34); look.DogResizeOffset = new Vector2(31, 27);
		look.MoveDogText = "Move my companion";
		look.MoveDogStyle = new StyleBoxFlat { BgColor = Colors.Teal, CornerRadiusTopLeft = 9 };
		look.MarkerRadius = 29; look.MarkerFontSize = 25; look.RouteLineWidth = 7;
		look.TextBubbleSize = new Vector2(240, 96); look.TextFontSize = 29;
		look.TextFont = new SystemFont { FontNames = new[] { "Segoe UI" }, FontWeight = 700 };
		look.TextBubbleStyle = new StyleBoxFlat { BgColor = Colors.Coral, BorderWidthBottom = 3 };
		Require(ResourceSaver.Save(look, _editedAppearancePath) == Error.Ok, "Desktop appearance edits save as an authored resource.");
		var restored = ResourceLoader.Load<DesktopAppearance>(_editedAppearancePath, "", ResourceLoader.CacheMode.Ignore);
		Require(restored.ResourceName == "DesignerDesktopAppearance" && restored.HandleRadius == 23 && restored.HandleBorderWidth == 4
			&& restored.MarkerRadius == 29 && restored.MarkerFontSize == 25 && restored.RouteLineWidth == 7,
			"Saved handle, patrol marker and route styling survives resource reload.");
		var controls = LoadScene<DesktopAccessoryControls>("res://UI/DesktopAccessoryControls.tscn");
		var patrol = LoadScene<DesktopPatrolEditor>("res://UI/DesktopPatrolEditor.tscn");
		var text = LoadScene<PetTextAccessory>("res://UI/PetTextAccessory.tscn");
		controls.Appearance = restored; patrol.Appearance = restored; text.Appearance = restored; text.Visible = false;
		AddFixture(controls); AddFixture(patrol); AddFixture(text);
		Require(controls.Appearance == restored && patrol.Appearance == restored && text.Appearance == restored,
			"Runtime desktop templates retain their selected appearance resource.");
		Require(controls.GetDogMoveRect().Size == new Vector2(137, 34) && controls.GetDogResizeHandle() == new Vector2(31, 27),
			"Desktop handle geometry uses authored move-button dimensions and resize offsets.");
		Require(controls.Appearance.MoveDogText == "Move my companion" && controls.Appearance.MoveDogStyle is StyleBoxFlat moveStyle
			&& moveStyle.BgColor == Colors.Teal && moveStyle.CornerRadiusTopLeft == 9,
			"Desktop move control retains the authored caption and StyleBox.");
		Require(text.BubbleSize == new Vector2(240, 96) && text.Appearance.TextFontSize == 29
			&& text.Appearance.TextFont is SystemFont font && font.FontWeight == 700
			&& text.Appearance.TextBubbleStyle is StyleBoxFlat bubbleStyle && bubbleStyle.BgColor == Colors.Coral && bubbleStyle.BorderWidthBottom == 3,
			"Text bubbles use the authored dimensions, font and frame resource.");
	}

	private async Task VerifyFloatingTemplates(AccessoryWardrobe wardrobe, PatrolRoute route)
	{
		var toolbar = LoadScene<PatrolRouteToolbar>("res://UI/PatrolRouteToolbar.tscn");
		toolbar.Visible = false; toolbar.Size = new Vector2I(650, 195);
		toolbar.LoopDraftFormat = "My loop: {loop} ({count} stops)";
		var done = toolbar.GetNode<Button>("%PatrolDone");
		done.Text = "Start my walk"; done.AddThemeFontSizeOverride("font_size", 19);
		var toolbarPanel = toolbar.GetNode<PanelContainer>("Background");
		toolbarPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Colors.PaleGoldenrod });
		toolbar = SaveReloadFloatingTemplate(toolbar);
		toolbar.ForceNative = true;
		done = toolbar.GetNode<Button>("%PatrolDone"); toolbarPanel = toolbar.GetNode<PanelContainer>("Background");
		AddFixture(toolbar); toolbar.Configure(route);
		route.BeginEdit(); route.ClearDraft(); route.AddPoint(new Vector2(0.2f, 0.4f)); route.AddPoint(new Vector2(0.7f, 0.6f));
		await Settle();
		Require(toolbar.Visible && toolbar.Size == new Vector2I(650, 195), "The toolbar preserves its authored size while opening.");
		Require(!toolbar.TransparentBg && !toolbar.Transparent, "The shown toolbar retains its authored viewport and native-window opacity.");
		Require(done.Text == "Start my walk" && done.GetThemeFontSize("font_size") == 19
			&& toolbarPanel.GetThemeStylebox("panel") is StyleBoxFlat style && style.BgColor == Colors.PaleGoldenrod,
			"Toolbar controls preserve designer text, font and frame overrides.");
		Require(toolbar.GetNode<Label>("%PatrolDraftSummary").Text == "My loop: 1 → 2 → 1 (2 stops)",
			"Toolbar dynamic text uses its exported format.");
		await Capture(toolbar, "authored-toolbar"); route.CancelEdit();
		var session = GetNode<AccessoryEditingSession>("/root/AccessoryEditingSession"); session.SetActive(true);
		var popup = LoadScene<AccessoryLayerMenu>("res://UI/AccessoryLayerMenu.tscn");
		popup.Visible = false; popup.MinSize = new Vector2I(450, 190); popup.Size = popup.MinSize; popup.FitToContents = false;
		popup.DogCaption = "My desktop dog"; popup.CaptionFormat = "Layer: {name}";
		popup.SetItemText(popup.GetItemIndex(1), "Raise this layer");
		popup.AddThemeFontSizeOverride("font_size", 18);
		popup = SaveReloadFloatingTemplate(popup); popup.ForceNative = true;
		AddFixture(popup); popup.Configure(wardrobe, session);
		popup.ShowFor(AccessoryWardrobe.DogLayerId, new Vector2I(120, 140)); await Settle();
		Require(popup.Visible && popup.Size == new Vector2I(450, 190), $"The layer popup preserves authored dimensions with FitToContents off (visible={popup.Visible}, size={popup.Size}, active={session.Active}).");
		Require(!popup.TransparentBg && !popup.Transparent, "The shown layer menu retains its authored viewport and native-window opacity.");
		Require(popup.GetItemText(popup.GetItemIndex(1)) == "Raise this layer" && popup.GetThemeFontSize("font_size") == 18,
			"The layer popup keeps authored option labels and fonts.");
		Require(popup.GetItemText(0) == "Layer: My desktop dog", "The layer popup uses its exported target caption.");
		await Capture(popup, "authored-layer-menu"); popup.HideMenu(); session.SetActive(false);
	}

	private T SaveReloadFloatingTemplate<T>(T window) where T : Window
	{
		Require(!window.TransparentBg && !window.Transparent, $"The authored {window.Name} template has an opaque viewport and native window.");
		var path = $"{OutputDirectory}/edited-{window.Name}-{Guid.NewGuid():N}.tscn";
		_editedFloatingScenePaths.Add(path);
		var packed = new PackedScene();
		Require(packed.Pack(window) == Error.Ok && ResourceSaver.Save(packed, path) == Error.Ok,
			$"The edited {window.Name} template serializes to a scene file.");
		window.Free();
		var restored = ResourceLoader.Load<PackedScene>(path, "", ResourceLoader.CacheMode.Ignore).Instantiate<T>();
		Require(!restored.TransparentBg && !restored.Transparent, $"The {restored.Name} viewport and native-window opacity survive save/reload.");
		return restored;
	}

	private async Task VerifyTextTemplateOnDesktop(AccessoryWardrobe wardrobe)
	{
		wardrobe.Clear(); wardrobe.Equip(AccessoryWardrobe.TextAccessoryId, new Vector2(1.1f, -0.2f));
		wardrobe.SetTransform(AccessoryWardrobe.TextAccessoryId, 1.3f, 32);
		var template = LoadScene<PetTextAccessory>("res://UI/PetTextAccessory.tscn");
		template.Appearance = ResourceLoader.Load<DesktopAppearance>(_editedAppearancePath, "", ResourceLoader.CacheMode.Ignore);
		var pet = LoadScene<DesktopPet>("res://Main.tscn");
		pet.TextAccessoryScene = PackTemplate(template);
		AddFixture(pet); await Settle();
		var text = pet.AccessoryNode(AccessoryWardrobe.TextAccessoryId) as PetTextAccessory;
		Require(text != null && text.BubbleSize == new Vector2(240, 96), "The desktop instantiates the exported text template with its custom bubble size.");
		var expectedSize = wardrobe.Find(AccessoryWardrobe.TextAccessoryId)!.Size * pet.EditableDogRect.Size.Y * 1.3f;
		Require((text!.BubbleSize * text.Scale.Abs()).DistanceTo(expectedSize) < 0.01f,
			"A custom text-template canvas keeps the equipped accessory's intended display size.");
		var half = text.BubbleSize * 0.5f;
		var corners = new[] { -half, new Vector2(half.X, -half.Y), half, new Vector2(-half.X, half.Y) };
		var bounds = pet.EditableOutfitBounds.Grow(0.1f);
		Require(corners.All(corner => bounds.HasPoint(text.ToGlobal(corner))),
			"Outfit bounds contain every rotated custom-template text corner.");
		var viewport = pet.GetViewportRect().Grow(1);
		Require(viewport.Encloses(pet.EditableOutfitBounds), "The native overlay fits the complete custom-template text appearance.");
		await Capture(GetWindow(), "authored-desktop-text");
	}

	private static IEnumerable<string> ScenePaths(string path)
	{
		using var directory = DirAccess.Open(path);
		if (directory == null) yield break;
		foreach (var file in directory.GetFiles()) if (file.EndsWith(".tscn", StringComparison.Ordinal)) yield return path + "/" + file;
		foreach (var child in directory.GetDirectories()) foreach (var scene in ScenePaths(path + "/" + child)) yield return scene;
	}
	private static IEnumerable<Node> Descendants(Node node)
	{
		foreach (var child in node.GetChildren()) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
	}
	private static T LoadScene<T>(string path) where T : Node => ResourceLoader.Load<PackedScene>(path).Instantiate<T>();
	private void AddFixture(Node node) { _fixtures.Add(node); AddChild(node); }
	private void FreeFixtures()
	{
		foreach (var node in _fixtures.AsEnumerable().Reverse()) if (GodotObject.IsInstanceValid(node)) node.Free();
		_fixtures.Clear();
	}
	private async Task Settle() { for (var i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
	private async Task Capture(Window window, string name)
	{
		if (DisplayServer.GetName() == "headless") return;
		await Settle(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		using var image = window.GetTexture().GetImage();
		Require(!image.IsEmpty() && image.SavePng($"{OutputDirectory}/{name}.png") == Error.Ok, $"The authored {name} viewport captures for review.");
	}
	private static T GetField<T>(object target, string name)
	{
		var field = target.GetType().GetField(name, PrivateInstance) ?? throw new MissingFieldException(target.GetType().Name, name);
		return (T)field.GetValue(target)!;
	}
	private void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
		_assertions++;
	}
}
