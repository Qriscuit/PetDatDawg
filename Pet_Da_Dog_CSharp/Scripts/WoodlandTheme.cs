using Godot;
using System.Collections.Generic;

// Shared UI presentation only. Dog/accessory textures and pet grant logic stay separate.
public static class WoodlandTheme
{
	private const string ArtPath = "res://Art/UI/Woodland";
	private static readonly Dictionary<string, Texture2D> Textures = new();
	public static readonly Color Text = Color.FromHtml("#293c28");
	public static readonly Color Muted = Color.FromHtml("#5f654c");
	public static readonly Color Accent = Color.FromHtml("#a45031");
	public static readonly Color Border = Color.FromHtml("#a28954");
	public static readonly Color Parchment = Color.FromHtml("#efe0bd");
	public static readonly Color Moss = Color.FromHtml("#486038");
	private static readonly Color Cream = Color.FromHtml("#fff1d0");

	public static Theme Build(float scale = 1)
	{
		var theme = new Theme
		{
			DefaultBaseScale = scale,
			DefaultFontSize = Mathf.RoundToInt(14 * scale),
			DefaultFont = new SystemFont { FontNames = new[] { "Segoe UI", "Noto Sans" } }
		};
		theme.SetColor("font_color", "Label", Text);
		theme.SetColor("font_shadow_color", "Label", new Color(0, 0, 0, 0));
		theme.SetFont("font", "WoodlandHeading", new SystemFont { FontNames = new[] { "Georgia", "Noto Serif" }, FontWeight = 700 });
		theme.SetTypeVariation("WoodlandHeading", "Label");
		theme.SetFontSize("font_size", "WoodlandHeading", Mathf.RoundToInt(19 * scale));
		ButtonStyles(theme, "Button", scale);
		foreach (var variation in new[] { "SecondaryButton", "ActionButton", "TabButton", "CardButton", "ColorPickerModeButton" })
		{
			theme.SetTypeVariation(variation, "Button");
			ButtonStyles(theme, variation, scale);
		}
		foreach (var type in new[] { "Panel", "PanelContainer", "PopupPanel", "ColorPicker", "TooltipPanel" })
			theme.SetStylebox("panel", type, PanelStyle());
		theme.SetStylebox("panel", "WoodlandCatalog", CatalogStyle());
		theme.SetTypeVariation("WoodlandCatalog", "ScrollContainer");
		theme.SetColor("font_color", "TooltipLabel", Text);
		foreach (var state in new[] { "normal", "read_only" })
			theme.SetStylebox(state, "LineEdit", PanelStyle());
		theme.SetStylebox("focus", "LineEdit", FocusStyle());
		theme.SetColor("font_color", "LineEdit", Text);
		theme.SetColor("font_uneditable_color", "LineEdit", Muted);
		theme.SetColor("font_placeholder_color", "LineEdit", Muted);
		theme.SetColor("caret_color", "LineEdit", Accent);
		theme.SetColor("selection_color", "LineEdit", Color.FromHtml("#cbd1a1"));
		theme.SetColor("font_selected_color", "LineEdit", Text);
		foreach (var type in new[] { "CheckBox", "CheckButton" })
		{
			foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
				theme.SetColor(state, type, Text);
			theme.SetColor("font_disabled_color", type, Muted);
			theme.SetIcon("checked", type, Icon("checked")!);
			theme.SetIcon("unchecked", type, Icon("unchecked")!);
			theme.SetIcon("checked_disabled", type, Icon("checked")!);
			theme.SetIcon("unchecked_disabled", type, Icon("unchecked")!);
			foreach (var state in new[] { "normal", "pressed", "disabled" })
				theme.SetStylebox(state, type, new StyleBoxEmpty());
			theme.SetStylebox("hover", type, Flat(Color.FromHtml("#e3d4ab"), new Color(0, 0, 0, 0), 8));
			theme.SetStylebox("hover_pressed", type, Flat(Color.FromHtml("#e3d4ab"), new Color(0, 0, 0, 0), 8));
			theme.SetStylebox("focus", type, FocusStyle());
			theme.SetConstant("h_separation", type, 9);
		}
		foreach (var type in new[] { "HSlider", "VSlider" })
		{
			theme.SetStylebox("slider", type, Flat(Color.FromHtml("#c1b68b"), Border, 4));
			theme.SetStylebox("grabber_area", type, Flat(Moss, Moss, 4));
			theme.SetStylebox("grabber_area_highlight", type, Flat(Accent, Accent, 4));
			theme.SetIcon("grabber", type, Icon("grabber")!);
			theme.SetIcon("grabber_highlight", type, Icon("grabber")!);
			theme.SetIcon("grabber_disabled", type, Icon("unchecked")!);
		}
		foreach (var type in new[] { "HScrollBar", "VScrollBar" })
		{
			theme.SetStylebox("scroll", type, Flat(Color.FromHtml("#d3cba6"), new Color(0, 0, 0, 0), 5));
			theme.SetStylebox("grabber", type, Flat(Moss, Moss, 5));
			theme.SetStylebox("grabber_highlight", type, Flat(Color.FromHtml("#61794c"), Moss, 5));
			theme.SetStylebox("grabber_pressed", type, Flat(Accent, Accent, 5));
		}
		theme.SetStylebox("separator", "HSeparator", Flat(Border, Border, 1));
		theme.SetColor("font_color", "SpinBox", Text);
		theme.SetIcon("updown", "SpinBox", Icon("updown")!);
		theme.SetIcon("up", "SpinBox", Icon("up")!);
		theme.SetIcon("down", "SpinBox", Icon("down")!);
		theme.SetStylebox("tab_selected", "TabBar", Skin("woodland-stitched.png", 9, new Color(0.78f, 0.85f, 0.66f)));
		theme.SetStylebox("tab_unselected", "TabBar", PanelStyle());
		theme.SetStylebox("tab_hovered", "TabBar", PanelStyle());
		theme.SetColor("font_selected_color", "TabBar", Text);
		theme.SetColor("font_unselected_color", "TabBar", Muted);
		theme.SetColor("font_hovered_color", "TabBar", Text);
		theme.SetStylebox("panel", "PopupMenu", PanelStyle(true));
		theme.SetStylebox("hover", "PopupMenu", PanelStyle());
		theme.SetColor("font_color", "PopupMenu", Text);
		theme.SetColor("font_hover_color", "PopupMenu", Accent);
		return theme;
	}

	public static void StyleButton(Button button, string variation = "Button") => button.ThemeTypeVariation = variation;

	public static void ApplyPopup(Window popup)
	{
		popup.Theme = popup.GetParent() is Control owner ? owner.GetWindow().Theme : Build();
		// Godot gives the color-mode buttons inline gray styleboxes. Keep its wheel
		// and color swatches intact, but replace those inline button surfaces.
		void ApplyModeButtons(Node node)
		{
			foreach (var child in node.GetChildren(true))
			{
				if (child is Button button && button.Text is "RGB" or "HSV" or "Linear" or "Raw")
				{
					StyleButton(button, "SecondaryButton");
					foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
						button.AddThemeStyleboxOverride(state, popup.Theme.GetStylebox(state, "SecondaryButton"));
				}
				ApplyModeButtons(child);
			}
		}
		ApplyModeButtons(popup);
	}

	public static Texture2D? Icon(string name) => Load($"Icons/{name}.svg");

	public static StyleBox WindowStyle()
	{
		var style = Skin("woodland-window.png", 48, Colors.White);
		style.SetContentMarginAll(0);
		return style;
	}

	public static StyleBox PanelStyle(bool woodFrame = false)
	{
		var style = Skin(woodFrame ? "woodland-panel.png" : "woodland-stitched.png", woodFrame ? 20 : 9, Colors.White);
		style.SetContentMarginAll(woodFrame ? 14 : 10);
		return style;
	}

	public static StyleBox CatalogStyle()
	{
		var style = Skin("woodland-stitched.png", 9, new Color(0.76f, 0.82f, 0.63f));
		style.SetContentMarginAll(4);
		return style;
	}

	private static void ButtonStyles(Theme theme, string type, float scale)
	{
		var action = type == "ActionButton";
		var card = type == "CardButton";
		var tab = type == "TabButton";
		var normal = action ? new Color(0.76f, 0.43f, 0.28f) : Colors.White;
		var hover = action ? new Color(0.9f, 0.54f, 0.34f) : new Color(0.93f, 0.97f, 0.81f);
		var pressed = card ? new Color(1f, 0.77f, 0.52f) : tab || action ? new Color(0.76f, 0.43f, 0.28f) : new Color(0.84f, 0.9f, 0.7f);
		foreach (var (state, tint) in new[] { ("normal", normal), ("hover", hover), ("pressed", pressed), ("hover_pressed", pressed), ("disabled", new Color(0.87f, 0.85f, 0.74f)) })
		{
			var style = Skin("woodland-stitched.png", 9, tint);
			style.SetContentMargin(Side.Left, card ? 7 : 11);
			style.SetContentMargin(Side.Right, card ? 7 : 11);
			style.SetContentMargin(Side.Top, card ? 7 : 7);
			style.SetContentMargin(Side.Bottom, card ? 7 : 7);
			theme.SetStylebox(state, type, style);
		}
		theme.SetStylebox("focus", type, FocusStyle());
		foreach (var state in new[] { "font_color", "font_hover_color", "font_focus_color" })
			theme.SetColor(state, type, action ? Cream : Text);
		theme.SetColor("font_pressed_color", type, tab || action ? Cream : Text);
		theme.SetColor("font_hover_pressed_color", type, tab || action ? Cream : Text);
		theme.SetColor("font_disabled_color", type, Muted);
		theme.SetColor("icon_normal_color", type, action ? Cream : Text);
		theme.SetColor("icon_hover_color", type, action ? Cream : Text);
		theme.SetColor("icon_pressed_color", type, tab || action ? Cream : Text);
		theme.SetFontSize("font_size", type, Mathf.RoundToInt((card ? 13 : 14) * scale));
		theme.SetConstant("h_separation", type, 6);
	}

	private static StyleBoxTexture Skin(string file, float margin, Color tint)
	{
		var style = new StyleBoxTexture
		{
			Texture = Load(file),
			ModulateColor = tint,
			AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Stretch,
			AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Stretch
		};
		style.SetTextureMarginAll(margin);
		return style;
	}

	private static StyleBoxFlat FocusStyle()
	{
		var style = Flat(new Color(0, 0, 0, 0), Accent, 10);
		style.SetBorderWidthAll(2);
		style.SetContentMarginAll(0);
		return style;
	}

	private static StyleBoxFlat Flat(Color fill, Color border, int radius)
	{
		var style = new StyleBoxFlat { BgColor = fill, BorderColor = border };
		style.SetCornerRadiusAll(radius);
		style.SetBorderWidthAll(border.A == 0 ? 0 : 1);
		style.SetContentMarginAll(3);
		return style;
	}

	private static Texture2D? Load(string file)
	{
		if (Textures.TryGetValue(file, out var texture)) return texture;
		texture = ResourceLoader.Load<Texture2D>($"{ArtPath}/{file}");
		if (texture != null) Textures[file] = texture;
		return texture;
	}
}
