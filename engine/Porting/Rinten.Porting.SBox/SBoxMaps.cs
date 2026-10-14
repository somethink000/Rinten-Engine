using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HalfEdgeMesh;

namespace Rinten.Porting.SBox;

//Converts a Hammer .vmap into a prefab: editable mesh brushwork, props and lights
//Inches convert to metres while building meshes; scenes get it via SBoxMapPlacements
internal static class SBoxMaps
{
	private static readonly Logger log = new( "Porter" );

	/// <summary>Read one map and write the prefab where the port is putting everything else.</summary>
	public static bool Port( string file, string relative, string sourceAssets, string assets, PortReport report )
	{
		SBoxMapDocument document;

		try
		{
			document = SBoxMapDocument.Read( File.ReadAllBytes( file ) );
		}
		catch ( Exception e )
		{
			report.Skipped.Add( $"{relative}: could not be read as a Hammer map - {e.Message}" );
			return false;
		}

		var root = document.Root;
		var world = root?.GetElement( "world" );

		if ( world is null )
		{
			report.Skipped.Add( $"{relative}: has no world in it" );
			return false;
		}

		var build = new Build( relative, file, sourceAssets, report );
		build.Read( root );

		var objects = new JsonArray();
		build.Children( world.GetElements( "children" ), objects, Transform.Zero, hidden: false );

		//The containers a selection set turned into go on the end, so that a set's objects are together
		//at the top of the map rather than wherever in the tree the first one of them happened to be.
		foreach ( var container in build.Sets.Values )
			objects.Add( container );

		//A prefab is one object. The map hangs under it at the origin, so dropping the prefab somewhere
		//puts the whole level where it was dropped, and everything in it keeps the position Hammer gave it.
		var prefab = new JsonObject
		{
			["RootObject"] = build.Root( Path.GetFileNameWithoutExtension( relative ), objects ),
			["ShowInMenu"] = false,
			["ResourceVersion"] = 2,
			["__references"] = new JsonArray(),
			["__version"] = 2,
		};

		var target = Path.Combine( assets, Path.ChangeExtension( relative, ".prefab" ) );

		//Name clash with an existing prefab: skip the map and report it rather than overwrite
		if ( File.Exists( target ) || File.Exists( Path.ChangeExtension( file, ".prefab" ) ) )
		{
			target = Path.Combine( assets, Path.ChangeExtension( relative, null ) + "_map.prefab" );

			report.Skipped.Add( $"{relative}: there is already a prefab of that name, so the level was written " +
				$"as '{Path.GetFileName( target )}' - what named the map points at the other file, not at this" );
		}

		Directory.CreateDirectory( Path.GetDirectoryName( target ) );
		File.WriteAllText( target, prefab.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

		//What this level actually became, for whatever names it afterwards. Not worked out again
		//from the map's name: the collision above is exactly the case where the two are different.
		report.Levels[relative.Replace( '\\', '/' )] =
			Path.GetRelativePath( assets, target ).Replace( '\\', '/' );

		build.Explain();

		log.Info( $"map {relative}: {build.Meshes} mesh(es) and {build.Entities} entity(s) -> " +
			$"{Path.GetRelativePath( assets, target ).Replace( '\\', '/' )}" );

		return true;
	}

	//One map being turned into one prefab, and what it has to say afterwards.
	private sealed class Build
	{
		//How many of ours one of Hammer's is. Their maps are in inches.
		private const float Scale = PortUnits.SBoxUnit;

		//Hammer's axes, Source's: +x forward, +y left, +z up. Kept apart from Scale on purpose - a map that muddles the two is the right size and inside out.
		private static ForeignBasis Axes => ForeignBasis.Quake;

		//A texture axis: a direction in the first three and a shift along it in the fourth. The direction turns; the shift is a distance and does not.
		private static Vector4 TurnAxis( Vector4 axis )
		{
			var turned = Axes.Vector( new Vector3( axis.x, axis.y, axis.z ) );

			return new Vector4( turned.x, turned.y, turned.z, axis.w );
		}

		//Container for nodes hidden in Hammer, kept together so they don't z-fight in place
		private const string HiddenName = "Hidden in Hammer";

		private readonly string relative;
		private readonly string file;
		private readonly string sourceAssets;
		private readonly PortReport report;

		//Selection set per node, keyed by element so binary and text maps work the same
		private readonly Dictionary<SBoxMapElement, Selection> sets = new();

		//Nodes Hammer's visibility manager has switched off.
		private readonly HashSet<SBoxMapElement> concealed = new();

		//Classnames Hammer had that this engine has no component for.
		private readonly Dictionary<string, int> unhandled = new( StringComparer.OrdinalIgnoreCase );

		//Meshes whose faces Hammer had marked to be subdivided.
		private int subdividedMeshes;

		//And of those, the ones whose sculpted shape was read too.
		private int sculptedMeshes;

		//Materials a face named that nothing could load.
		private readonly HashSet<string> lostMaterials = new( StringComparer.OrdinalIgnoreCase );

		//Fallback for missing materials: our dev grid, since their core dev materials don't resolve
		private static Material Untextured => untextured ??= Material.Load( "materials/dev/grid.mat" );

		private static Material untextured;

		//Maps a prefab node points at that are being read right now.
		private readonly HashSet<string> open = new( StringComparer.OrdinalIgnoreCase );

		//Nesting depth inside instances/prefabs; nested objects have relative positions and can't be hoisted
		private int nested;

		public Build( string relative, string file, string sourceAssets, PortReport report )
		{
			this.relative = relative;
			this.file = file;
			this.sourceAssets = sourceAssets;
			this.report = report;
		}

		//A set a node belongs to, and whether Hammer had it switched on.
		private readonly record struct Selection( string Name, bool Enabled );

		public Dictionary<string, JsonObject> Sets { get; } = new( StringComparer.Ordinal );

		public int Meshes { get; private set; }
		public int Entities { get; private set; }

		//At the origin and untransformed, which is the convention a prefab root is written with: where the level ends up is decided by whoever drops it into a scene, not by this file.
		/// <summary>The one object a prefab is, with the whole map under it.</summary>
		public JsonObject Root( string name, JsonArray children )
		{
			var go = Object( name, Transform.Zero );
			go["Children"] = children;

			return go;
		}

		//-- what the map says about itself before any of it is walked ---------

		public void Read( SBoxMapElement root )
		{
			ReadSets( root.GetElement( "rootSelectionSet" ), parentEnabled: true );
			ReadVisibility( root.GetElement( "visbility" ) ?? root.GetElement( "visibility" ) );
		}

		private void ReadSets( SBoxMapElement parent, bool parentEnabled )
		{
			if ( parent is null ) return;

			foreach ( var set in parent.GetElements( "children" ) )
			{
				var name = set.GetString( "selectionSetName" );

				//A set switched off inside one that is on is off; a set inside one that is off cannot be on, whatever it says about itself.
				var enabled = parentEnabled && Enabled( set );

				if ( !string.IsNullOrEmpty( name ) && set.GetElement( "selectionSetData" ) is { } data )
				{
					foreach ( var node in data.GetElements( "selectedObjects" ) )
					{
						//An object in two sets keeps the first, unless the first was hidden and the second is not - a node
						//that only appears switched off in the scene because of a set nobody remembers making is a bug report.
						if ( sets.TryGetValue( node, out var existing ) && existing.Enabled )
							continue;

						sets[node] = new Selection( name, enabled );
					}
				}

				ReadSets( set, enabled );
			}
		}

		private static bool Enabled( SBoxMapElement set )
		{
			foreach ( var on in new[] { "enabled", "visible", "isVisible", "m_bVisible" } )
			{
				if ( set.TryGetBool( on, out var value ) ) return value;
			}

			foreach ( var off in new[] { "hidden", "isHidden", "m_bHidden" } )
			{
				if ( set.TryGetBool( off, out var value ) ) return !value;
			}

			return true;
		}

		//Hammer keeps what is hidden in one list beside the tree rather than on the nodes: every node it knows about, and a flag each, in step.
		private void ReadVisibility( SBoxMapElement visibility )
		{
			if ( visibility is null ) return;

			var nodes = visibility.GetElements( "nodes" );
			var flags = visibility.GetInts( "hiddenFlags" );

			for ( int i = 0; i < nodes.Count && i < flags.Length; i++ )
			{
				if ( flags[i] != 0 ) concealed.Add( nodes[i] );
			}
		}

		//-- the tree ----------------------------------------------------------

		/// <summary>Walk one level of Hammer's tree into one level of the object tree.</summary>
		public void Children( List<SBoxMapElement> children, JsonArray into, Transform inherited, bool hidden )
		{
			//What a CMapInstance points at is written into the tree as well as being pointed at, and placing
			//it both times leaves a copy of every instanced room sitting where the original was drawn.
			var instanced = new HashSet<SBoxMapElement>();

			foreach ( var child in children )
			{
				if ( child.TypeName != "CMapInstance" ) continue;
				if ( child.GetElement( "target" ) is { } target ) instanced.Add( target );
			}

			foreach ( var child in children )
			{
				if ( instanced.Contains( child ) ) continue;

				var isHidden = hidden || concealed.Contains( child )
					|| child.GetBool( "force_hidden" ) || child.GetBool( "editorOnly" );

				//Nothing is moved out of an instance or a prefab - see `nested`. What was hidden in there is switched off where it stands.
				var inPlace = nested > 0;
				var target = inPlace ? into : isHidden ? Hidden() : Set( child ) ?? into;

				switch ( child.TypeName )
				{
					case "CMapMesh":
						if ( Mesh( child ) is { } mesh ) { target.Add( Switch( mesh, inPlace && isHidden ) ); ++Meshes; }
						break;

					case "CMapEntity":
						if ( Entity( child ) is { } entity ) { target.Add( Switch( entity, inPlace && isHidden ) ); ++Entities; }
						break;

					case "CMapGroup":
						Group( child, target, inherited, isHidden );
						break;

					case "CMapPrefab":
						Prefab( child, target, inherited, isHidden );
						break;

					case "CMapInstance":
						Instance( child, target, isHidden );
						break;
				}
			}
		}

		//Switches an object off where it could not be moved out of the way.
		private static JsonObject Switch( JsonObject go, bool off )
		{
			if ( off ) go["Enabled"] = false;

			return go;
		}

		private void Group( SBoxMapElement group, JsonArray into, Transform inherited, bool hidden )
		{
			var transform = ReadTransform( group );
			var go = Object( Named( group, "Group" ), Transform.Zero, enabled: !hidden );

			Children( group.GetElements( "children" ), (JsonArray)go["Children"],
				IsIdentity( transform ) ? inherited : transform, hidden );

			into.Add( go );
		}

		//Map instance: container placed at instance minus the rotated original's position
		private void Instance( SBoxMapElement instance, JsonArray into, bool hidden )
		{
			var target = instance.GetElement( "target" );

			if ( target is null )
			{
				report.Skipped.Add( $"{relative}: an instance points at nothing and was dropped" );
				return;
			}

			var placed = ReadTransform( instance );
			var original = ReadTransform( target );

			var rotation = placed.Rotation * original.Rotation.Inverse;
			var position = placed.Position - rotation * original.Position;

			var go = Object( Named( target, "Instance" ), new Transform( position, rotation, placed.Scale ), enabled: !hidden );

			++nested;

			try
			{
				Children( target.GetElements( "children" ), (JsonArray)go["Children"], Transform.Zero, hidden );
			}
			finally
			{
				--nested;
			}

			into.Add( go );
		}

		//Map prefab: flattened inline, since the referenced prefab isn't written yet at this point
		private void Prefab( SBoxMapElement prefab, JsonArray into, Transform inherited, bool hidden )
		{
			var named = prefab.GetString( "targetMapPath" );

			if ( string.IsNullOrWhiteSpace( named ) )
			{
				report.Skipped.Add( $"{relative}: a prefab names no map and was dropped" );
				return;
			}

			var transform = ReadTransform( prefab );

			if ( IsIdentity( transform ) && !IsIdentity( inherited ) )
				transform = inherited;

			var name = prefab.GetString( "targetName" );

			if ( string.IsNullOrWhiteSpace( name ) )
				name = Path.GetFileNameWithoutExtension( named );

			//A prefab that contains itself is a map somebody can save and Hammer will draw until it runs out of stack. Read it once, then stop.
			if ( !open.Add( named ) )
			{
				report.Skipped.Add( $"{relative}: '{named}' contains itself, so the inner copy was dropped" );
				return;
			}

			++nested;

			try
			{
				if ( Nested( named )?.Root?.GetElement( "world" ) is not { } world )
				{
					report.Skipped.Add( $"{relative}: '{named}' could not be read, so that prefab is empty" );
					return;
				}

				var go = Object( name, transform,
					enabled: !hidden && prefab.GetString( "loadIfNested" ) != "0" );

				Children( world.GetElements( "children" ), (JsonArray)go["Children"], Transform.Zero, hidden: false );

				into.Add( go );
			}
			finally
			{
				--nested;
				open.Remove( named );
			}
		}

		//The map a prefab names, found beside the one being read.
		private SBoxMapDocument Nested( string named )
		{
			foreach ( var candidate in Candidates( named.Replace( '\\', '/' ) ) )
			{
				if ( !File.Exists( candidate ) ) continue;

				try
				{
					return SBoxMapDocument.Read( File.ReadAllBytes( candidate ) );
				}
				catch ( Exception e )
				{
					report.Skipped.Add( $"{relative}: '{named}' would not read - {e.Message}" );
					return null;
				}
			}

			return null;
		}

		//Beside the map that names it first, then from the assets root, which is the order Hammer resolves one in.
		private IEnumerable<string> Candidates( string path )
		{
			if ( Path.IsPathRooted( path ) )
			{
				yield return path;
				yield break;
			}

			if ( Path.GetDirectoryName( file ) is { Length: > 0 } here )
				yield return Path.GetFullPath( Path.Combine( here, path ) );

			if ( !string.IsNullOrEmpty( sourceAssets ) )
				yield return Path.GetFullPath( Path.Combine( sourceAssets, path ) );
		}

		//-- meshes ------------------------------------------------------------

		//A CMapMesh, which is the brushwork the level was carved out of.
		private JsonObject Mesh( SBoxMapElement node )
		{
			if ( node.GetElement( "meshData" ) is not { } data ) return null;

			var transform = ReadTransform( node );

			if ( Polygons( data, transform ) is not { } mesh ) return null;

			var go = Object( "Mesh", transform );

			var component = new JsonObject
			{
				["__type"] = "Rinten.MeshComponent",
				["__guid"] = Guid.NewGuid().ToString(),
				["Mesh"] = Json.ToNode( mesh ),
				["Collision"] = Collision( node ),
			};

			if ( node.TryGetFloat( "smoothingAngle", out var smoothing ) )
				component["SmoothingAngle"] = smoothing;

			( (JsonArray)go["Components"] ).Add( component );

			return go;
		}

		//What a brush collides as. Hammer says physicsType , which is theirs, and the nearest thing of ours is what the mesh component builds
		//its collision from. A brush marked as having none keeps none: a trigger volume that arrives solid is a wall in the middle of a room.
		private static string Collision( SBoxMapElement node )
			=> node.GetString( "physicsType", "default" ) switch
			{
				"none" => "None",
				"hull" => "Hull",
				_ => "Mesh",
			};

		private PolygonMesh Polygons( SBoxMapElement data, Transform transform )
		{
			var faceEdges = data.GetInts( "faceEdgeIndices" );
			var edgeVertices = data.GetInts( "edgeVertexIndices" );
			var edgeNext = data.GetInts( "edgeNextIndices" );
			var vertexData = data.GetInts( "vertexDataIndices" );
			var faceData = data.GetInts( "faceDataIndices" );
			var materials = data.GetStrings( "materials" );

			var positions = Stream( data.GetElement( "vertexData" ), "position:0" )?.GetVector3s( "data" );

			if ( faceEdges.Length == 0 || positions is null || positions.Length == 0 ) return null;

			var faces = data.GetElement( "faceData" );
			var materialIndices = Stream( faces, "materialindex:0" )?.GetInts( "data" ) ?? Array.Empty<int>();
			var axisU = Stream( faces, "textureAxisU:0" )?.GetVector4s( "data" ) ?? Array.Empty<Vector4>();
			var axisV = Stream( faces, "textureAxisV:0" )?.GetVector4s( "data" ) ?? Array.Empty<Vector4>();
			var scales = Stream( faces, "textureScale:0" )?.GetVector2s( "data" ) ?? Array.Empty<Vector2>();

			//Displacement data: per-edge depth and sculpt stream, without which terrain comes out flat
			var subdivision = data.GetElement( "subdivisionData" );

			var levels = subdivision?.GetInts( "subdivisionLevels" ) ?? Array.Empty<int>();

			var offsets = Stream( subdivision, "displacement:0" )?.GetVector3s( "data" );

			var places = new List<Vector3>( positions.Length );

			//Vertices and the texture axes below both turn. A change of basis is a rotation, so the dot product that makes a uv
			//out of the two is the number it always was - turning one and not the other is what slides every texture off its wall.
			foreach ( var position in positions ) places.Add( Axes.Vector( position ) * Scale );

			//Read as indices into that list rather than as vertices of a mesh: subdividing makes points, and a point that does not exist yet cannot be a handle.
			var cages = new List<SBoxSubdivision.Cage>( faceEdges.Length );
			var described = new List<(string Material, Vector4 AxisU, Vector4 AxisV, Vector2 Scale)>( faceEdges.Length );

			for ( int i = 0; i < faceEdges.Length; i++ )
			{
				var start = faceEdges[i];

				if ( start < 0 || start >= edgeNext.Length ) continue;

				var corners = Corners( start, edgeVertices, edgeNext, vertexData, out var cornerEdges );

				if ( corners.Length < 3 ) continue;

				var of = i < faceData.Length ? faceData[i] : 0;
				var material = At( materialIndices, of, 0 );

				cages.Add( new SBoxSubdivision.Cage( corners, cornerEdges, Level( start, edgeNext, levels ) ) );

				described.Add( (
					material >= 0 && material < materials.Length ? materials[material] : "",
					TurnAxis( At( axisU, of, new Vector4( 1, 0, 0, 0 ) ) ),
					TurnAxis( At( axisV, of, new Vector4( 0, 1, 0, 0 ) ) ),
					At( scales, of, new Vector2( 0.25f, 0.25f ) ) ) );
			}

			//The offsets are in the mesh's own inches, like everything else it holds, so they are moved into metres before anything is added to them.
			Vector3[] sculpt = null;

			if ( offsets is { Length: > 0 } )
			{
				sculpt = new Vector3[offsets.Length];

				for ( var i = 0; i < offsets.Length; i++ ) sculpt[i] = Axes.Vector( offsets[i] ) * Scale;
			}

			var subdivision2 = SBoxSubdivision.Apply( places, cages, sculpt );
			var subdivided = subdivision2.Faces;

			if ( subdivision2.Trouble is { } trouble )
				report.Skipped.Add( $"{relative}: {trouble}" );

			if ( subdivision2.Sculpted ) ++sculptedMeshes;

			var mesh = new PolygonMesh();

			//Before any face goes on: texture parameters are turned into coordinates against this as each face
			//is added, and a mesh handed its transform afterwards has every uv computed about the origin.
			mesh.SetTransform( transform );

			var vertices = new VertexHandle[places.Count];

			for ( int i = 0; i < places.Count; i++ )
				vertices[i] = mesh.AddVertex( places[i] );

			var pending = new List<Face>( faceEdges.Length );

			for ( int i = 0; i < cages.Count; i++ )
			{
				var (material, u, v, scale) = described[i];

				foreach ( var polygon in subdivided[i] )
				{
					var corners = new VertexHandle[polygon.Length];

					for ( var c = 0; c < polygon.Length; c++ ) corners[c] = vertices[polygon[c]];

					//Every face a subdivided one became keeps the material and the texture axes it was cut from: theirs is a
					//planar projection off the world, so it means the same thing on a piece of the face as on the whole of it.
					pending.Add( new Face( corners, material, u, v, scale ) );
				}
			}

			if ( cages.Count > 0 && pending.Count > cages.Count ) ++subdividedMeshes;

			return Faces( mesh, pending ) ? mesh : null;
		}

		//How many times to subdivide the face this half edge starts, which Hammer writes per half edge rather than per face. The deepest of the loop's,
		//so a face marked unevenly comes out whole rather than partly flat. The same guard as the walk that reads the corners, for the same reason.
		private static int Level( int start, int[] edgeNext, int[] levels )
		{
			if ( levels.Length == 0 ) return 0;

			var deepest = 0;
			var at = start;
			var guard = edgeNext.Length + 1;

			do
			{
				if ( at >= 0 && at < levels.Length && levels[at] > deepest ) deepest = levels[at];

				at = at < edgeNext.Length ? edgeNext[at] : start;
			}
			while ( at != start && --guard > 0 );

			return deepest;
		}

		//Walks a face's half edges to its corners; guarded against loops that never close
		private static int[] Corners( int start, int[] edgeVertices, int[] edgeNext, int[] vertexData,
			out int[] edges )
		{
			var corners = new List<int>( 4 );
			var walked = new List<int>( 4 );
			var at = start;
			var guard = edgeNext.Length + 1;

			do
			{
				var vertex = at < edgeVertices.Length ? edgeVertices[at] : 0;
				var position = vertex >= 0 && vertex < vertexData.Length ? vertexData[vertex] : vertex;

				if ( position >= 0 ) { corners.Add( position ); walked.Add( at ); }

				at = at < edgeNext.Length ? edgeNext[at] : start;
			}
			while ( at != start && --guard > 0 );

			edges = walked.ToArray();

			return corners.ToArray();
		}

		private readonly record struct Face( VertexHandle[] Corners, string Material, Vector4 AxisU, Vector4 AxisV, Vector2 Scale );

		//Adds faces, retrying rejects until a pass adds nothing; order affects winding conflicts
		private bool Faces( PolygonMesh mesh, List<Face> pending )
		{
			var any = false;
			var previous = -1;

			while ( pending.Count > 0 && pending.Count != previous )
			{
				previous = pending.Count;

				var failed = new List<Face>();

				foreach ( var face in pending )
				{
					var handle = mesh.AddFace( face.Corners );

					if ( !handle.IsValid )
					{
						failed.Add( face );
						continue;
					}

					//A face always gets a material, even when the one it names is not here. Left without, it draws as nothing at all - which reads as the
					//map having failed to import rather than as one texture being missing, and is the harder of the two to work out from looking at it.
					if ( !string.IsNullOrEmpty( face.Material ) )
					{
						if ( Material.Load( face.Material ) is { } material )
						{
							mesh.SetFaceMaterial( handle, material );
						}
						else
						{
							lostMaterials.Add( face.Material );

							if ( Untextured is { } grid ) mesh.SetFaceMaterial( handle, grid );
						}
					}
					else if ( Untextured is { } bare )
					{
						mesh.SetFaceMaterial( handle, bare );
					}

					//The axes stay as they are and the scale moves with the world, because a uv is an axis dotted with a world position
					//over the scale. Shrink one and not the other and every texture in the map comes out thirty-nine times too small.
					mesh.SetFaceTextureParameters( handle, face.AxisU, face.AxisV, face.Scale * Scale );

					any = true;
				}

				pending = failed;
			}

			if ( pending.Count > 0 )
			{
				report.Notes.Add( $"{relative}: {pending.Count} face(s) would not go onto their mesh - " +
					"their winding conflicts with a face already on it." );
			}

			return any;
		}

		//One named run of per-vertex or per-face data. Binary puts the name on the element and text
		//puts it in an attribute, and either way it is the semantic and its index with a colon between.
		private static SBoxMapElement Stream( SBoxMapElement data, string name )
		{
			if ( data is null ) return null;

			foreach ( var stream in data.GetElements( "streams" ) )
			{
				if ( StreamName( stream ) == name ) return stream;
			}

			return null;
		}

		private static string StreamName( SBoxMapElement stream )
		{
			if ( !string.IsNullOrEmpty( stream.Name ) ) return stream.Name;

			if ( stream.GetString( "name" ) is { Length: > 0 } named ) return named;

			return $"{stream.GetString( "semanticName" )}:{(int)stream.GetFloat( "semanticIndex" )}";
		}

		//-- entities ----------------------------------------------------------

		//Entity to components by classname; unknown ones become empty named objects and get reported
		private JsonObject Entity( SBoxMapElement entity )
		{
			if ( entity.GetElement( "entity_properties" ) is not { } properties ) return null;

			var classname = properties.GetString( "classname" );

			if ( string.IsNullOrEmpty( classname ) ) return null;

			var components = classname switch
			{
				"prop_static" or "prop_dynamic" or "prop_dynamic_override"
					=> Prop( properties, collides: true, physics: false ),

				"prop_physics" or "prop_physics_override" or "prop_physics_multiplayer"
					=> Prop( properties, collides: true, physics: true ),

				"prop_animated" => Prop( properties, collides: false, physics: false ),

				"light_spot" => Spot( properties ),
				"light_omni" or "light" => Point( properties ),
				"light_environment" or "light_directional" => Directional( properties ),

				"info_player_start" or "info_player_teamspawn" => Spawn(),

				"snd_soundscape" => Soundscape( properties, box: false ),
				"snd_soundscape_box" => Soundscape( properties, box: true ),

				"env_gradient_fog" => Fog( properties ),

				"env_cubemap" or "env_cubemap_box" => Cubemap( properties ),

				_ => null,
			};

			var transform = ReadTransform( entity );

			//Fog is the one whose object moves. Hammer gives the volume a bottom and a top, ours has a bottom
			//and a height, so the bottom is where the object goes rather than wherever the entity was dragged to.
			if ( classname == "env_gradient_fog" && properties.TryGetFloat( "fogstartheight", out var bottom ) )
				transform = transform.WithPosition( transform.Position.WithY( bottom * Scale ) );

			var go = Object( EntityName( properties, classname ), transform );

			if ( components is null )
			{
				unhandled[classname] = unhandled.GetValueOrDefault( classname ) + 1;
				return go;
			}

			go["Components"] = components;

			if ( properties.TryGetBool( "enabled", out var enabled ) && !enabled )
				go["Enabled"] = false;

			return go;
		}

		private static string EntityName( SBoxMapElement properties, string classname )
		{
			if ( properties.GetString( "targetname" ) is { Length: > 0 } named ) return named;

			if ( properties.GetString( "model" ) is { Length: > 0 } model )
				return Path.GetFileNameWithoutExtension( model );

			return classname;
		}

		private static JsonArray Prop( SBoxMapElement properties, bool collides, bool physics )
		{
			var model = properties.GetString( "model" );

			if ( string.IsNullOrEmpty( model ) ) return null;

			var renderer = new JsonObject
			{
				["__type"] = "Rinten.ModelRenderer",
				["__guid"] = Guid.NewGuid().ToString(),
				["Model"] = model,
			};

			if ( properties.GetString( "rendercolor" ) is { Length: > 0 } tint )
				renderer["Tint"] = Json.ToNode( Colour( tint ) );

			if ( properties.GetString( "skin" ) is { Length: > 0 } skin && skin != "0" && skin != "default" )
				renderer["MaterialGroup"] = skin;

			if ( properties.GetString( "materialoverride" ) is { Length: > 0 } material )
				renderer["MaterialOverride"] = material;

			if ( properties.GetBool( "disableshadows" ) )
				renderer["RenderType"] = "Off";

			var components = new JsonArray( renderer );

			//Their `solid` is the keyvalue that says whether anything hits it, and a zero there is a decoration somebody put where a player walks.
			if ( collides && properties.GetString( "solid" ) != "0" )
			{
				components.Add( new JsonObject
				{
					["__type"] = "Rinten.ModelCollider",
					["__guid"] = Guid.NewGuid().ToString(),
					["Model"] = model,
					["Static"] = !physics,
				} );
			}

			if ( !physics ) return components;

			var body = new JsonObject
			{
				["__type"] = "Rinten.Rigidbody",
				["__guid"] = Guid.NewGuid().ToString(),
			};

			if ( properties.GetBool( "Static" ) )
				body["MotionEnabled"] = false;

			//Their massScale multiplies what the model already weighs; ours is the weight
			//itself. Only carried where it is one, which is what a scale of one is not.
			if ( properties.TryGetFloat( "massscale", out var mass ) && mass > 0.0f && mass != 1.0f )
				body["MassOverride"] = mass;

			components.Add( body );

			return components;
		}

		private static JsonArray Spot( SBoxMapElement properties )
		{
			var light = Light( "Rinten.SpotLight", properties );

			if ( properties.TryGetFloat( "range", out var range ) && range > 0.0f )
				light["Radius"] = range * Scale;

			if ( properties.TryGetFloat( "innerconeangle", out var inner ) )
				light["ConeInner"] = inner;

			if ( properties.TryGetFloat( "outerconeangle", out var outer ) )
				light["ConeOuter"] = outer;

			if ( properties.TryGetFloat( "attenuation1", out var attenuation ) )
				light["Attenuation"] = attenuation;

			return new JsonArray( light );
		}

		private static JsonArray Point( SBoxMapElement properties )
		{
			var light = Light( "Rinten.PointLight", properties );

			if ( properties.TryGetFloat( "range", out var range ) && range > 0.0f )
				light["Radius"] = range * Scale;

			return new JsonArray( light );
		}

		private static JsonArray Directional( SBoxMapElement properties )
		{
			var light = Light( "Rinten.DirectionalLight", properties );

			if ( properties.GetString( "skycolor" ) is { Length: > 0 } sky )
			{
				var colour = Colour( sky );

				if ( properties.TryGetFloat( "skyintensity", out var intensity ) )
					colour *= intensity;

				light["SkyColor"] = Json.ToNode( colour );
			}

			var components = new JsonArray( light );

			//Their light_environment carries the ambient term as well. Ours are two components, so
			//the entity becomes both rather than losing the half of it that has nowhere else to go.
			if ( properties.GetString( "ambient_color" ) is { Length: > 0 } ambient )
			{
				components.Add( new JsonObject
				{
					["__type"] = "Rinten.AmbientLight",
					["__guid"] = Guid.NewGuid().ToString(),
					["Color"] = Json.ToNode( Colour( ambient ) ),
				} );
			}

			return components;
		}

		private static JsonObject Light( string type, SBoxMapElement properties )
		{
			var light = new JsonObject
			{
				["__type"] = type,
				["__guid"] = Guid.NewGuid().ToString(),
			};

			var colour = properties.GetString( "color" ) is { Length: > 0 } written ? Colour( written ) : Color.White;

			if ( properties.TryGetFloat( "brightness", out var brightness ) )
				colour *= brightness;

			light["LightColor"] = Json.ToNode( colour );

			if ( properties.TryGetBool( "castshadows", out var shadows ) )
				light["Shadows"] = shadows;

			return light;
		}

		private static JsonArray Spawn()
			=> new( new JsonObject
			{
				["__type"] = "Rinten.SpawnPoint",
				["__guid"] = Guid.NewGuid().ToString(),
			} );

		private static JsonArray Soundscape( SBoxMapElement properties, bool box )
		{
			var named = properties.GetString( "soundscape" );

			if ( string.IsNullOrEmpty( named ) ) return null;

			var trigger = new JsonObject
			{
				["__type"] = "Rinten.SoundscapeTrigger",
				["__guid"] = Guid.NewGuid().ToString(),
				["Soundscape"] = named,
				["Type"] = box ? "Box" : "Sphere",
			};

			if ( box )
			{
				var extents = properties.GetVector3( "extents" );

				if ( !extents.IsNearlyZero() )
					trigger["BoxSize"] = Json.ToNode( Axes.Size( extents ) * Scale );
			}
			else if ( properties.TryGetFloat( "radius", out var radius ) )
			{
				trigger["Radius"] = radius * Scale;
			}

			return new JsonArray( trigger );
		}

		private static JsonArray Fog( SBoxMapElement properties )
		{
			var fog = new JsonObject
			{
				["__type"] = "Rinten.GradientFog",
				["__guid"] = Guid.NewGuid().ToString(),
			};

			var colour = properties.GetString( "fogcolor" ) is { Length: > 0 } written ? Colour( written ) : Color.White;

			if ( properties.TryGetFloat( "fogmaxopacity", out var opacity ) )
				colour = colour.WithAlpha( opacity );

			fog["Color"] = Json.ToNode( colour );

			if ( properties.TryGetFloat( "fogstart", out var start ) )
				fog["StartDistance"] = start * Scale;

			if ( properties.TryGetFloat( "fogend", out var end ) )
				fog["EndDistance"] = end * Scale;

			if ( properties.TryGetFloat( "fogfalloffexponent", out var falloff ) )
				fog["FalloffExponent"] = falloff;

			if ( properties.TryGetFloat( "fogverticalexponent", out var vertical ) )
				fog["VerticalFalloffExponent"] = vertical;

			if ( properties.TryGetFloat( "fogstartheight", out var bottom ) &&
				properties.TryGetFloat( "fogendheight", out var top ) )
			{
				fog["Height"] = ( top - bottom ) * Scale;
			}

			return new JsonArray( fog );
		}

		//Their cubemap entity as our envmap probe, Baked: the scene's bake takes its picture
		private static JsonArray Cubemap( SBoxMapElement properties )
		{
			var probe = new JsonObject
			{
				["__type"] = "Rinten.EnvmapProbe",
				["__guid"] = Guid.NewGuid().ToString(),
				["Mode"] = "Baked",
			};

			//The box first, because whether there is one is what decides between a box projection and a
			//sphere. Hammer writes both corners relative to the entity, which is what our Bounds are as well.
			var mins = properties.GetVector3( "box_mins" );
			var maxs = properties.GetVector3( "box_maxs" );

			if ( maxs != Vector3.Zero || mins != Vector3.Zero )
			{
				//Both corners turn, and a turned min is not a min - so the box is put back in order after it.
				var turned = Axes.Box( new BBox( mins, maxs ) );

				probe["Bounds"] = new JsonObject
				{
					["Mins"] = Json.ToNode( turned.Mins * Scale ),
					["Maxs"] = Json.ToNode( turned.Maxs * Scale ),
				};

				probe["Projection"] = "Box";
			}
			else if ( properties.TryGetFloat( "influenceradius", out var radius ) && radius > 0.0f )
			{
				//No box, so it reaches the same distance every way. Written as a box all the same, because that is the shape the
				//component keeps and a sphere projection only changes how the picture is read, not how far the probe carries.
				var reach = new Vector3( radius, radius, radius ) * Scale;

				probe["Bounds"] = new JsonObject
				{
					["Mins"] = Json.ToNode( -reach ),
					["Maxs"] = Json.ToNode( reach ),
				};

				probe["Projection"] = "Sphere";
			}

			if ( properties.TryGetFloat( "cubemap_feathering", out var feathering ) )
				probe["Feathering"] = feathering * Scale;

			if ( properties.TryGetFloat( "bakenearz", out var near ) && near > 0.0f )
				probe["ZNear"] = near * Scale;

			if ( properties.TryGetFloat( "bakefarz", out var far ) && far > 0.0f )
				probe["ZFar"] = far * Scale;

			return new JsonArray( probe );
		}

		//Hammer writes a colour as numbers out of 255, with the alpha left off where it is opaque.
		private static Color Colour( string written )
		{
			var parts = written.Trim().Split( ' ', StringSplitOptions.RemoveEmptyEntries );

			float At( int i )
				=> i < parts.Length &&
					float.TryParse( parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value )
					? value / 255.0f
					: 1.0f;

			return new Color( At( 0 ), At( 1 ), At( 2 ), parts.Length > 3 ? At( 3 ) : 1.0f );
		}

		//-- objects -----------------------------------------------------------

		//Container for the node's Hammer selection set, or null; those are the only human-chosen names
		private JsonArray Set( SBoxMapElement node )
		{
			if ( !sets.TryGetValue( node, out var selection ) ) return null;

			return Container( selection.Name, selection.Enabled );
		}

		private JsonArray Hidden() => Container( HiddenName, enabled: false );

		private JsonArray Container( string name, bool enabled )
		{
			if ( !Sets.TryGetValue( name, out var container ) )
			{
				container = Object( name, Transform.Zero, enabled );
				Sets[name] = container;
			}

			return (JsonArray)container["Children"];
		}

		//An empty GameObject, written the way a scene of this version wants one: with its arrays
		//present even when they are empty, so that nothing has to be upgraded on the way in.
		private static JsonObject Object( string name, Transform transform, bool enabled = true )
		{
			var go = new JsonObject
			{
				["__guid"] = Guid.NewGuid().ToString(),
				["Flags"] = 0,
				["Name"] = name,
				["Enabled"] = enabled,
				["Components"] = new JsonArray(),
				["Children"] = new JsonArray(),
			};

			if ( !transform.Position.IsNearlyZero() )
				go["Position"] = Json.ToNode( transform.Position );

			if ( transform.Rotation != Rotation.Identity )
				go["Rotation"] = Json.ToNode( transform.Rotation );

			if ( transform.Scale != Vector3.One )
				go["Scale"] = Json.ToNode( transform.Scale );

			return go;
		}

		private static string Named( SBoxMapElement node, string fallback )
		{
			var name = node.GetString( "name" );

			return string.IsNullOrWhiteSpace( name ) ? fallback : name;
		}

		//Where a node is, in our units, and how it is turned and sized.
		private static Transform ReadTransform( SBoxMapElement node )
			=> new(
				Axes.Vector( node.GetVector3( "origin" ) ) * Scale,
				Angles( node.GetVector3( "angles" ) ),
				Axes.Size( node.GetVector3( "scales", Vector3.One ) ) );

		//Hammer's angles, which are pitch, yaw and roll in that order - and mean the same three things in both engines, so they come over as written.
		private static Rotation Angles( Vector3 angles ) => Rotation.From( angles.x, angles.y, angles.z );

		private static bool IsIdentity( Transform transform )
			=> transform.Position.IsNearlyZero()
				&& transform.Scale == Vector3.One
				&& transform.Rotation == Rotation.Identity;

		private static T At<T>( T[] values, int index, T fallback )
			=> index >= 0 && index < values.Length ? values[index] : fallback;

		/// <summary>What the port could not do with this map, said once rather than once per entity that hit it.</summary>
		public void Explain()
		{
			foreach ( var (classname, count) in unhandled.OrderByDescending( x => x.Value ) )
			{
				report.Notes.Add( $"{relative}: {count} {classname} - kept as an empty object where it stood, " +
					"because this engine has no component that is the same thing." );
			}

			foreach ( var material in lostMaterials )
				report.Skipped.Add( $"{relative}: faces named '{material}', which would not load - they wear the dev grid" );

			if ( subdividedMeshes > 0 )
			{
				report.Notes.Add( $"{relative}: {subdividedMeshes} mesh(es) had faces Hammer was subdividing, " +
					$"{sculptedMeshes} of them with the shape they were sculpted into. A displacement is the cage " +
					"in the mesh and the shape in a stream beside it; read without the stream, a lake is a ramp." );
			}
		}
	}
}
