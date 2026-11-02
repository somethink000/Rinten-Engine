namespace Rinten;

/// <summary>A model in our own editable format: source meshes plus materials, collision and mass.</summary>
[AssetType( Name = "Model", Extension = "mdl", Category = "Rendering", IconColor = "#5c8fd6", Icon = "view_in_ar" )]
[ProvidesResource( typeof( Model ) )]
public sealed partial class ModelDefinition : GameResource
{
	/// <summary>Source exports read at compile time; empty means geometry is baked in the compiled model.</summary>
	[Property]
	public List<MeshPart> Meshes { get; set; } = new();

	/// <summary>One source file the compiler merges into this model.</summary>
	public class MeshPart
	{
		//A part is made before its file is chosen - an empty slot for an export to be dropped into - and until then there is nothing to name it after. Blank falls back to the file's own name, which is what a part has always been called.
		/// <summary>What this part is called in the editor, where it is called anything.</summary>
		[Property]
		public string Name { get; set; } = "";

		[Property, FilePath( "fbx,gltf,glb,obj,dae,stl" )]
		public string File { get; set; } = "";

		[Property, Title( "Import scale" )]
		public float Scale { get; set; } = 1.0f;

		/// <summary>Imported meshes to keep. Null or empty is the whole file.</summary>
		[Property, Hide]
		public List<string> Include { get; set; }

		[Property, Hide]
		public bool Enabled { get; set; } = true;

		public bool Includes( string name )
			=> Include is not { Count: > 0 }
			|| Include.Any( n => string.Equals( n, name, StringComparison.OrdinalIgnoreCase ) );

		public void SetIncluded( string name, bool include, IReadOnlyList<string> allNames )
		{
			if ( Include is not { Count: > 0 } )
			{
				Include = new List<string>();
				foreach ( var child in allNames )
					Include.Add( child );
			}

			if ( include )
			{
				if ( !Include.Any( n => string.Equals( n, name, StringComparison.OrdinalIgnoreCase ) ) )
					Include.Add( name );
			}
			else
			{
				Include.RemoveAll( n => string.Equals( n, name, StringComparison.OrdinalIgnoreCase ) );
			}
		}

		//A model is built from several exports and they do not always agree about where the origin is - a sight modelled on its own, a magazine exported at nothing. Set here rather than re-exported, because the export is often somebody else's file.
		/// <summary>Moves this part after it is read, in metres.</summary>
		[Property]
		public Vector3 Position { get; set; }

		/// <summary>Turns this part after it is read, before it is moved.</summary>
		[Property]
		public Angles Rotation { get; set; }

		/// <summary>The bone this part hangs off, if it hangs off one.</summary>
		[Property, ModelDocBone]
		public string Parent { get; set; } = "";

		[Hide, System.Text.Json.Serialization.JsonIgnore]
		public bool IsPlaced => !Position.IsNearlyZero( 0.0001f )
			|| !Rotation.AsVector3().IsNearlyZero( 0.0001f );
	}

	/// <summary>Applied to the mesh on the way in. For when the export is in the wrong units and re-exporting is not worth it.</summary>
	[Property]
	public float Scale { get; set; } = 1.0f;

	//Stored as a name, resolved to a curve row per instance
	/// <summary>Project detail group (LOD by distance) for this model; empty keeps world detail.</summary>
	[Property, Title( "Detail Group" ), LodGroupName]
	public string LodGroup { get; set; } = "";

	/// <summary>Draw as a baked six-sided impostor when far away; never for what bones or shapes pose.</summary>
	[Property]
	public bool Impostor { get; set; }

	/// <summary>How the model comes down with distance - see <see cref="DetailMode"/>.</summary>
	[Property, Group( "Detail" ), Title( "Detail" )]
	public DetailMode Detail { get; set; }

	/// <summary>How many levels the chain has, the model as authored among them; nought for as many as the geometry gives - eight at the most, the last a few clusters.</summary>
	[Property, Group( "Detail" ), Range( 0, 8 ), ShowIf( nameof( Detail ), DetailMode.Levels )]
	public int DetailLevels { get; set; }

	/// <summary>What share of a level's triangles the next one keeps.</summary>
	[Property, Group( "Detail" ), Range( 0.1f, 0.9f ), ShowIf( nameof( Detail ), DetailMode.Levels )]
	public float DetailStep { get; set; } = 0.5f;

	/// <summary>How many levels coarser than the camera's the model's shadows are drawn with: a shadow is a silhouette, and a silhouette keeps its shape a level or two longer than the surface's light does.</summary>
	[Property, Group( "Detail" ), Range( 0, 3 ), ShowIf( nameof( Detail ), DetailMode.Levels )]
	public int ShadowLevelOffset { get; set; }

	/// <summary>What happens to a mesh of the model along the chain: it leaves it at a level, or it is the artist's own copy of another mesh at a level.</summary>
	[Property, Group( "Detail" ), ShowIf( nameof( Detail ), DetailMode.Levels )]
	public List<DetailMesh> DetailMeshes { get; set; } = new();

	/// <summary>How a model comes down with distance.</summary>
	public enum DetailMode
	{
		//A chain of levels made from it when it is compiled, the impostor last if it has one.
		[Icon( "stacked_line_chart" )] Levels,

		//The model as authored near, its impostor far, nothing between: rocks, crates, trees.
		[Icon( "photo" )] ImpostorOnly,

		//The model as authored at every distance.
		[Icon( "block" )] None,
	}

	/// <summary>One mesh of the model and what it is along the chain.</summary>
	public sealed class DetailMesh
	{
		/// <summary>The mesh, by its name in the art.</summary>
		[Property]
		public string Mesh { get; set; } = "";

		[Property]
		public DetailMeshUse Use { get; set; }

		/// <summary>The mesh it is a level of, for <see cref="DetailMeshUse.LevelOf"/>.</summary>
		[Property, ShowIf( nameof( Use ), DetailMeshUse.LevelOf )]
		public string Of { get; set; } = "";

		/// <summary>The level: the first it is gone from, or the one it is.</summary>
		[Property, Range( 1, 8 )]
		public int Level { get; set; } = 1;
	}

	public enum DetailMeshUse
	{
		/// <summary>Not in the chain from <see cref="DetailMesh.Level"/> on.</summary>
		GoneFrom,

		/// <summary>Level <see cref="DetailMesh.Level"/> of <see cref="DetailMesh.Of"/>, made by the artist.</summary>
		LevelOf,
	}

	/// <summary>How the skin follows the bones: their matrices blended, or the bones blended as rotations so that a twisted elbow or wrist keeps its volume. See <see cref="Rinten.SkinningMethod"/>.</summary>
	[Property]
	public SkinningMethod Skinning { get; set; }

	/// <summary>The files to compile: the ones this model names that name a file.</summary>
	public IReadOnlyList<MeshPart> AllMeshes()
	{
		if ( Meshes is not { Count: > 0 } ) return Array.Empty<MeshPart>();

		var listed = new List<MeshPart>();

		foreach ( var part in Meshes )
		{
			if ( part is null || string.IsNullOrWhiteSpace( part.File ) ) continue;

			listed.Add( part );
		}

		return listed;
	}

	/// <summary>Fill Transform, Mirror, mesh list and clips so the tree is not mutating on first paint.</summary>
	public void EnsureEditorDefaults()
	{
		Transform ??= new ModelTransform();
		Mirror ??= new ModelMirror();
		Meshes ??= new List<MeshPart>();
		Clips ??= new List<ModelClip>();
		Joints ??= new List<Joint>();
		Hulls ??= new List<Hull>();
		Attachments ??= new List<Attachment>();
		JiggleBones ??= new List<JiggleBone>();
		JiggleColliders ??= new List<JiggleCollider>();
	}

	/// <summary>Whether the game needs this definition at runtime (graph, clips, bones...); update with new sections</summary>
	[System.Text.Json.Serialization.JsonIgnore, Hide]
	public bool ReadAtRuntime =>
		Graph is not null
		|| Clips is { Count: > 0 }
		|| JiggleBones is { Count: > 0 }
		|| JiggleColliders is { Count: > 0 }
		|| Attachments is { Count: > 0 }
		|| Bones is { Count: > 0 }
		|| LookAtChains is { Count: > 0 }
		|| IkChains is { Count: > 0 }
		|| WeightLists is { Count: > 0 }
		|| Constraints is { Count: > 0 };

	//Overrides MaterialRemaps and defaults when set; empty uses the art's own materials
	/// <summary>One material over the whole model, whatever its art names.</summary>
	[Property, ResourceType( "mat" )]
	public string Material { get; set; } = "";

	//By position, so fragile; MaterialRemaps (by name) takes priority
	/// <summary>One per material slot in the mesh, in the order the mesh names them.</summary>
	[Hide, ResourceType( "mat" )]
	public List<string> Materials { get; set; } = new();

	//By surface name, which survives re-exports; Blender ".005"-style suffixes are ignored
	/// <summary>Which material goes on which surface, by the name the art calls it.</summary>
	[Property]
	public List<SurfaceMaterial> MaterialRemaps { get; set; } = new();

	//Numbers laid over a material, never a material made from them: the colour goes into the draw's tint and the rest stand in for the material's own, so a model of forty flat colours is one material drawn forty ways. See ModelFormat.Surface.
	/// <summary>Which meshes wear what the art itself said their surface is - the colour, roughness, metalness and glow a modelling tool writes into the file for a material nobody painted a picture for.</summary>
	[Property]
	public ArtSurfaceUse ArtSurfaces { get; set; } = ArtSurfaceUse.Unchosen;

	public enum ArtSurfaceUse
	{
		/// <summary>The meshes nobody chose a material for. One somebody did choose for wears that material as it is: a material is a whole answer, and the colour a surface had in the modelling tool before it was given one is not part of it.</summary>
		Unchosen,

		/// <summary>Every mesh, over whatever it wears - which is what makes one material of a project's own, named above, into every colour the art has.</summary>
		Everywhere,

		/// <summary>None: the model is drawn by its materials alone.</summary>
		Nowhere,
	}

	//Names no maps, so every slot uses its neutral default; the checkerboard is for broken materials
	/// <summary>The engine's own material: a plain surface, and what a mesh nobody put a material on wears.</summary>
	public const string PlainMaterial = "materials/default.mat";

	/// <summary>One surface of the art, and the material it wears.</summary>
	public class SurfaceMaterial
	{
		/// <summary>What the art calls the surface.</summary>
		[Property]
		public string Surface { get; set; } = "";

		/// <summary>The material to draw it with.</summary>
		[Property, ResourceType( "mat" )]
		public string Material { get; set; } = "";
	}

	//Declared explicitly on the model, not discovered from a folder
	/// <summary>The clips this model can play, named as the export names them, or as their own <see cref="ModelClip.TakeName"/> says otherwise.</summary>
	[Property]
	public List<ModelClip> Clips { get; set; } = new();

	[Property]
	public ModelTransform Transform { get; set; } = new();

	[Property]
	public ModelMirror Mirror { get; set; } = new();

	[Property]
	public List<Joint> Joints { get; set; } = new();

	//On the model because they are a fact about the skeleton: which bones bend when a character looks somewhere, and how much of the bend each takes, is the same for every graph that poses it.
	/// <summary>Runs of bones that turn together to point at something - a head and neck for looking, the spine for aiming a weapon.</summary>
	[Property, Group( "Animation" )]
	public List<LookChain> LookAtChains { get; set; } = new();

	/// <summary>One such run, from the bone that moves most.</summary>
	public class LookChain
	{
		[Property]
		public string Name { get; set; } = "";

		/// <summary>In order, heaviest first. Weights should add to about 1.</summary>
		[Property]
		public List<Link> Bones { get; set; } = new();

		public class Link
		{
			[Property, ModelDocBone]
			public string Bone { get; set; } = "";

			/// <summary>Share of the turn. A chain's weights should add to about 1.</summary>
			[Property]
			public float Weight { get; set; } = 1.0f;
		}
	}

	//Chain names map a graph's IK node to this model's bones and bend direction
	/// <summary>Named handles a graph reaches for a limb with - a foot toward the ground, a hand toward a weapon.</summary>
	[Property, Group( "Animation" )]
	public List<IkChain> IkChains { get; set; } = new();

	/// <summary>One such handle, named for a graph to ask for by that name.</summary>
	public class IkChain
	{
		[Property]
		public string Name { get; set; } = "";

		/// <summary>A chain turned off is one no graph can reach for.</summary>
		[Property]
		public bool Enabled { get; set; } = true;

		/// <summary>The bone that arrives: a hand, a foot, the last link of a tail.</summary>
		[Property, ModelDocBone, Title( "Tip Bone" )]
		public string Bone { get; set; } = "";

		//Naming the two ends rather than every joint is the whole of it: how many there are between them is the rig's business, and a chain that had to be told is a chain that breaks when an artist adds a twist bone.
		/// <summary>The bone the reach swings from - a thigh, an upper arm, the base of a tail. Everything the skeleton puts between it and the tip turns too. Empty is two bones up from the tip, which is a limb.</summary>
		[Property, ModelDocBone, Title( "Root Bone" )]
		public string StartBone { get; set; } = "";

		/// <summary>Pins the run to three bones with this one in the middle, for a limb whose joints between the ends are not meant to turn.</summary>
		[Property, ModelDocBone]
		public string MiddleBone { get; set; } = "";

		/// <summary>Which way the joint is pushed as the limb folds, in the model's own space. Off keeps whatever way the animation already bends it.</summary>
		[Property]
		public bool UseKneeDirection { get; set; }

		//Only read where UseKneeDirection is on. A knee is forward and an elbow is back, and the direction is squared against the reach before it is used, so it need only be roughly right.
		/// <summary>Where the knee or elbow points, in the model's own space.</summary>
		[Property, ShowIf( nameof( UseKneeDirection ), true )]
		public Vector3 KneeDirection { get; set; } = Vector3.Forward;

		//A default for the chain. A graph node that sets its own wins, so a hold that wants no twist can still say so.
		/// <summary>How much of the end bone's roll is spread back into the bone above it, so a wrist does not turn while the forearm stays still.</summary>
		[Property, Range( 0, 1 )]
		public float TwistWeight { get; set; }
	}

	//Per skeleton weight lists; list only bones where weight changes, rest get BaseWeight
	/// <summary>Named sets of per-bone weights a graph layers through - the upper body on its own, everything but the arms.</summary>
	[Property, Group( "Animation" )]
	public List<WeightList> WeightLists { get; set; } = new();

	//Only listed shapes from the FBX ship in the compiled model
	/// <summary>What the model does with the blend shapes its art brought in.</summary>
	[Property, Group( "Animation" )]
	public List<Morph> Morphs { get; set; } = new();

	/// <summary>One blend shape the art brought in, and what the model does with it.</summary>
	public class Morph
	{
		/// <summary>The name the export gave it. This is what a game asks for.</summary>
		[Property]
		public string Name { get; set; } = "";

		/// <summary>A shape turned off is one the compiled model does not carry at all.</summary>
		[Property]
		public bool Enabled { get; set; } = true;

		//Preview only - the compiled model carries the shape, not a weight. What a game sets is its own business.
		/// <summary>How far it is pushed in the model editor's viewport, so a shape can be looked at without putting the model in a scene.</summary>
		[Property, Range( 0, 1 )]
		public float Preview { get; set; }
	}

	/// <summary>One such set, named for a graph to ask for by that name.</summary>
	public class WeightList
	{
		[Property]
		public string Name { get; set; } = "";

		[Property, Hide]
		public bool Enabled { get; set; } = true;

		/// <summary>What a bone no entry reaches weighs. Nothing, for a mask that names the part of the body it lets through.</summary>
		[Property, Range( 0, 1 ), Title( "Base Weight" )]
		public float BaseWeight { get; set; } = 0.0f;

		/// <summary>Where the weight changes, and what it changes to.</summary>
		[Property]
		public List<BoneWeight> Bones { get; set; } = new();

		/// <summary>One bone, and what share of a layer reaches it.</summary>
		public class BoneWeight
		{
			[Property, KeyProperty, ModelDocBone]
			public string Bone { get; set; } = "";

			[Property, Range( 0, 1 )]
			public float Weight { get; set; } = 1.0f;

			/// <summary>Whether everything under the bone takes this weight too.</summary>
			[Property, Title( "Affects Children" )]
			public bool Descendants { get; set; } = true;
		}
	}

	//-- body groups -----------------------------------------------------------

	//Meshes nobody claims are always drawn, which is nearly all of them in nearly every model - so a model with no groups here behaves exactly as it did before there were any.
	/// <summary>Parts of the model that can be swapped or hidden: a head, a hat, nothing. One choice of each group shows at a time, and the first is what shows when nobody has said otherwise.</summary>
	[Property, Group( "Body Groups" )]
	public List<BodyGroup> BodyGroups { get; set; } = new();

	//A node carries the folder it is in - see the Folder on a collider, a joint or a clip - and that is enough to draw one. This is here so a folder can exist before anything is in it: made, then filled, which is the order somebody tidying up works in.
	/// <summary>Folders the outliner sorts nodes into, as "list/name".</summary>
	[Hide]
	public List<string> Folders { get; set; } = new();

	/// <summary>A set of choices of which one shows at a time.</summary>
	public class BodyGroup
	{
		[Property]
		public string Name { get; set; } = "";

		[Property, Hide]
		public bool Enabled { get; set; } = true;

		/// <summary>In order. The first is the default.</summary>
		[Property, Hide]
		public List<BodyChoice> Choices { get; set; } = new();
	}

	/// <summary>One option, and the meshes it turns on.</summary>
	public class BodyChoice
	{
		[Property]
		public string Name { get; set; } = "";

		/// <summary>Named as the export names them. Empty hides this group's meshes.</summary>
		[Property, Hide]
		public List<string> Meshes { get; set; } = new();
	}

	//-- levels of detail ------------------------------------------------------

	//No LODs (cluster DAG instead); this just stops exported LOD copies being compiled on top
	/// <summary>Meshes an export marked as cheaper copies for distance, by name. They are not compiled into the model at all.</summary>
	[Property, Hide]
	public List<string> CoarseMeshes { get; set; } = new();

	/// <summary>Alternative sets of materials - the same model in another livery. One entry per mesh, in the order the mesh names them; blanks keep what the model was built with.</summary>
	[Property, Group( "Body Groups" )]
	public List<MaterialSet> MaterialGroups { get; set; } = new();

	public class MaterialSet
	{
		[Property]
		public string Name { get; set; } = "";

		[Property, Hide, ResourceType( "mat" )]
		public List<string> Materials { get; set; } = new();
	}

	//-- collision ------------------------------------------------------------

	//A list rather than a setting, because collision is not one decision. A door is one hull; a chair is a hull for the seat and one per leg; a terrain piece is its own triangles. See Hull for what a shape can be built from.
	/// <summary>What this model is collided with: a list of shapes, each built its own way. Empty is a model nothing can hit, which most decoration is.</summary>
	public List<Hull> Hulls { get; set; } = new();

	/// <summary>What the collision feels like, where a shape does not say for itself.</summary>
	[Property, Group( "Collision" ), ResourceType( "surface" )]
	public string Surface { get; set; } = "";

	/// <summary>Kilograms. Zero means work it out from the volume and the surface's density, which is right for most things and wrong for anything hollow.</summary>
	[Property, Group( "Collision" )]
	public float Mass { get; set; } = 0.0f;

	//-- bounds ---------------------------------------------------------------

	/// <summary>Normally measured from the geometry. Overriding is for models that move their own vertices - foliage that sways, anything skinned past its rest pose - which would otherwise be culled while still on screen.</summary>
	[Property, Group( "Bounds" )]
	public bool OverrideBounds { get; set; } = false;

	[Property, Group( "Bounds" ), ShowIf( nameof( OverrideBounds ), true )]
	public BBox Bounds { get; set; } = BBox.FromPositionAndSize( 0, 32 );

	//The tile in the asset browser. Overridden rather than declared on the attribute: the icon only lives on the obsolete GameResource attribute,
	//and the base implementation reads it from there - so a type using the current AssetType attribute gets a question mark unless it draws its own.
	protected override Bitmap CreateAssetTypeIcon( int width, int height )
		=> EssResource.DrawGlyph( "view_in_ar", width, height, "#7fb4ff" );
}
