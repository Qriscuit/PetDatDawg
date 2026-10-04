using Godot;

/// <summary>Shared bounds for rotated cosmetic artwork in the preview and desktop overlay.</summary>
public static class AccessoryGeometry
{
	public static Rect2 BoundsFromCorners(Node2D node, Rect2 rect)
	{
		var first = node.ToGlobal(rect.Position);
		var bounds = new Rect2(first, Vector2.Zero);
		foreach (var point in new[] { rect.End, new Vector2(rect.End.X, rect.Position.Y), new Vector2(rect.Position.X, rect.End.Y) })
			bounds = bounds.Expand(node.ToGlobal(point));
		return bounds;
	}
	public static Rect2 Bounds(Vector2 center, Vector2 size, float rotationDegrees)
	{
		var angle = Mathf.DegToRad(rotationDegrees);
		var cosine = Mathf.Abs(Mathf.Cos(angle));
		var sine = Mathf.Abs(Mathf.Sin(angle));
		var extent = new Vector2(size.X * cosine + size.Y * sine, size.X * sine + size.Y * cosine);
		return new Rect2(center - extent * 0.5f, extent);
	}
}
