using System;

namespace Editor.Materials;

/// <summary>The material being edited, as everything in the editor sees it.</summary>
public interface IMaterialDocument
{
	/// <summary>The file open here, or null while there is none.</summary>
	Asset Asset { get; }

	/// <summary>The definition being edited, or null while there is none.</summary>
	MaterialDefinition Definition { get; }

	/// <summary>The definition as the inspector's controls edit it.</summary>
	SerializedObject Serialized { get; }

	/// <summary>Whether there is a material here at all.</summary>
	bool IsOpen { get; }

	/// <summary>Whether the file can be compiled again from here.</summary>
	bool CanRecompile { get; }

	/// <summary>Whether there is a version to go back to.</summary>
	bool CanStepBack { get; }

	/// <summary>A material was opened, or the one open was put back to an earlier version: whatever is showing it starts again from the definition.</summary>
	event Action Opened;

	/// <summary>The material was edited, and what kind of thing the edit touched.</summary>
	event Action<MaterialEditKind> Changed;

	/// <summary>Say the definition was written to directly - a feature, a number the shader invented - rather than through one of its own properties.</summary>
	void NoteEdited( MaterialEditKind kind );

	/// <summary>Compile the file again from scratch.</summary>
	void Recompile();

	/// <summary>Show the file in the operating system's file browser.</summary>
	void Reveal();

	/// <summary>Put the material back the way it was before the last edit.</summary>
	void StepBack();
}

/// <summary>Material ResourceDocument shared by all views, reporting what each edit touched</summary>
public sealed class MaterialDocument : ResourceDocument<MaterialDefinition>, IMaterialDocument
{
	/// <summary>A document with nothing in it, for a view that has no file yet - an editor opened from the Apps menu shows this until a material is picked.</summary>
	public static MaterialDocument Empty { get; } = new();

	public MaterialDefinition Definition => Resource;

	public event Action<MaterialEditKind> Changed;

	protected override void OnPropertyChanged( SerializedProperty property )
		=> Changed?.Invoke( MaterialProperties.KindOf( property.Name ) );

	public void NoteEdited( MaterialEditKind kind )
	{
		if ( !IsOpen ) return;

		MarkEdited();
		Changed?.Invoke( kind );
	}
}
