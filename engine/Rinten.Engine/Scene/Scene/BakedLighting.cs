using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Rinten;

//The settings are kept in the scene's properties, as its navmesh's are, and what the bake made in assets of its own
/// <summary>The scene's baked lighting: its lightmaps and its envmap probes, how each bakes and the asset each is kept in.</summary>
[Expose]
public sealed class BakedLighting
{
	internal BakedLighting( Scene scene )
	{
		Scene = scene;
		Lightmaps = new LightmapSettings( this );
		Envmaps = new EnvmapSettings();
	}

	/// <summary>The scene this lights.</summary>
	[Hide]
	public Scene Scene { get; }

	/// <summary>The light on the scene's still surfaces, and the volume that lights what moves.</summary>
	[Hide]
	public LightmapSettings Lightmaps { get; }

	/// <summary>What the scene's Baked envmap probes reflect.</summary>
	[Hide]
	public EnvmapSettings Envmaps { get; }

	/// <summary>Whether the frame reads the lightmaps; off draws the scene as if it had none.</summary>
	[Hide]
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

	/// <summary>The lightmap bake the scene reads: its lightmap asset's, null where it has none.</summary>
	[Hide]
	public BakedLightingData Data => Lightmaps.Asset?.Data;

	/// <summary>Whether there is a lightmap bake to read, as against a wish for one.</summary>
	[Hide]
	public bool HasBake => Data is { IsEmpty: false };

	//The bake whose atlas squares the renderers hold - see CoreBakedLighting.Place
	internal BakedLightingData Placed;

	/// <summary>The scene's lightmaps and envmap probes, as far as each is switched on: lightmaps first, so the probes
	/// reflect them. In the editor each is written to its asset - one beside the scene, named after it, where it had none.</summary>
	public async Task Bake( CancellationToken cancel = default )
	{
		if ( !Scene.IsValid() ) return;

		if ( Lightmaps.Bake )
		{
			var made = await CoreBakedLighting.Bake( this, cancel );
			if ( cancel.IsCancellationRequested ) return;

			if ( made is not null )
			{
				var lightmap = Lightmaps.Asset ?? new BakedLightmap();
				lightmap.Data = made;
				Lightmaps.Asset = lightmap;
				Keep( lightmap, "lightmap" );
			}
		}

		if ( Envmaps.Bake ) await BakeEnvmaps( cancel );
	}

	//Every Baked probe drawn as the scene stands now, into the envmaps asset
	private async Task BakeEnvmaps( CancellationToken cancel )
	{
		var probes = Scene.GetAllComponents<EnvmapProbe>()
			.Where( x => x.IsValid() && x.Enabled && x.Mode == EnvmapProbe.EnvmapProbeMode.Baked )
			.ToList();

		using var section = Application.Editor?.ProgressSection();

		if ( section is not null )
		{
			section.Icon = "panorama_photosphere";
			section.Title = "Baking envmaps";
			section.TotalCount = Math.Max( probes.Count, 1 );
		}

		var data = new BakedEnvmapsData { Side = CoreEnvmap.Side, Bands = CoreEnvmap.Bands };

		foreach ( var probe in probes )
		{
			if ( cancel.IsCancellationRequested || ( section?.GetCancel().IsCancellationRequested ?? false ) ) return;

			//A tick between probes, so the editor draws, and none while a runtime probe's capture has the views
			do await GameTask.Yield();
			while ( !CoreEnvmap.MayCapture && !cancel.IsCancellationRequested );

			if ( CoreEnvmap.Take( Scene, probe ) is { } pixels )
				data.Pictures.Add( new BakedEnvmapsData.Picture { Probe = probe.GameObject.Id, Pixels = pixels } );
			else
				Log.Warning( $"baked lighting: the envmap probe on {probe.GameObject.Name} did not capture" );

			if ( section is not null ) section.Current = data.Pictures.Count;
		}

		var envmaps = Envmaps.Asset ?? new BakedEnvmaps();
		envmaps.Data = data;
		Envmaps.Asset = envmaps;
		Keep( envmaps, "envmaps" );

		Log.Info( $"baked lighting: {data.Pictures.Count} of {probes.Count} envmap probes" );
	}

	//Into its own asset, or a new one beside the scene; a bake of a scene not being edited lives as long as the scene
	private void Keep( GameResource asset, string extension )
	{
		if ( Scene.Editor is not { } session || Application.Editor is not { } editor ) return;

		var path = asset.ResourcePath;

		if ( string.IsNullOrEmpty( path ) )
		{
			if ( Scene.Source?.ResourcePath is not { Length: > 0 } scene )
			{
				Log.Warning( "baked lighting: the scene has no file yet, so the bake has nowhere to go - save the scene and bake again" );
				return;
			}

			path = System.IO.Path.ChangeExtension( scene, extension );
			session.HasUnsavedChanges = true;
		}

		if ( editor.SaveResource( asset, path ) is null )
			Log.Warning( $"baked lighting: could not write {path}" );
	}

	internal JsonObject Serialize() => new()
	{
		["Enabled"] = Enabled,
		["Lightmaps"] = Write( Lightmaps ),
		["Envmaps"] = Write( Envmaps ),
	};

	internal void Deserialize( JsonObject json )
	{
		if ( json is null ) return;

		if ( json["Enabled"] is JsonValue enabled ) Enabled = enabled.GetValue<bool>();

		Read( Lightmaps, json["Lightmaps"] as JsonObject );
		Read( Envmaps, json["Envmaps"] as JsonObject );
	}

	//Every property of one block, an asset by its path
	private static JsonObject Write( object block )
	{
		var json = new JsonObject();

		foreach ( var property in Properties( block ) )
			json[property.Name] = Json.ToNode( property.GetValue( block ), property.PropertyType );

		return json;
	}

	private static void Read( object block, JsonObject json )
	{
		if ( json is null ) return;

		foreach ( var property in Properties( block ) )
		{
			if ( !json.TryGetPropertyValue( property.Name, out var node ) ) continue;

			try
			{
				property.SetValue( block, Json.FromNode( node, property.PropertyType ) );
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"baked lighting: could not read {property.Name} ({e.Message})" );
			}
		}
	}

	private static IEnumerable<PropertyDescription> Properties( object block )
		=> Game.TypeLibrary.GetType( block.GetType() ).Properties.Where( x => x.HasAttribute<PropertyAttribute>() && x.CanWrite );

	//Off the card and off the renderers, for a scene being emptied or destroyed
	internal void Release() => CoreBakedLighting.Unplace( this );

	/// <summary>How the lightmaps bake, and the asset they are kept in.</summary>
	[Expose]
	public sealed class LightmapSettings
	{
		private readonly BakedLighting owner;

		internal LightmapSettings( BakedLighting owner ) => this.owner = owner;

		/// <summary>The sizes an atlas can be, which are what the baker packs to.</summary>
		public enum Sizes
		{
			[Title( "512" )] Size512 = 512,
			[Title( "1024" )] Size1024 = 1024,
			[Title( "2048" )] Size2048 = 2048,
			[Title( "4096" )] Size4096 = 4096,
			[Title( "8192" )] Size8192 = 8192,
		}

		/// <summary>Whether the scene's bake bakes them.</summary>
		[Property, Hide]
		public bool Bake { get; set; } = true;

		/// <summary>The widest the atlas may be; a scene needing more at its density gets less, evenly.</summary>
		[Property, Order( -3 ), EnumButtonGroup]
		public Sizes Resolution { get; set; } = Sizes.Size2048;

		/// <summary>The asset the lightmaps are kept in and read from; a bake with none makes one beside the scene.</summary>
		[Property, Order( -1 ), Title( "Lightmap" )]
		public BakedLightmap Asset
		{
			get;
			set
			{
				if ( ReferenceEquals( field, value ) ) return;

				CoreBakedLighting.Unplace( owner );
				field = value;
			}
		}

		/// <summary>How many texels of the atlas a metre of surface gets.</summary>
		[Property, Group( "Lightmap Detail", StartFolded = true ), Order( 0 ), Range( 1, 64 ), Title( "Texels Per Metre" )]
		public float TexelsPerMetre { get; set; } = 10.0f;

		/// <summary>How often light may bounce, nought for direct only.</summary>
		[Property, Group( "Lightmap Detail", StartFolded = true ), Order( 0 ), Range( 0, 16 )]
		public int Bounces { get; set; } = 4;

		/// <summary>How much light surfaces throw back against what their colour says; above one is a cheat.</summary>
		[Property, Group( "Lightmap Detail", StartFolded = true ), Order( 0 ), Range( 0, 2 )]
		public float BounceStrength { get; set; } = 1.0f;

		/// <summary>How hard the filter smooths the trace's noise, nought for none; sharp shadows stay sharp.</summary>
		[Property, Group( "Lightmap Detail", StartFolded = true ), Order( 0 ), Range( 0, 8 )]
		public float Denoise { get; set; } = 3.0f;

		/// <summary>How far apart the points of the volume that lights moving things stand, in metres.</summary>
		[Property, Group( "Light Volume", StartFolded = true ), Order( 1 ), Range( 0.25f, 8.0f )]
		public float VolumeSpacing { get; set; } = 1.0f;

		/// <summary>The sky's light against the frame's on an open surface; at one, an open field bakes as drawn.</summary>
		[Property, Group( "Sky and Sun", StartFolded = true ), Order( 2 ), Range( 0, 4 )]
		public float SkyStrength { get; set; } = 1.0f;

		/// <summary>The sun's width in degrees (the real one is half), which sets how soft its baked shadow is.</summary>
		[Property, Group( "Sky and Sun", StartFolded = true ), Order( 2 ), Range( 0, 10 )]
		public float SunWidth { get; set; } = 0.53f;

		//Paths a texel traces and rays a volume point sends
		internal const int TexelSamples = 512;
		internal const int VolumeSamples = 1024;
	}

	/// <summary>Whether the Baked envmap probes bake, and the asset they are kept in.</summary>
	[Expose]
	public sealed class EnvmapSettings
	{
		internal EnvmapSettings()
		{
		}

		/// <summary>Whether the scene's bake takes them.</summary>
		[Property, Hide]
		public bool Bake { get; set; } = true;

		/// <summary>The asset the probes' pictures are kept in and read from; a bake with none makes one beside the scene.</summary>
		[Property, Title( "Envmaps" )]
		public BakedEnvmaps Asset { get; set; }
	}
}
