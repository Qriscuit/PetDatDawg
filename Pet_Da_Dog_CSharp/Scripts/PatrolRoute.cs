using System;
using System.Collections.Generic;
using Godot;

// Screen-relative walking preferences only; route editing never grants pets.
public partial class PatrolRoute : Node
{
	public const int MaxPoints = 16;
	private const string Section = "route";
	private readonly List<Vector2> _points = new();
	private readonly List<Vector2> _draftPoints = new();
	private IReadOnlyList<Vector2>? _pointsView, _draftPointsView;

	[Export] public string StoragePath { get; set; } = "user://patrol_route.cfg";
	public IReadOnlyList<Vector2> Points => _pointsView ??= _points.AsReadOnly();
	public IReadOnlyList<Vector2> DraftPoints => _draftPointsView ??= _draftPoints.AsReadOnly();
	public bool Enabled { get; private set; }
	public bool IsEditing { get; private set; }
	public bool CanFinish => IsEditing && HasDistinctStops(_draftPoints);
	public bool LastSaveSucceeded { get; private set; } = true;
	public event Action? Changed;
	public event Action<bool>? EditingChanged;

	public override void _Ready() => Load();

	public bool BeginEdit()
	{
		if (IsEditing) return false;
		_draftPoints.Clear();
		_draftPoints.AddRange(_points);
		IsEditing = true;
		Changed?.Invoke();
		EditingChanged?.Invoke(true);
		return true;
	}

	public bool AddPoint(Vector2 normalized)
	{
		if (!IsEditing || _draftPoints.Count >= MaxPoints || !normalized.IsFinite()) return false;
		normalized = normalized.Clamp(Vector2.Zero, Vector2.One);
		if (_draftPoints.Count > 0 && SamePoint(_draftPoints[^1], normalized)) return false;
		_draftPoints.Add(normalized);
		Changed?.Invoke();
		return true;
	}

	public bool MovePoint(int index, Vector2 normalized)
	{
		if (!IsEditing || index < 0 || index >= _draftPoints.Count || !normalized.IsFinite()) return false;
		normalized = normalized.Clamp(Vector2.Zero, Vector2.One);
		if (SamePoint(_draftPoints[index], normalized)
			|| index > 0 && SamePoint(_draftPoints[index - 1], normalized)
			|| index + 1 < _draftPoints.Count && SamePoint(_draftPoints[index + 1], normalized)) return false;
		_draftPoints[index] = normalized;
		Changed?.Invoke();
		return true;
	}

	public bool RemovePoint(int index)
	{
		if (!IsEditing || index < 0 || index >= _draftPoints.Count) return false;
		_draftPoints.RemoveAt(index);
		// Removing a middle stop can bring two equal stops together.
		if (index > 0 && index < _draftPoints.Count && SamePoint(_draftPoints[index - 1], _draftPoints[index]))
			_draftPoints.RemoveAt(index);
		Changed?.Invoke();
		return true;
	}

	public bool UndoLastPoint() => RemovePoint(_draftPoints.Count - 1);

	public bool ClearDraft()
	{
		if (!IsEditing || _draftPoints.Count == 0) return false;
		_draftPoints.Clear();
		Changed?.Invoke();
		return true;
	}

	public bool FinishEdit()
	{
		if (!CanFinish) return false;
		_points.Clear();
		_points.AddRange(_draftPoints);
		_draftPoints.Clear();
		Enabled = true;
		IsEditing = false;
		Save();
		Changed?.Invoke();
		EditingChanged?.Invoke(false);
		// A valid route remains usable for this run when persistence fails.
		return true;
	}

	public void CancelEdit()
	{
		if (!IsEditing) return;
		_draftPoints.Clear();
		IsEditing = false;
		Changed?.Invoke();
		EditingChanged?.Invoke(false);
	}

	public void SetEnabled(bool enabled)
	{
		enabled = enabled && HasDistinctStops(_points);
		if (Enabled == enabled) return;
		Enabled = enabled;
		Save();
		Changed?.Invoke();
	}

	public void ClearRoute()
	{
		var wasEditing = IsEditing;
		if (_points.Count == 0 && !Enabled && !wasEditing) return;
		_points.Clear();
		_draftPoints.Clear();
		Enabled = false;
		IsEditing = false;
		Save();
		Changed?.Invoke();
		if (wasEditing) EditingChanged?.Invoke(false);
	}

	private static bool SamePoint(Vector2 left, Vector2 right) => left.IsEqualApprox(right);
	private static bool HasDistinctStops(IReadOnlyList<Vector2> points)
	{
		if (points.Count < 2) return false;
		for (var index = 1; index < points.Count; index++)
			if (!SamePoint(points[0], points[index])) return true;
		return false;
	}

	private void Save()
	{
		var config = new ConfigFile();
		config.SetValue(Section, "points", _points.ToArray());
		config.SetValue(Section, "enabled", Enabled);
		var error = config.Save(StoragePath);
		LastSaveSucceeded = error == Error.Ok;
		if (!LastSaveSucceeded) GD.PushWarning($"Could not save patrol route to {StoragePath}: {error}");
	}

	private void Load()
	{
		var config = new ConfigFile();
		if (config.Load(StoragePath) != Error.Ok) return;
		var value = config.GetValue(Section, "points", Array.Empty<Vector2>());
		if (value.VariantType == Variant.Type.PackedVector2Array)
		{
			foreach (var point in value.AsVector2Array())
			{
				AppendLoadedPoint(point);
				if (_points.Count == MaxPoints) break;
			}
		}
		else if (value.VariantType == Variant.Type.Array)
		{
			foreach (var item in value.AsGodotArray())
			{
				if (item.VariantType == Variant.Type.Vector2) AppendLoadedPoint(item.AsVector2());
				if (_points.Count == MaxPoints) break;
			}
		}
		var enabled = config.GetValue(Section, "enabled", false);
		Enabled = enabled.VariantType == Variant.Type.Bool && enabled.AsBool() && HasDistinctStops(_points);
		if (_points.Count > 0) Changed?.Invoke();
	}

	private void AppendLoadedPoint(Vector2 point)
	{
		if (!point.IsFinite()) return;
		point = point.Clamp(Vector2.Zero, Vector2.One);
		if (_points.Count == 0 || !SamePoint(_points[^1], point)) _points.Add(point);
	}
}
