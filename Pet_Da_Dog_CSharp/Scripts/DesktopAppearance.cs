using Godot;

/// <summary>Editor-authored presentation for the desktop's procedural UI.</summary>
[Tool, GlobalClass]
public partial class DesktopAppearance : Resource
{
	[ExportGroup("Selection and handles")]
	[Export] public Color DogOutlineColor { get; set; } = new(0.937f, 0.878f, 0.741f, 0.75f);
	[Export] public Color SelectionColor { get; set; } = Color.FromHtml("a45031");
	[Export] public float DogOutlineWidth { get; set; } = 1;
	[Export] public float SelectionWidth { get; set; } = 2;
	[Export] public float DogOutlinePadding { get; set; } = 5;
	[Export] public float SelectionPadding { get; set; } = 4;
	[Export(PropertyHint.Range, "4,40,1")] public float HandleRadius { get; set; } = 11;
	[Export] public float HandleBorderWidth { get; set; } = 2;
	[Export] public Color HandleBorderColor { get; set; } = Color.FromHtml("a28954");
	[Export] public Color HandleFillColor { get; set; } = Color.FromHtml("efe0bd");
	[Export] public Color HandleHoverColor { get; set; } = Color.FromHtml("fff1d0");
	[Export] public Color HandleGlyphColor { get; set; } = Color.FromHtml("293c28");
	[Export] public float HandleGlyphWidth { get; set; } = 1.5f;
	[Export] public float HandleGlyphSize { get; set; } = 5;
	[Export] public Texture2D? ResizeIcon { get; set; }
	[Export] public Texture2D? RotationIcon { get; set; }
	[Export] public Vector2 HandleIconSize { get; set; } = new(12, 12);
	[Export] public Vector2 DogResizeOffset { get; set; } = new(17, 17);
	[Export] public float AccessoryResizeOffset { get; set; } = 13;
	[Export] public float RotationHandleOffset { get; set; } = 29;
	[ExportGroup("Move dog control")]
	[Export] public StyleBox? MoveDogStyle { get; set; }
	[Export] public Font? ControlFont { get; set; }
	[Export] public int ControlFontSize { get; set; } = 12;
	[Export] public Color ControlTextColor { get; set; } = Color.FromHtml("293c28");
	[Export] public string MoveDogText { get; set; } = "Move dog";
	[Export] public Vector2 MoveDogSize { get; set; } = new(98, 25);
	[Export] public float MoveDogGap { get; set; } = 82;
	[Export] public Vector2 MoveDogTextOffset { get; set; } = new(14, 17);
	[ExportGroup("Patrol markers")]
	[Export(PropertyHint.Range, "8,50,1")] public float MarkerRadius { get; set; } = 18;
	[Export] public float MarkerBorderWidth { get; set; } = 2;
	[Export] public Color MarkerFillColor { get; set; } = Color.FromHtml("486038");
	[Export] public Color MarkerBorderColor { get; set; } = Color.FromHtml("efe0bd");
	[Export] public Color MarkerTextColor { get; set; } = Color.FromHtml("efe0bd");
	[Export] public Font? MarkerFont { get; set; }
	[Export] public int MarkerFontSize { get; set; } = 17;
	[Export] public float MarkerTextBaseline { get; set; } = 6;
	[Export] public Color MarkerShadowColor { get; set; } = new(0, 0, 0, 0.35f);
	[Export] public Vector2 MarkerShadowOffset { get; set; } = new(1, 2);
	[Export] public float MarkerShadowSize { get; set; } = 3;
	[ExportGroup("Patrol route lines")]
	[Export] public Color RouteLineColor { get; set; } = new(0.84f, 0.89f, 0.55f, 0.9f);
	[Export] public float RouteLineWidth { get; set; } = 2;
	[Export] public Color RouteShadowColor { get; set; } = new(0.08f, 0.13f, 0.07f, 0.75f);
	[Export] public float RouteShadowWidth { get; set; } = 5;
	[Export] public Color ArrowColor { get; set; } = Color.FromHtml("efe0bd");
	[Export] public float ArrowSize { get; set; } = 10;
	[Export] public float ArrowWidth { get; set; } = 3;
	[Export] public float ArrowAngle { get; set; } = 0.55f;
	[Export(PropertyHint.Range, "0,1,0.05")] public float ArrowPosition { get; set; } = 0.6f;
	[ExportGroup("Text accessory")]
	[Export] public StyleBox? TextBubbleStyle { get; set; }
	[Export] public Font? TextFont { get; set; }
	[Export] public Vector2 TextBubbleSize { get; set; } = new(200, 84);
	[Export] public Vector2 TextPadding { get; set; } = new(12, 9);
	[Export] public int TextFontSize { get; set; } = 24;
	[Export] public int MinimumTextFontSize { get; set; } = 8;
	[Export] public int TextOutlineSize { get; set; } = 2;
	[Export] public Color LightTextOutline { get; set; } = Colors.Black;
	[Export] public Color DarkTextOutline { get; set; } = Colors.White;
	public Vector2 TextCanvasSize => new(Mathf.Max(1, TextBubbleSize.X), Mathf.Max(1, TextBubbleSize.Y));
	[ExportGroup("Placement preview")]
	[Export(PropertyHint.Range, "0,1,0.05")] public float PlacementOpacity { get; set; } = 0.55f;

	public static DesktopAppearance Default => ResourceLoader.Load<DesktopAppearance>("res://UI/Theme/DesktopAppearance.tres");
}
