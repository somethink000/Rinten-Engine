using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Rinten;

//The settings are kept in the scene's properties, as its navmesh's are, and the bake in its own asset - see Scene.Lighting
/// <summary>The scene's baked lighting: its settings and the lightmap it reads.</summary>
[Expose]
public sealed class BakedLighting
{
	internal BakedLighting( Scene scene ) => Scene = scene;

	/// <summary>The scene this lights.</summary>
	[Hide]
	public Scene Scene { get; }

	/// <summary>How many texels of the atlas a metre of surface gets.</summary>
	[Property, Group( "Quality" ), Order( 0 ), Range( 1, 64 ), Title( "Texels Per Metre" )]
	public float TexelsPerMetre { get; set; } = 16.0f;

	/// <summary>Paths traced a texel. Noise goes as one over its square root; the filter takes what is left.</summary>
	[Property, Group( "Quality" ), Order( 0 ), Range( 16, 16384 )]
	public int Samples { get; set; } = 512;

	/// <summary>How often light may bounce, nought for direct only; past two, a path goes on as it carries light.</summary>
	[Property, Group( "Quality" ), Order( 0 ), Range( 0, 16 )]
	public int Bounces { get; set; } = 6;

	/// <summary>How far apart the points of the volume that lights moving things stand, in metres.</summary>
	[Property, Group( "Volume" ), Order( 1 ), Range( 0.25f, 8.0f )]
	public float VolumeSpacing { get; set; } = 1.0f;

	/// <summary>The widest the atlas may be; a scene needing more at that density gets less, evenly.</summary>
	[Property, Group( "Quality" ), Order( 0 ), Range( 256, 8192 ), Step( 256 )]
	public int MaxResolution { get; set; } = 4096;

	/// <summary>How hard the filter smooths the trace's noise, nought for none; sharp shadows stay sharp.</summary>
	[Property, Group( "Quality" ), Order( 0 ), Range( 0, 8 )]
	public float Denoise { get; set; } = 3.0f;

	/// <summary>Rays a point of the volume sends, spread evenly over every direction.</summary>
	[Property, Group( "Volume" ), Order( 1 ), Range( 64, 4096 )]
	public int VolumeSamples { get; set; } = 1024;

	/// <summary>The sky's light against the frame's on an open surface; at one, an open field bakes as drawn.</summary>
	[Property, Group( "Light" ), Order( 2 ), Range( 0, 4 )]
	public float SkyStrength { get; set; } = 1.0f;

	/// <summary>How much light surfaces throw back against what their colour says; above one is a cheat.</summary>
	[Property, Group( "Light" ), Order( 2 ), Range( 0, 2 )]
	public float BounceStrength { get; set; } = 1.0f;

	/// <summary>The sun's width in degrees (the real one is half), which sets how soft its baked shadow is.</summary>
	[Property, Group( "Light" ), Order( 2 ), Range( 0, 10 )]
	public float SunWidth { get; set; } = 0.53f;

	/// <summary>Whether the frame reads the bake; off draws the scene as if it had none.</summary>
	[Property, Hide]
	public bool Enabled
	{
		get;
		set
		{
			if ( field == value ) return;

			CoreBakedLighting.Unplace( this );
			field = value;
		}
	} = true;

	/// <summary>The asset the bake is kept in and read from; a bake with none makes one beside the scene.</summary>
	[Property, Order( -1 )]
	public BakedLightmap Lightmap
	{
		get;
		set
		{
			if ( ReferenceEquals( field, value ) ) return;

			CoreBakedLighting.Unplace( this );
			field = value;
		}
	}

	/// <summary>The bake the scene reads: its lightmap's, null where it has none.</summary>
	[Hide]
	public BakedLightingData Data => Lightmap?.Data;

	/// <summary>Whether there is a bake to read, as against a wish for one.</summary>
	[Hide]
	public bool HasBake => Data is { IsEmpty: false };

	//The bake whose atlas squares the renderers hold - see CoreBakedLighting.Place
	internal BakedLightingData Placed;

	/// <summary>Traces the scene's light into its lightmap, off the main thread, reporting progress. In the editor the
	/// lightmap is written to its asset - one beside the scene, named after it, where it had none.</summary>
	public async Task Bake( CancellationToken cancel = default )
	{
		if ( !Scene.IsValid() ) return;

		var made = await CoreBakedLighting.Bake( this, cancel );

		if ( made is null ) return;

		var lightmap = Lightmap ?? new BakedLightmap();
		lightmap.Data = made;

		if ( Lightmap is null )
		{
			Lightmap = lightmap;
			if ( Scene.Editor is { } session ) session.HasUnsavedChanges = true;
		}

		Keep( lightmap );
	}

	//Into its own asset, or a new one beside the scene; a bake of a scene not being edited lives as long as the scene
	private void Keep( BakedLightmap lightmap )
	{
		if ( Scene.Editor is null || Application.Editor is not { } editor ) return;

		var path = lightmap.ResourcePath;

		if ( string.IsNullOrEmpty( path ) )
		{
			if ( Scene.Source?.ResourcePath is not { Length: > 0 } scene )
			{
				Log.Warning( "baked lighting: the scene has no file yet, so the bake has nowhere to go - save the scene and bake again" );
				return;
			}

			path = System.IO.Path.ChangeExtension( scene, "lightmap" );
		}

		if ( editor.SaveResource( lightmap, path ) is null )
			Log.Warning( $"baked lighting: could not write {path}" );
	}

	/// <summary>Stops reading the bake: every light back in the frame, every surface back on the frame's sky. The
	/// lightmap's asset is left as it is.</summary>
	public void Clear()
	{
		Lightmap = null;

		if ( Scene.Editor is { } editor ) editor.HasUnsavedChanges = true;
	}

	//Every property, the lightmap by its path
	internal JsonObject Serialize()
	{
		var json = new JsonObject();

		foreach ( var property in Properties() )
			json[property.Name] = Json.ToNode( property.GetValue( this ), property.PropertyType );

		return json;
	}

	internal void Deserialize( JsonObject json )
	{
		if ( json is null ) return;

		foreach ( var property in Properties() )
		{
			if ( !json.TryGetPropertyValue( property.Name, out var node ) ) continue;

			try
			{
				property.SetValue( this, Json.FromNode( node, property.PropertyType ) );
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"baked lighting: could not read {property.Name} ({e.Message})" );
			}
		}
	}

	//Off the card and off the renderers, for a scene being emptied or destroyed
	internal void Release() => CoreBakedLighting.Unplace( this );

	private static IEnumerable<PropertyDescription> Properties()
		=> Game.TypeLibrary.GetType<BakedLighting>().Properties.Where( x => x.HasAttribute<PropertyAttribute>() && x.CanWrite );
}
