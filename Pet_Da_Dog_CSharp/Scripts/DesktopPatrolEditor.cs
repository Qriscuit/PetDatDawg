using System;
using Godot;

/// <summary>Numbered desktop stops. Captures input only during explicit route editing.</summary>
public partial class DesktopPatrolEditor : Node2D
{
	private const float MarkerRadius = 18;
	private DesktopPet? _pet;
	private PatrolRoute? _route;
	private PatrolRouteToolbar? _toolbar;
	private int _draggedPoint = -1;
	private Vector2 _dragOffset;
	private bool Active => _pet?.IsPatrolEditing == true && _route?.IsEditing == true;

	public void Configure(DesktopPet pet, PatrolRoute route)
	{
		_pet = pet;
		_route = route;
		ZIndex = 200;
		_route.Changed += OnChanged;
		_route.EditingChanged += OnEditingChanged;
		_toolbar = new PatrolRouteToolbar { Visible = false, ForceNative = true };
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
	public Vector2 GetMarkerPosition(int index) => _pet != null && _route != null && index >= 0 && index < _route.DraftPoints.Count
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
		if (!Active || _route == null) return;
		var count = _route.DraftPoints.Count;
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
			DrawLine(start, end, new Color(0.08f, 0.13f, 0.07f, 0.75f), 5, true);
			DrawLine(start, end, new Color(0.84f, 0.89f, 0.55f, 0.9f), 2, true);
			var middle = start.Lerp(end, 0.6f);
			DrawPolyline(new[] { middle - direction.Rotated(-0.55f) * 10, middle, middle - direction.Rotated(0.55f) * 10 }, WoodlandTheme.Parchment, 3, true);
		}
		var font = ThemeDB.FallbackFont;
		for (var i = 0; i < count; i++)
		{
			var center = GetMarkerPosition(i);
			DrawCircle(center + new Vector2(1, 2), MarkerRadius + 3, new Color(0, 0, 0, 0.35f));
			DrawCircle(center, MarkerRadius + 2, WoodlandTheme.Parchment);
			DrawCircle(center, MarkerRadius, WoodlandTheme.Moss);
			var label = (i + 1).ToString();
			var width = font.GetStringSize(label, HorizontalAlignment.Left, -1, 17).X;
			DrawString(font, center + new Vector2(-width * 0.5f, 6), label, HorizontalAlignment.Left, -1, 17, WoodlandTheme.Parchment);
		}
	}
}
