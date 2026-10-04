using System;
using Godot;

/// <summary>Numbered desktop stops. Captures input only during explicit route editing.</summary>
[Tool]
public partial class DesktopPatrolEditor : Node2D
{
	[Export] public DesktopAppearance? Appearance { get; set; }
	[Export] public PackedScene? ToolbarScene { get; set; }
	[Export] public bool ShowEditorPreview { get; set; } = true;
	[Export] public Godot.Collections.Array<Vector2> EditorPreviewPoints { get; set; } = new() { new(110, 120), new(290, 250), new(410, 140) };
	private DesktopAppearance Look => Appearance ?? DesktopAppearance.Default;
	private float MarkerRadius => Mathf.Max(8, Look.MarkerRadius);
	private DesktopPet? _pet;
	private PatrolRoute? _route;
	private PatrolRouteToolbar? _toolbar;
	private int _draggedPoint = -1;
	private Vector2 _dragOffset;
	private bool Active => _pet?.IsPatrolEditing == true && _route?.IsEditing == true;

	public void Configure(DesktopPet pet, PatrolRoute route)
	{
		if (Engine.IsEditorHint()) return;
		_pet = pet;
		_route = route;
		_route.Changed += OnChanged;
		_route.EditingChanged += OnEditingChanged;
		_toolbar = (ToolbarScene ?? ResourceLoader.Load<PackedScene>("res://UI/PatrolRouteToolbar.tscn")).Instantiate<PatrolRouteToolbar>();
		_toolbar.Visible = false;
		_toolbar.ForceNative = true;
		AddChild(_toolbar);
		_toolbar.Configure(route);
		RefreshLayout(DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenPrimary));
	}
	public override void _ExitTree()
	{
		if (_route == null) return;
		_route.Changed -= OnChanged;
		_route.EditingChanged -= OnEditingChanged;
	}
	public void RefreshLayout(Rect2I usable)
	{
		_toolbar?.Reposition(usable);
		QueueRedraw();
	}
	private void OnChanged()
	{
		if (_route != null && _draggedPoint >= _route.DraftPoints.Count) _draggedPoint = -1;
		QueueRedraw();
	}
	private void OnEditingChanged(bool active)
	{
		_draggedPoint = -1;
		QueueRedraw();
	}
	public Vector2 GetMarkerPosition(int index) => Engine.IsEditorHint() && index >= 0 && index < EditorPreviewPoints.Count
		? EditorPreviewPoints[index] : _pet != null && _route != null && index >= 0 && index < _route.DraftPoints.Count
		? _pet.PatrolPointToViewport(_route.DraftPoints[index]) : Vector2.Zero;
	private int HitMarker(Vector2 point)
	{
		if (_route == null) return -1;
		for (var i = _route.DraftPoints.Count - 1; i >= 0; i--)
			if (point.DistanceSquaredTo(GetMarkerPosition(i)) <= (MarkerRadius + 5) * (MarkerRadius + 5)) return i;
		return -1;
	}
	public bool HandlePointer(Vector2 point, MouseButton button, bool pressed)
	{
		if (!Active || _route == null || _pet == null || !point.IsFinite()) return false;
		if (button == MouseButton.Right && pressed)
		{
			_draggedPoint = -1;
			var hit = HitMarker(point);
			if (hit >= 0) _route.RemovePoint(hit);
			return true;
		}
		if (button != MouseButton.Left) return false;
		if (!pressed)
		{
			UpdatePointer(point);
			_draggedPoint = -1;
			return true;
		}
		_draggedPoint = HitMarker(point);
		if (_draggedPoint >= 0) _dragOffset = point - GetMarkerPosition(_draggedPoint);
		else _route.AddPoint(_pet.NormalizePatrolPoint(point));
		return true;
	}
	public void UpdatePointer(Vector2 point)
	{
		if (Active && _draggedPoint >= 0 && _pet != null && point.IsFinite())
			_route?.MovePoint(_draggedPoint, _pet.NormalizePatrolPoint(point - _dragOffset));
	}
	public void PollPointer()
	{
		if (!Active || _draggedPoint < 0) return;
		var point = (Vector2)(DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition((int)DisplayServer.MainWindowId));
		UpdatePointer(point);
		if (!NativeWindowBridge.IsLeftMouseButtonDown()) _draggedPoint = -1;
	}
	public void HandleInput(InputEvent inputEvent)
	{
		if (inputEvent is InputEventMouseMotion motion) UpdatePointer(motion.Position);
		if (inputEvent is InputEventMouseButton button) HandlePointer(button.Position, button.ButtonIndex, button.Pressed);
	}
	public override void _Draw()
	{
		if (Engine.IsEditorHint() ? !ShowEditorPreview : !Active || _route == null) return;
		var count = Engine.IsEditorHint() ? EditorPreviewPoints.Count : _route!.DraftPoints.Count;
		for (var i = 0; i < count; i++)
		{
			if (i + 1 >= count && count < 2) continue;
			var start = GetMarkerPosition(i);
			var end = GetMarkerPosition((i + 1) % count);
			var offset = end - start;
			if (offset.Length() < MarkerRadius * 2 + 12) continue;
			var direction = offset.Normalized();
			start += direction * (MarkerRadius + 3);
			end -= direction * (MarkerRadius + 3);
			DrawLine(start, end, Look.RouteShadowColor, Look.RouteShadowWidth, true);
			DrawLine(start, end, Look.RouteLineColor, Look.RouteLineWidth, true);
			var middle = start.Lerp(end, Look.ArrowPosition);
			DrawPolyline(new[] { middle - direction.Rotated(-Look.ArrowAngle) * Look.ArrowSize, middle,
				middle - direction.Rotated(Look.ArrowAngle) * Look.ArrowSize }, Look.ArrowColor, Look.ArrowWidth, true);
		}
		var font = Look.MarkerFont ?? ThemeDB.FallbackFont;
		for (var i = 0; i < count; i++)
		{
			var center = GetMarkerPosition(i);
			DrawCircle(center + Look.MarkerShadowOffset, MarkerRadius + Look.MarkerShadowSize, Look.MarkerShadowColor);
			DrawCircle(center, MarkerRadius + Look.MarkerBorderWidth, Look.MarkerBorderColor);
			DrawCircle(center, MarkerRadius, Look.MarkerFillColor);
			var label = (i + 1).ToString();
			var width = font.GetStringSize(label, HorizontalAlignment.Left, -1, Look.MarkerFontSize).X;
			DrawString(font, center + new Vector2(-width * 0.5f, Look.MarkerTextBaseline), label, HorizontalAlignment.Left, -1, Look.MarkerFontSize, Look.MarkerTextColor);
		}
	}
	public override void _Process(double delta) { if (Engine.IsEditorHint()) QueueRedraw(); }
}
