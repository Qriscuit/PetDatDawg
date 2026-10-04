using Godot;

/// <summary>Code-drawn text artwork shared by the wardrobe preview and desktop pet.</summary>
[Tool]
public partial class PetTextAccessory : Node2D
{
	[Export] public DesktopAppearance? Appearance { get; set; }
	public static Vector2 ReferenceSize => DesktopAppearance.Default.TextCanvasSize;
	public Vector2 BubbleSize => (Appearance ?? DesktopAppearance.Default).TextCanvasSize;
	private const TextServer.LineBreakFlag Breaks = TextServer.LineBreakFlag.Mandatory | TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.GraphemeBound;
	[Export(PropertyHint.MultilineText)] public string Text { get; set; } = AccessoryWardrobe.DefaultText;
	[Export] public Color TextColor { get; set; } = Colors.White;
	[Export] public bool BackgroundVisible { get; set; } = true;
	public override void _Process(double delta) { if (Engine.IsEditorHint()) QueueRedraw(); }
	public override void _Draw() => DrawTextBox(this, Text, TextColor, BackgroundVisible, Appearance);
	public static void DrawTextBox(CanvasItem canvas, string text, Color color, bool backgroundVisible = true, DesktopAppearance? appearance = null)
	{
		var look = appearance ?? DesktopAppearance.Default;
		var referenceSize = look.TextCanvasSize;
		var font = look.TextFont ?? ThemeDB.FallbackFont;
		if (backgroundVisible && look.TextBubbleStyle != null) canvas.DrawStyleBox(look.TextBubbleStyle, new Rect2(-referenceSize * 0.5f, referenceSize));
		var fontSize = look.TextFontSize;
		var area = referenceSize - look.TextPadding * 2;
		Vector2 textSize;
		do
		{
			textSize = font.GetMultilineStringSize(text, HorizontalAlignment.Center, area.X, fontSize, -1, Breaks);
			if (textSize.Y <= area.Y || fontSize <= look.MinimumTextFontSize) break;
			fontSize--;
		} while (true);
		var origin = new Vector2(look.TextPadding.X, (referenceSize.Y - textSize.Y) * 0.5f + font.GetAscent(fontSize)) - referenceSize * 0.5f;
		if (!backgroundVisible)
		{
			// Keep free-floating letters readable against both light and dark desktops.
			var lightText = color.R * 0.2126f + color.G * 0.7152f + color.B * 0.0722f > 0.45f;
			var outline = new Color(lightText ? look.LightTextOutline : look.DarkTextOutline, color.A * 0.85f);
			canvas.DrawMultilineStringOutline(font, origin, text, HorizontalAlignment.Center, area.X, fontSize, -1, look.TextOutlineSize, outline, Breaks);
		}
		canvas.DrawMultilineString(font, origin, text, HorizontalAlignment.Center, area.X, fontSize, -1, color, Breaks);
	}
}
