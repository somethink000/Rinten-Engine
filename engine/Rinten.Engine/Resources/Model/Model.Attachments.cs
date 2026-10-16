namespace Rinten;

public class ModelAttachments
{
	public Model Model { get; }

	List<Attachment> _all;

	public IReadOnlyList<Attachment> All => _all;

	public int Count { get; private set; }

	/// <summary>The attachments a model has, read off its definition.</summary>
	public ModelAttachments( Model model )
	{
		Model = model;

		_all = new();

		Read( Definition( model ) );

		Count = _all.Count;
	}

	//The definition beside a compiled model, if this one came from a file. The compiled model is foo.mdl_c and its definition is foo.mdl
	//, which is the path the model itself carries - a model loaded by the compiled name is the same asset under a different suffix.
	private static ModelDefinition Definition( Model model )
	{
		var path = model?.ResourcePath;

		if ( string.IsNullOrWhiteSpace( path ) ) return null;

		if ( path.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) )
			path = path[..^2];

		return ResourceLibrary.TryGet<ModelDefinition>( path, out var definition ) ? definition : null;
	}

	//Resolves definition chains to a real bone plus composed offset; extra bones become attachments
	private void Read( ModelDefinition definition )
	{
		if ( definition is null ) return;

		var extras = new Dictionary<string, ModelDefinition.ExtraBone>( StringComparer.OrdinalIgnoreCase );

		foreach ( var bone in definition.Bones )
		{
			if ( bone is null || string.IsNullOrWhiteSpace( bone.Name ) ) continue;

			extras.TryAdd( bone.Name, bone );
		}

		foreach ( var bone in definition.Bones )
		{
			if ( bone is null || string.IsNullOrWhiteSpace( bone.Name ) ) continue;

			//One that made it into the skeleton is a bone and needs nothing from here - it is posed, merged onto and found by name like any
			//other. This list is for the ones that did not: a bone added to a model that cannot be compiled again is still a place on it.
			if ( Model?.Bones?.AllBones?.Any( x => x.IsNamed( bone.Name ) ) == true ) continue;

			var (parent, offset) = Resolve( bone.Parent, extras );

			Add( bone.Name, parent, offset.ToWorld( bone.Local ) );
		}

		foreach ( var attachment in definition.Attachments )
		{
			if ( attachment is null || string.IsNullOrWhiteSpace( attachment.Name ) ) continue;
			if ( !attachment.Enabled ) continue;

			var (parent, offset) = Resolve( attachment.Parent, extras );

			var local = offset.ToWorld( attachment.Local );

			//One that faces one way keeps the way it faced when the model was built: "ignore rotation" is what an animation may not do to
			//it, not a turn measured against nothing. Read as the bare angles it would point along the model's side rather than its front.
			Rotation? facing = attachment.FacesOneWay && parent is not null
				? Model.GetBoneTransform( parent.Index ).ToWorld( local ).Rotation
				: null;

			Add( attachment.Name, parent, local, Pulled( attachment, extras ), facing );
		}
	}

	//The real bone a name ends up on, and everything that was between them.
	private (BoneCollection.Bone Bone, Transform Offset) Resolve( string name,
		Dictionary<string, ModelDefinition.ExtraBone> extras )
	{
		var offset = Transform.Zero;

		//A chain cannot be longer than the definition has bones in it, and one that names itself would otherwise be a hang rather than a mistake.
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		while ( !string.IsNullOrWhiteSpace( name ) && seen.Add( name ) )
		{
			var bone = Model?.Bones?.AllBones?.FirstOrDefault( x => x.IsNamed( name ) );

			if ( bone is not null ) return (bone, offset);

			if ( !extras.TryGetValue( name, out var extra ) ) break;

			offset = extra.Local.ToWorld( offset );
			name = extra.Parent;
		}

		return (null, offset);
	}

	//The bones a place is pulled between, each found in the skeleton. Dropped altogether unless every bone in it is one this model has:
	//a list with a hole in it is a weighted average of the wrong things, which is worse than the single bone the definition also names.
	private Attachment.Influence[] Pulled( ModelDefinition.Attachment attachment,
		Dictionary<string, ModelDefinition.ExtraBone> extras )
	{
		if ( attachment.Influences is not { Count: > 1 } ) return System.Array.Empty<Attachment.Influence>();

		var pulled = new Attachment.Influence[attachment.Influences.Count];

		for ( var i = 0; i < pulled.Length; i++ )
		{
			var one = attachment.Influences[i];

			if ( one is null || string.IsNullOrWhiteSpace( one.Bone ) ) return System.Array.Empty<Attachment.Influence>();

			var (bone, offset) = Resolve( one.Bone, extras );

			if ( bone is null ) return System.Array.Empty<Attachment.Influence>();

			pulled[i] = new Attachment.Influence( bone, offset.ToWorld( one.Local ), one.Weight );
		}

		return pulled;
	}

	private void Add( string name, BoneCollection.Bone bone, Transform local,
		Attachment.Influence[] pulled = null, Rotation? facing = null )
	{
		_all.Add( new Attachment( Model, _all.Count, name, local )
		{
			Bone = bone,
			Pulled = pulled ?? System.Array.Empty<Attachment.Influence>(),
			Facing = facing,
		} );
	}

	public class Attachment
	{
		internal Attachment( Model model, int index, string name, Transform local )
		{
			Model = model;
			Index = index;
			Name = name ?? "";
			LocalTransform = local;
		}

		public Model Model { get; }
		public int Index { get; }
		public string Name { get; }
		public Transform LocalTransform { get; }
		public BoneCollection.Bone Bone { get; set; }

		/// <summary>The bones it is pulled between, where it is not simply on one.</summary>
		public Influence[] Pulled { get; set; } = System.Array.Empty<Influence>();

		/// <summary>Which way it faces, where that is not something its bones decide.</summary>
		public Rotation? Facing { get; set; }

		/// <summary>One bone a place is pulled by, and how much of it that bone is.</summary>
		public readonly record struct Influence( BoneCollection.Bone Bone, Transform Local, float Weight );

		/// <summary>Bone transformed LocalTransform</summary>
		public Transform WorldTransform
		{
			get
			{
				if ( Pulled.Length > 1 )
					return Faced( Between( Pulled, x => Model.GetBoneTransform( x.Index ) ) ?? LocalTransform );

				if ( Bone is null )
					return Faced( LocalTransform );

				var tx = Model.GetBoneTransform( Bone.Index );
				return Faced( tx.ToWorld( LocalTransform ) );
			}
		}

		//Weighted average of each influence's offset position; rotations averaged on the same hemisphere
		internal Transform Faced( Transform at )
		{
			if ( Facing is not { } fixedTo ) return at;

			at.Rotation = fixedTo;

			return at;
		}

		internal static Transform? Between( Influence[] pulled, System.Func<BoneCollection.Bone, Transform> where )
		{
			var position = Vector3.Zero;
			var rotation = new Rotation( 0.0f, 0.0f, 0.0f, 0.0f );
			var scale = Vector3.Zero;
			var whole = 0.0f;

			var first = Rotation.Identity;
			var started = false;

			foreach ( var one in pulled )
			{
				if ( one.Bone is null || one.Weight == 0.0f ) continue;

				var at = where( one.Bone ).ToWorld( one.Local );

				if ( !started )
				{
					first = at.Rotation;
					started = true;
				}

				//The shorter way round, so two turns that are the same do not cancel each other out - a
				//quaternion and its negative say the same thing, and adding one of each says nothing at all.
				var along = ( at.Rotation.x * first.x ) + ( at.Rotation.y * first.y )
					+ ( at.Rotation.z * first.z ) + ( at.Rotation.w * first.w );

				var turn = along < 0.0f
					? new Rotation( -at.Rotation.x, -at.Rotation.y, -at.Rotation.z, -at.Rotation.w )
					: at.Rotation;

				position += at.Position * one.Weight;
				scale += at.Scale * one.Weight;

				rotation = new Rotation(
					rotation.x + ( turn.x * one.Weight ),
					rotation.y + ( turn.y * one.Weight ),
					rotation.z + ( turn.z * one.Weight ),
					rotation.w + ( turn.w * one.Weight ) );

				whole += one.Weight;
			}

			if ( whole <= 0.0f ) return null;

			return new Transform( position / whole, rotation.Normal, scale / whole );
		}

		public bool IsNamed( string name )
		{
			return string.Equals( name, Name, StringComparison.OrdinalIgnoreCase );
		}
	}

	internal void Dispose()
	{
		_all.Clear();
	}

	public Attachment Get( string name )
	{
		//A loop: asked by name every frame for what is held or carried, and a lambda over the name was an allocation each time
		foreach ( var attachment in _all )
		{
			if ( attachment.IsNamed( name ) ) return attachment;
		}

		return null;
	}

	public Transform? GetTransform( string name )
	{
		var attachment = Get( name );
		if ( attachment is null ) return default;

		return attachment.WorldTransform;
	}
}

public partial class Model
{
	ModelAttachments _attachments;

	/// <summary>Access to bones of this model.</summary>
	public ModelAttachments Attachments
	{
		get
		{
			_attachments ??= new ModelAttachments( this );
			return _attachments;
		}
	}

	//They are read once, the first time anything asks. The editor calls this when somebody adds one, because a model that keeps the list it read at load is a model where a new attachment does not exist until the editor is restarted.
	/// <summary>Reads the attachments off the definition again.</summary>
	public void ReloadAttachments()
	{
		_attachments?.Dispose();
		_attachments = null;
	}

	/// <summary>Returns amount of attachment points this model has.</summary>
	public int AttachmentCount => Attachments.Count;

	//The model's own pose, which is the one it was modelled in. What something being animated has instead is on the renderer - see SkinnedModelRenderer.GetAttachment - because a muzzle is only where the arm holding it is this frame.
	/// <summary>Retrieves attachment transform based on given attachment name.</summary>
	public Transform? GetAttachment( string name ) => Attachments.GetTransform( name );

	/// <summary>Retrieves attachment transform based on given attachment index.</summary>
	public Transform? GetAttachment( int index )
	{
		if ( index < 0 || index >= AttachmentCount )
			throw new ArgumentOutOfRangeException( nameof( index ) );

		return Attachments.All[index].WorldTransform;
	}

	/// <summary>Returns name of an attachment at given index.</summary>
	public string GetAttachmentName( int index )
	{
		if ( index < 0 || index >= AttachmentCount )
			throw new ArgumentOutOfRangeException( nameof( index ) );

		return Attachments.All[index].Name;
	}
}
