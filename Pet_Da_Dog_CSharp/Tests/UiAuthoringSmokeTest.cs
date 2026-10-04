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
			var settings = GetNode<PetSettings>("/root/PetSettings"); settings.SetUiScale(1);
			await VerifySavedSceneOverrides(settings);
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
		authored.PetsTotalFormat = "Confirmed: {count}"; authored.PendingClickFormat = "Pending one: {count}";
		authored.PendingClicksFormat = "Pending several: {count}"; authored.ServiceStatusFormat = "My service: {status}";
		authored.ConnectedText = "Everything connected"; authored.NoPatrolRouteText = "Ready to draw a route";
		authored.GetNode<Label>("%PendingPets").TooltipText = "My pending-click explanation";
		authored.GetNode<Label>("%PatrolRouteSaveStatus").Text = "My save-failure explanation";
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
		reloaded.UpdateStatus(null, 1, "Connecting", "Offline");
		Require(reloaded.GetNode<Label>("%PendingPets").TooltipText == "My pending-click explanation"
			&& reloaded.GetNode<Label>("%PatrolRouteSaveStatus").Text == "My save-failure explanation",
			"Live status refresh preserves authored static tooltips and save explanations.");
		Require(reloaded.GetNode<Button>("%EditPatrolRoute").Text == "Draw my route", "Route captions use their Inspector-exported text.");
		reloaded.UpdateStatus(1234, 2, "Ready", "Ready", true, true);
		Require(reloaded.GetNode<Label>("%PetsValue").Text == "Confirmed: 1,234"
			&& reloaded.GetNode<Label>("%PendingPets").Text == "Pending several: 2"
			&& reloaded.GetNode<Label>("%BackendStatus").Text == "My service: Ready"
			&& reloaded.GetNode<Label>("%ConnectionSummary").Text == "Everything connected"
			&& reloaded.GetNode<Label>("%PatrolRouteSummary").Text == "Ready to draw a route",
			"Saved live-copy formats substitute data while preserving Inspector-authored presentation.");
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
		Require(Mathf.IsEqualApprox(reloaded.ContentScaleFactor, 0.825f) && reloaded.Size == new Vector2I(516, 548),
			"Menu size scales the authored dimensions and content without rebuilding their theme.");
		settings.SetUiScale(1); settings.SetDogTransparency(originalVisibility); await Settle();
		Require(reloaded.Size == new Vector2I(688, 730) && Mathf.IsEqualApprox(reloaded.ContentScaleFactor, 1.1f),
			"Returning menu scale to 100% restores the authored base size and content factor.");
		reloaded.GetNode<Button>("%DogsTab").EmitSignal(BaseButton.SignalName.Pressed);
		Require(reloaded.GetNode<Control>("%DogsPage").Visible, "The reparented unique-name tab still runs its bound action.");
		reloaded.GetNode<Button>("%SettingsTab").EmitSignal(BaseButton.SignalName.Pressed);
		await Capture(reloaded, "authored-settings");
		reloaded.GetNode<Button>("%ItemsTab").EmitSignal(BaseButton.SignalName.Pressed);
		await Capture(reloaded, "authored-items");
		reloaded.Hide(); reloaded.SetProcess(false);
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
		Require(cards.Values.All(value => value.GetNode<Label>("%CardStatus").Text == "Choose my item"
			&& value.TooltipText.StartsWith("Custom choice: ", StringComparison.Ordinal)),
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
		Require(cards[wardrobe.Catalog[0].Id].GetNode<Label>("%CardStatus").Text == "Wearing my item",
			"Equipped state uses the authored card caption.");
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
