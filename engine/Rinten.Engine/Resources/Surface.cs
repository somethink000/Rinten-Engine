using NativeEngine;
using Rinten.Engine;
using System.Text.Json.Serialization;

namespace Rinten;

/// <summary>A physics surface. This is applied to each <see cref="PhysicsShape">PhysicsShape</see> and controls its physical properties and physics related sounds.</summary>
[AssetType( Name = "Surface Description", Extension = "surface", Category = "Physics", Flags = AssetTypeFlags.NoEmbedding, IconColor = "#4596ec", Icon = "texture" )]
public partial class Surface : GameResource
{
	//Per-context lookup of loaded surfaces by their physics index. Each GlobalContext (Menu, Game)
	//owns its own dictionary so that game-session teardown never clears entries belonging to the menu.
	internal static Dictionary<int, Surface> All => GlobalContext.Current.Surfaces;

	[Hide]
	[JsonIgnore]
	public uint NameHash { get; internal set; }

	[Hide]
	[JsonIgnore]
	public int Index { get; internal set; }

	//Use the full mount path to avoid native surface name collisions between games.
	internal string PhysicsName => Mounting.MountUtility.IsMountPath( ResourcePath ) ? ResourcePath : ResourceName;

	[ResourceType( "surface" ), Description( "Fallback surface for empty fields on this surface." )]
	public string BaseSurface { get; set; }

	/// <summary>Defines the audio properties of this surface for Steam Audio</summary>
	public AudioSurface AudioSurface { get; set; }

	/// <summary>A concise description explaining what this surface property should be used for.</summary>
	public string Description { get; set; }

	/// <summary>Friction of this surface material.</summary>
	[Range( 0, 1 ), Category( "Physics" ), DefaultValue( 0.8f )]
	public float Friction { get; set; } = 0.8f;

	/// <summary>Controls bounciness.</summary>
	[Range( 0, 1 ), Category( "Physics" ), DefaultValue( 0.25f )]
	public float Elasticity { get; set; } = 0.25f;

	/// <summary>Density of this surface material. This affects things like automatic mass calculation. Density is in kg/m^3.</summary>
	[Range( 0, 4000 ), Category( "Physics" ), DefaultValue( 2000.0f )]
	public float Density { get; set; } = 2000;

	/// <summary>Controls how easily rolling shapes (sphere, capsule) roll on surfaces.</summary>
	[Range( 0, 1 ), Category( "Physics" ), DefaultValue( 0.0f )]
	public float RollingResistance { get; set; } = 0;

	/// <summary>Velocity threshold, below which objects will not bounce due to their elasticity.</summary>
	[Range( 0, 100 ), Category( "Physics" ), DefaultValue( 40.0f )]
	public float BounceThreshold { get; set; } = 40;

	/// <summary>Linear drag applied when submerged.</summary>
	[Category( "Fluid" ), Title( "Linear Drag" ), Range( 0, 20 ), DefaultValue( 0.1f )]
	public float FluidLinearDrag { get; set; } = 0.1f;

	/// <summary>Angular drag applied when submerged.</summary>
	[Category( "Fluid" ), Title( "Angular Drag" ), Range( 0, 20 ), DefaultValue( 0.1f )]
	public float FluidAngularDrag { get; set; } = 0.1f;

	/// <summary>Returns the base surface of this surface, or null if we are the default surface.</summary>
	public Surface GetBaseSurface()
	{
		var baseSurf = All.Where( x => x.Value.ResourcePath == BaseSurface && x.Value != this ).FirstOrDefault().Value;
		if ( baseSurf == null && Index != 0 ) return All[0]; //If not found, fallback to default unless we are the default.
		return baseSurf;
	}

	/// <summary>Sets the base surface by name.</summary>
	public void SetBaseSurface( string name )
	{
		BaseSurface = All.Where( x => x.Value.ResourceName == name ).FirstOrDefault().Value.ResourcePath;
	}

	protected override void PostLoad()
	{
		Create();
	}

	protected override void PostReload()
	{
		Create( true );
	}

	protected override void OnDestroy()
	{
		if ( All.TryGetValue( Index, out var v ) && v == this )
		{
			All.Remove( Index );
		}
	}

	void Create( bool reload = false )
	{
		//Physics reads friction and the rest off this object, so the surface table is only a way to find
		//a surface again by index; we keep it ourselves.
		if ( !reload )
		{
			NameHash = Hash( PhysicsName ?? string.Empty );

			//The first surface loaded is index nought, which is what everything that asks for "the default one" gets handed.
			Index = All.Count == 0 ? 0 : nextIndex++;
		}

		//Get tags string and create a HashSet full of string tokens
		Tags?.Split( ' ' )
			.ToList()
			.ForEach( x => TagList.Add( StringToken.FindOrCreate( x ) ) );

		All[Index] = this;
	}

	//Where the next surface of ours goes in the table.
	private static int nextIndex = 1;

	//The same hash their physics used for a surface name: FNV-1a over the lowercased
	//name, so a name saved by one and looked up by the other still finds it.
	private static uint Hash( string name )
	{
		uint hash = 2166136261;

		foreach ( var c in name )
		{
			hash ^= char.ToLowerInvariant( c );
			hash *= 16777619;
		}

		return hash;
	}

	static readonly Surface _defaultSurface = new();

	//Fastest lookup by array index; the index can differ between loads, so never save or network it
	internal static Surface FindByIndex( int index )
	{
		if ( All.TryGetValue( index, out var val ) )
			return val;

		foreach ( var v in All )
		{
			return v.Value;
		}

		return _defaultSurface;
	}

	/// <summary>Returns a Surface from its name, or null</summary>
	public static Surface FindByName( string name )
	{
		return All.FirstOrDefault( x => x.Value.PhysicsName == name ).Value
			?? (Mounting.MountUtility.IsMountPath( name ) ? GameResource.Load<Surface>( name ) : null);
	}

	protected override Bitmap CreateAssetTypeIcon( int width, int height )
	{
		return CreateSimpleAssetTypeIcon( "airline_stops", width, height, "#4596ec", "#a9c9ec" );
	}
}

/// <summary>Defines acoustic properties of a surface, which defines how sound will bounce</summary>
[Expose]
public enum AudioSurface
{
	Generic,
	Brick,
	Concrete,
	Ceramic,
	Gravel,
	Carpet,
	Glass,
	Plaster,
	Wood,
	Metal,
	Rock,
	Fabric,
	Foam,
	Sand,
	Snow,
	Soil,
	Curtain,
	Steel,
	AcousticTile,
	Leather,
	Linoleum,
	Asphalt,
	Water,
	Marble,
	Paper
}
