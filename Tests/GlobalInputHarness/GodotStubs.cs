namespace Godot;

public class Node
{
	public enum ProcessModeEnum { Always }
	public ProcessModeEnum ProcessMode { get; set; }
	public bool Processing { get; private set; }
	public virtual void _Ready() { }
	public virtual void _Process(double delta) { }
	public virtual void _ExitTree() { }
	public void SetProcess(bool enabled) => Processing = enabled;
}

public static class Engine
{
	public static bool EditorHint { get; set; }
	public static bool IsEditorHint() => EditorHint;
}

public static class DisplayServer
{
	public static string Name { get; set; } = "headless";
	public static string GetName() => Name;
}
