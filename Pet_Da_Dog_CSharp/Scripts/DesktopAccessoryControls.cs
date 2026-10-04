using System;
using System.Collections.Generic;
using Godot;

/// <summary>Direct manipulation of the desktop dog. Editing never queues pet grants.</summary>
[Tool]
public partial class DesktopAccessoryControls : Node2D
{
	[Export] public DesktopAppearance? Appearance { get; set; }
	[Export] public PackedScene? LayerMenuScene { get; set; }
	[Export] public bool ShowEditorPreview { get; set; } = true;
	[Export] public Rect2 EditorPreviewBounds { get; set; } = new(140, 90, 128, 128);
	private DesktopAppearance Look => Appearance ?? DesktopAppearance.Default;
	private float HandleRadius => Mathf.Max(4, Look.HandleRadius);
	private enum Gesture { None, MoveDog, ResizeDog, MoveAccessory, ResizeAccessory, RotateAccessory }
	private DesktopPet? _pet;
	private AccessoryWardrobe? _wardrobe;
	private AccessoryEditingSession? _session;
	private AccessoryLayerMenu? _layerMenu;
	private readonly Dictionary<string, Image> _hitImages = new();
	private Gesture _gesture;
	private Vector2 _pointer, _pointerStart, _dogStart, _dragOffset, _center;
	private float _scaleStart, _rotationStart, _radiusStart, _angleStart, _dogHeightStart;
	private string? _gestureId;

	public void Configure(DesktopPet pet, AccessoryWardrobe? wardrobe, AccessoryEditingSession? session)
	{
		if (Engine.IsEditorHint()) return;
		_pet = pet; _wardrobe = wardrobe; _session = session;
		if (_session != null)
		{
			_session.StateChanged += OnStateChanged;
			_session.InteractionCanceled += RestoreCanceledGesture;
		}
		if (_wardrobe != null) _wardrobe.Changed += QueueRedraw;
		if (_wardrobe != null && _session != null)
		{
			_layerMenu = (LayerMenuScene ?? ResourceLoader.Load<PackedScene>("res://UI/AccessoryLayerMenu.tscn")).Instantiate<AccessoryLayerMenu>();
			_layerMenu.Visible = false;
			_layerMenu.ForceNative = true;
			AddChild(_layerMenu);
			_layerMenu.Configure(_wardrobe, _session);
			_layerMenu.OutfitSaved += OnLayerOutfitSaved;
		}
	}

	public override void _ExitTree()
	{
		if (_layerMenu != null) _layerMenu.OutfitSaved -= OnLayerOutfitSaved;
		if (_session != null)
		{
			_session.StateChanged -= OnStateChanged;
			_session.InteractionCanceled -= RestoreCanceledGesture;
		}
		if (_wardrobe != null) _wardrobe.Changed -= QueueRedraw;
		foreach (var image in _hitImages.Values) image.Dispose();
	}
	private void OnLayerOutfitSaved(bool success) => _session?.ReportOutfitSave(success);

	private bool Active => _pet?.IsAccessoryEditing == true && _session?.Active == true;
	private Rect2 DogBounds => Engine.IsEditorHint() ? EditorPreviewBounds : _pet == null ? new Rect2() : AccessoryGeometry.BoundsFromCorners(_pet.EditableDog, _pet.EditableDogRect);
	private Node2D? SelectedNode => _session?.SelectedId is string id ? _pet?.AccessoryNode(id) : null;
	private static Rect2 NodeRect(Node2D node) => node is Sprite2D sprite ? sprite.GetRect()
		: node is PetTextAccessory text ? new Rect2(-text.BubbleSize * 0.5f, text.BubbleSize) : new Rect2();

	public Rect2 GetDogMoveRect()
	{
		var dog = DogBounds;
		// Keep this explicit dog control clear of equipped text and rotation handles.
		var top = Mathf.Min(dog.Position.Y, _pet?.EditableOutfitBounds.Position.Y ?? dog.Position.Y);
		return new Rect2(new Vector2(dog.GetCenter().X - Look.MoveDogSize.X * 0.5f, top - Look.MoveDogGap), Look.MoveDogSize);
	}
	public Vector2 GetDogResizeHandle() => DogBounds.End + Look.DogResizeOffset;
	public Vector2[] GetSelectionCorners()
	{
		var node = SelectedNode;
		if (node == null) return Array.Empty<Vector2>();
		var rect = NodeRect(node).Grow(node is PetTextAccessory ? Look.SelectionPadding : Look.SelectionPadding / Mathf.Max(0.001f, node.GlobalScale.Abs().X));
		return new[] { node.ToGlobal(rect.Position), node.ToGlobal(new Vector2(rect.End.X, rect.Position.Y)),
			node.ToGlobal(rect.End), node.ToGlobal(new Vector2(rect.Position.X, rect.End.Y)) };
	}
	public Vector2 GetAccessoryResizeHandle()
	{
		var corners = GetSelectionCorners();
		return corners.Length == 0 ? Vector2.Zero : corners[2] + (corners[2] - SelectedNode!.GlobalPosition).Normalized() * Look.AccessoryResizeOffset;
	}
	public Vector2 GetAccessoryRotationHandle()
	{
		var corners = GetSelectionCorners();
		if (corners.Length == 0) return Vector2.Zero;
		var middle = (corners[0] + corners[1]) * 0.5f;
		return middle + (middle - SelectedNode!.GlobalPosition).Normalized() * Look.RotationHandleOffset;
	}
	private bool Near(Vector2 point, Vector2 handle) => point.DistanceSquaredTo(handle) <= (HandleRadius + 4) * (HandleRadius + 4);
	private Rect2 PlacementArea(string id)
	{
		if (_pet == null) return new Rect2();
		var rect = _pet.EditableDogRect;
		if (_wardrobe?.Find(id)?.IsText == true)
			rect = new Rect2(rect.Position + rect.Size * AccessoryWardrobe.TextMinPosition,
				rect.Size * (AccessoryWardrobe.TextMaxPosition - AccessoryWardrobe.TextMinPosition));
		return AccessoryGeometry.BoundsFromCorners(_pet.EditableDog, rect);
	}

	public bool IsInteractiveAt(Vector2 point)
	{
		if (!Active || !point.IsFinite()) return false;
		if (_gesture != Gesture.None) return true;
		if (GetDogMoveRect().HasPoint(point) || (SelectedNode == null && Near(point, GetDogResizeHandle()))) return true;
		if (SelectedNode != null && (Near(point, GetAccessoryResizeHandle()) || Near(point, GetAccessoryRotationHandle()))) return true;
		if (_session!.IsPlacing && _session.SelectedId is string id && PlacementArea(id).HasPoint(point)) return true;
		return HitLayer(point) != null;
	}

	public bool HandlePointer(Vector2 position, bool pressed)
	{
		if (!Active || _pet == null || _wardrobe == null || _session == null || !position.IsFinite()) return false;
		_pointer = position;
		if (!pressed)
		{
			if (_gesture == Gesture.None) return false;
			UpdatePointer(position);
			_gesture = Gesture.None; _gestureId = null;
			_session.CompleteGesture(); QueueRedraw();
			return true;
		}
		if (_gesture != Gesture.None) CancelGesture();
		if (_session.IsPlacing && _session.SelectedId is string placing && PlacementArea(placing).HasPoint(position))
			return _session.PlaceAt(_pet.NormalizeAccessoryPoint(position));
		if (SelectedNode != null && Near(position, GetAccessoryRotationHandle()))
			return StartAccessoryGesture(Gesture.RotateAccessory, position);
		if (SelectedNode != null && Near(position, GetAccessoryResizeHandle()))
			return StartAccessoryGesture(Gesture.ResizeAccessory, position);
		if (SelectedNode == null && Near(position, GetDogResizeHandle())) return StartDogGesture(Gesture.ResizeDog, position);
		if (HitLayer(position) is string hit && hit != AccessoryWardrobe.DogLayerId)
		{
			_session.SelectAccessory(hit);
			return StartAccessoryGesture(Gesture.MoveAccessory, position);
		}
		if (GetDogMoveRect().HasPoint(position)) return StartDogGesture(Gesture.MoveDog, position);
		if (_pet.HitEditableDog(position)) return StartDogGesture(Gesture.MoveDog, position);
		return false;
	}

	public bool HandleContextMenu(Vector2 position)
	{
		if (!Active || !position.IsFinite() || _pet == null || _layerMenu == null) return false;
		UpdatePointer(position);
		_session!.CompleteGesture();
		CancelGesture();
		_pointer = position;
		var hit = SelectedNode != null && (Near(position, GetAccessoryResizeHandle()) || Near(position, GetAccessoryRotationHandle()))
			? _session.SelectedId : HitLayer(position);
		if (hit == null && GetDogMoveRect().HasPoint(position)) hit = AccessoryWardrobe.DogLayerId;
		if (hit == null) { _layerMenu.HideMenu(); return false; }
		var screenPoint = new Vector2I(Mathf.RoundToInt(position.X), Mathf.RoundToInt(position.Y))
			+ DisplayServer.WindowGetPosition((int)DisplayServer.MainWindowId);
		_layerMenu.ShowFor(hit, screenPoint);
		return true;
	}

	private bool StartDogGesture(Gesture gesture, Vector2 point)
	{
		_session!.ClearSelection();
		if (!_session.BeginGesture(gesture == Gesture.MoveDog ? "move dog" : "resize dog")) return false;
		_pointerStart = point; _dogStart = _pet!.EditingPosition;
		_scaleStart = _pet.EditableDogScale;
		_dogHeightStart = DogBounds.Size.Y;
		_center = DogBounds.GetCenter();
		_radiusStart = Mathf.Max(1, point.DistanceTo(_center));
		_gesture = gesture; QueueRedraw();
		return true;
	}
	private bool StartAccessoryGesture(Gesture gesture, Vector2 point)
	{
		if (_session?.SelectedId is not string id || SelectedNode is not Node2D node || _wardrobe == null) return false;
		if (!_session.BeginGesture(gesture switch { Gesture.MoveAccessory => "move accessory", Gesture.ResizeAccessory => "resize accessory", _ => "rotate accessory" })) return false;
		_gestureId = id; _center = node.GlobalPosition;
		_dogStart = _pet!.EditingPosition;
		_dragOffset = point - _center;
		_scaleStart = _wardrobe.GetScale(id); _rotationStart = _wardrobe.GetRotationDegrees(id);
		_radiusStart = Mathf.Max(1, point.DistanceTo(_center)); _angleStart = (point - _center).Angle();
		_gesture = gesture; QueueRedraw();
		return true;
	}

	public void UpdatePointer(Vector2 position)
	{
		if (!position.IsFinite()) return;
		var moved = position != _pointer;
		_pointer = position;
		if (!Active || _pet == null || _wardrobe == null) return;
		if (!moved && _gesture == Gesture.None) return;
		switch (_gesture)
		{
			case Gesture.MoveDog: _pet.MoveEditingDog(_dogStart + position - _pointerStart); break;
			case Gesture.ResizeDog:
				_pet.ResizeEditingDog(_scaleStart * position.DistanceTo(_center) / _radiusStart);
				_pet.MoveEditingDog(_dogStart + new Vector2(0, (DogBounds.Size.Y - _dogHeightStart) * 0.5f));
				break;
			case Gesture.MoveAccessory when _gestureId != null:
				_wardrobe.Move(_gestureId, _pet.NormalizeAccessoryPoint(position - _dragOffset)); break;
			case Gesture.ResizeAccessory when _gestureId != null:
				_wardrobe.SetTransform(_gestureId, _scaleStart * position.DistanceTo(_center) / _radiusStart, _rotationStart); break;
			case Gesture.RotateAccessory when _gestureId != null:
				var direction = Mathf.Sign(_pet.EditableDog.GlobalTransform.Determinant());
				var angle = _rotationStart + direction * Mathf.RadToDeg((position - _center).Angle() - _angleStart);
				_wardrobe.SetTransform(_gestureId, _scaleStart, Mathf.Wrap(angle, -180f, 180f)); break;
		}
		QueueRedraw();
	}

	public void PollPointer()
	{
		if (!Active) return;
		UpdatePointer((Vector2)(DisplayServer.MouseGetPosition() - DisplayServer.WindowGetPosition((int)DisplayServer.MainWindowId)));
		// Global polling catches releases beyond a native clip or window boundary.
		if (_gesture != Gesture.None && !NativeWindowBridge.IsLeftMouseButtonDown()) HandlePointer(_pointer, false);
	}
	public void CancelGesture() => _session?.CancelInteraction();
	private void RestoreCanceledGesture()
	{
		var previous = _gesture; _gesture = Gesture.None; _gestureId = null;
		if (previous == Gesture.ResizeDog) _pet?.ResizeEditingDog(_scaleStart);
		if (previous != Gesture.None) _pet?.MoveEditingDog(_dogStart);
		QueueRedraw();
	}
	private void OnStateChanged()
	{
		if (_session?.IsDragging != true) { _gesture = Gesture.None; _gestureId = null; }
		QueueRedraw();
	}
	public bool HandleKey(InputEventKey key)
	{
		if (!Active || !key.Pressed || key.Echo || _session == null || _wardrobe == null) return false;
		if (key.Keycode == Key.Escape) { CancelGesture(); return true; }
		if ((key.CtrlPressed || key.MetaPressed) && key.Keycode == Key.Z)
		{
			CancelGesture(); _wardrobe.Undo(); _wardrobe.Save(); return true;
		}
		if (key.Keycode == Key.Delete && _session.SelectedId is string id)
		{
			CancelGesture(); _wardrobe.Remove(id); _wardrobe.Save(); _session.ClearSelection(); return true;
		}
		return false;
	}

	private string? HitLayer(Vector2 point)
	{
		if (_wardrobe == null || _pet == null) return null;
		for (var index = _wardrobe.LayerOrder.Count - 1; index >= 0; index--)
		{
			var id = _wardrobe.LayerOrder[index];
			if (id == AccessoryWardrobe.DogLayerId)
			{
				if (_pet.HitEditableDog(point)) return id;
				continue;
			}
			var node = _pet.AccessoryNode(id);
			var definition = _wardrobe.Find(id);
			if (node == null || definition == null) continue;
			var rect = NodeRect(node); var local = node.ToLocal(point);
			if (!rect.HasPoint(local)) continue;
			if (definition.IsText) return id;
			if (!_hitImages.TryGetValue(id, out var image)) { image = definition.Texture.GetImage(); _hitImages.Add(id, image); }
			var uv = (local - rect.Position) / rect.Size;
			var x = Mathf.Clamp((int)(uv.X * image.GetWidth()), 0, image.GetWidth() - 1);
			var y = Mathf.Clamp((int)(uv.Y * image.GetHeight()), 0, image.GetHeight() - 1);
			if (image.GetPixel(x, y).A > 0.1f) return id;
		}
		return null;
	}

	public Rect2? GetRenderBounds()
	{
		if (!Active) return null;
		var padding = Mathf.Max(HandleRadius + Look.HandleBorderWidth + 4, Look.HandleIconSize.Length() * 0.5f + 4);
		padding = Mathf.Max(padding, Mathf.Abs(Look.HandleGlyphSize) * 1.2f + Mathf.Abs(Look.HandleGlyphWidth) * 0.5f + 4);
		padding = Mathf.Max(padding, Mathf.Abs(Look.SelectionWidth) * 0.5f + 2);
		var bounds = DogBounds.Grow(Look.DogOutlinePadding + Look.DogOutlineWidth + 2).Merge(GetDogMoveRect());
		var controlFont = Look.ControlFont ?? ThemeDB.FallbackFont;
		var textOrigin = GetDogMoveRect().Position + Look.MoveDogTextOffset;
		var textSize = controlFont.GetStringSize(Look.MoveDogText, HorizontalAlignment.Left, -1, Look.ControlFontSize);
		bounds = bounds.Merge(new Rect2(textOrigin - new Vector2(0, controlFont.GetAscent(Look.ControlFontSize)), textSize).Grow(2));
		if (SelectedNode == null) bounds = bounds.Merge(new Rect2(GetDogResizeHandle() - Vector2.One * padding, Vector2.One * padding * 2));
		if (SelectedNode is Node2D node)
		{
			bounds = bounds.Merge(AccessoryGeometry.BoundsFromCorners(node, NodeRect(node)));
			foreach (var corner in GetSelectionCorners())
				bounds = bounds.Merge(new Rect2(corner, Vector2.Zero).Grow(Mathf.Abs(Look.SelectionWidth) * 0.5f + 2));
			foreach (var handle in new[] { GetAccessoryResizeHandle(), GetAccessoryRotationHandle() })
				bounds = bounds.Merge(new Rect2(handle - Vector2.One * padding, Vector2.One * padding * 2));
		}
		if (_session!.IsPlacing && _session.SelectedId is string id && PlacementArea(id).HasPoint(_pointer))
		{
			var size = _wardrobe!.Find(id)!.Size * DogBounds.Size.Y * _wardrobe.GetScale(id);
			bounds = bounds.Merge(AccessoryGeometry.Bounds(_pointer, size, _wardrobe.GetRotationDegrees(id)).Grow(5));
		}
		return bounds;
	}

	public override void _Draw()
	{
		if (Engine.IsEditorHint())
		{
			if (!ShowEditorPreview) return;
			DrawDogControls();
			DrawHandle(GetDogResizeHandle(), false);
			DrawHandle(DogBounds.Position + new Vector2(64, -Look.RotationHandleOffset), true);
			return;
		}
		if (!Active || _pet == null || _session == null || _wardrobe == null) return;
		DrawDogControls();
		if (SelectedNode == null) DrawHandle(GetDogResizeHandle(), false);
		if (SelectedNode != null)
		{
			var corners = GetSelectionCorners();
			for (var i = 0; i < 4; i++) DrawLine(corners[i], corners[(i + 1) % 4], Look.SelectionColor, Look.SelectionWidth, true);
			DrawLine((corners[0] + corners[1]) * 0.5f, GetAccessoryRotationHandle(), Look.SelectionColor, Look.SelectionWidth, true);
			DrawHandle(GetAccessoryResizeHandle(), false);
			DrawHandle(GetAccessoryRotationHandle(), true);
		}
		if (_session.IsPlacing && _session.SelectedId is string id && PlacementArea(id).HasPoint(_pointer))
		{
			var definition = _wardrobe.Find(id)!;
			var direction = Mathf.Sign(_pet.EditableDog.GlobalTransform.Determinant());
			var size = definition.Size * DogBounds.Size.Y * _wardrobe.GetScale(id);
			var tint = _wardrobe.GetTint(id); tint.A = Look.PlacementOpacity;
			var rotation = direction * Mathf.DegToRad(_wardrobe.GetRotationDegrees(id));
			DrawSetTransform(_pointer, rotation, definition.IsText ? size / Look.TextCanvasSize : new Vector2(direction, 1));
			if (definition.IsText) PetTextAccessory.DrawTextBox(this, _wardrobe.GetText(), tint, _wardrobe.GetTextBackgroundVisible(), Look);
			else DrawTextureRect(definition.Texture, new Rect2(-size * 0.5f, size), false, tint);
			DrawSetTransform(Vector2.Zero);
		}
	}
	public override void _Process(double delta) { if (Engine.IsEditorHint()) QueueRedraw(); }
	private void DrawDogControls()
	{
		DrawRect(DogBounds.Grow(Look.DogOutlinePadding), Look.DogOutlineColor, false, Look.DogOutlineWidth);
		var move = GetDogMoveRect();
		if (Look.MoveDogStyle != null) DrawStyleBox(Look.MoveDogStyle, move);
		DrawString(Look.ControlFont ?? ThemeDB.FallbackFont, move.Position + Look.MoveDogTextOffset, Look.MoveDogText,
			HorizontalAlignment.Left, -1, Look.ControlFontSize, Look.ControlTextColor);
	}
	private void DrawHandle(Vector2 point, bool rotation)
	{
		DrawCircle(point, HandleRadius + Look.HandleBorderWidth, Look.HandleBorderColor, true, -1, true);
		DrawCircle(point, HandleRadius, Near(_pointer, point) ? Look.HandleHoverColor : Look.HandleFillColor, true, -1, true);
		var icon = rotation ? Look.RotationIcon : Look.ResizeIcon;
		if (icon != null) { DrawTextureRect(icon, new Rect2(point - Look.HandleIconSize * 0.5f, Look.HandleIconSize), false); return; }
		var size = Look.HandleGlyphSize;
		if (rotation)
		{
			DrawArc(point, size, -Mathf.Pi * 0.9f, Mathf.Pi * 0.7f, 16, Look.HandleGlyphColor, Look.HandleGlyphWidth, true);
			DrawLine(point + new Vector2(-size, -size * 0.4f), point + new Vector2(-size, -size * 1.2f), Look.HandleGlyphColor, Look.HandleGlyphWidth, true);
		}
		else
		{
			var diagonal = size * 0.8f;
			DrawLine(point - new Vector2(diagonal, diagonal), point + new Vector2(diagonal, diagonal), Look.HandleGlyphColor, Look.HandleGlyphWidth, true);
			DrawLine(point + new Vector2(diagonal, 0), point + new Vector2(diagonal, diagonal), Look.HandleGlyphColor, Look.HandleGlyphWidth, true);
			DrawLine(point + new Vector2(0, diagonal), point + new Vector2(diagonal, diagonal), Look.HandleGlyphColor, Look.HandleGlyphWidth, true);
		}
	}
}
