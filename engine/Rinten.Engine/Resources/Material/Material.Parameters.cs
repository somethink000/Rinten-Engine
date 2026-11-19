namespace Rinten;

public partial class Material
{
	/// <summary>Gets the underlying shader name for this material.</summary>
	public string ShaderName => core.Shader;

	//Asks the shader; true for the standard surface and for shaders that fail to compile
	/// <summary>Whether the surface drawing this material multiplies its colour by the mesh's own per-corner tint, so that two faces of one material can be two colours without being two materials.</summary>
	public bool TintsPerVertex => CoreResources.TintsPerVertex( ShaderName );

	/// <summary>Get thje first texture assigned to this material, if any.</summary>
	public Texture FirstTexture => core.FirstTexture;

	//The loaded material at this path if something was set on it at runtime, else null
	internal static Material Edited( string path )
	{
		//Nothing anywhere has been set on: the ordinary case, and the answer without a lookup or a path
		//to normalise. A draw asks this per part per frame, so the cost of asking has to be a comparison.
		if ( edits == 0 ) return null;

		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		//Only a path somebody has set something on is looked up at all. Asked for every part of every instance on a frame where any material
		//was set on, the lookup of the rest went through the resource system and made a string each time - see CoreSceneTables.EditedMark.
		lock ( keys )
		{
			if ( !editedKeys.Contains( LoadedKey( path ) ) ) return null;
		}

		var found = Loaded( path );

		return found?.core is { Edited: true } ? found : null;
	}

	//The paths of the materials something has been set on - see Edited.
	private static readonly HashSet<string> editedKeys = new( StringComparer.Ordinal );

	//This material has been set on: drawn from what it now says, and found by Edited.
	private void MarkEdited()
	{
		if ( core.Edited ) return;

		core.Edited = true;

		if ( string.IsNullOrWhiteSpace( Name ) ) return;

		lock ( keys ) editedKeys.Add( LoadedKey( Name ) );
	}

	//How many times anything has been set on any material.
	private static int edits;

	//How many times anything has been set on any material - which the scene's tables watch to
	//know when a material being driven from code has moved, see CoreSceneTables.EditedMark.
	internal static int EditCount => edits;

	//How many materials something has set values on - grows only when one is set on for the first time
	internal static int EditedMaterials
	{
		get { lock ( keys ) return editedKeys.Count; }
	}

	/// <summary>Get texture parameter, by name</summary>
	public Texture GetTexture( string name ) => core.Texture( name );

	/// <summary>Get Vector4 parameter, by name</summary>
	public Vector4 GetVector4( string name ) => core.Value( name );

	/// <summary>A number, or the fallback (shader's default) when the material never set it</summary>
	public float GetFloat( string name, float fallback ) => core.Value( name, fallback );

	/// <summary>Get Color parameter, by name</summary>
	public Color GetColor( string name ) => GetVector4( name );

	/// <summary>Overrides/Sets an Vector4 within the material</summary>
	public unsafe bool Set( string param, Vector4 value )
	{
		core.Values[param] = value;

		//The first value set makes it a material drawn live, which the scene has to go and find - see Edited and CoreSceneTables.EditedMark.
		if ( !core.Edited )
		{
			MarkEdited();
			core.Revision++;
			edits++;
			return true;
		}

		//After that a number is only a number: the GPU material keeps its handle and the new numbers
		//are written into it before the next frame is drawn, with nothing else to find again.
		core.ValuesRevision++;
		CoreResources.NumbersChanged( this );
		return true;
	}

	/// <summary>Override/Sets texture parameter (Color, Normal, etc)</summary>
	public unsafe bool Set( string param, Texture texture )
	{
		if ( texture == null || !texture.IsValid ) return false;

		//Legacy support for old style param names - 16/10/2025 Newer games should be putting the exact param name in
		if ( Application.GamePackage?.ApiVersion < 22 && !param.StartsWith( "g_t" ) )
		{
			param = $"g_t{param}";
		}

			texture.UsedOutsidePanels = true;

			//The same picture again changes nothing - a game that sets its target every frame would otherwise rebuild it every frame
			if ( core.Edited && core.Textures.TryGetValue( param, out var had ) && ReferenceEquals( had, texture ) ) return true;

			core.Textures[param] = texture;
			MarkEdited();
			core.Revision++;
			edits++;
			return true;
	}

	/// <summary>Overrides/Sets an color within the material as a color value within the material</summary>
	public bool Set( string param, Color value ) => Set( param, new Vector4( value ) );

	/// <summary>Overrides/Sets an Vector3 within the material</summary>
	public bool Set( string param, Vector3 value ) => Set( param, new Vector4( value, 0.0f ) );

	/// <summary>Overrides/Sets an Vector2 within the material</summary>
	public bool Set( string param, Vector2 value ) => Set( param, new Vector4( value.x, value.y, 0.0f, 0.0f ) );

	/// <summary>Overrides/Sets an float within the material</summary>
	public bool Set( string param, float value ) => Set( param, new Vector4( value, 0.0f, 0.0f, 0.0f ) );

	/// <summary>Overrides/Sets an int within the material</summary>
	public bool Set( string param, int value ) => Set( param, new Vector4( value, 0.0f, 0.0f, 0.0f ) );

	/// <summary>Overrides/Sets an bool within the material</summary>
	public bool Set( string param, bool value ) => Set( param, new Vector4( value ? 1.0f : 0.0f, 0.0f, 0.0f, 0.0f ) );
}
