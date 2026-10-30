namespace Rinten.Rendering;

/// <summary>A distance from the camera the editor can also set by clicking the point in the scene.</summary>
[AttributeUsage( AttributeTargets.Property )]
public class FocusPickerAttribute : EditorAttribute
{
	public FocusPickerAttribute()
	{
		Value = "focuspicker";
	}
}
