namespace Rinten;

partial class ModelRenderer
{
	BBox IHasBounds.LocalBounds => LocalBounds;

	public BBox Bounds => GetWorldBoundsInternal();

	public BBox LocalBounds => GetLocalBoundsInternal();

	internal virtual BBox GetWorldBoundsInternal()
	{
		return LocalBounds.Transform( WorldTransform );
	}

	internal virtual BBox GetLocalBoundsInternal()
	{
		//The error marker it draws until there is a model to measure - see ModelOrError.
		if ( Model is null )
			return ModelOrError?.RenderBounds ?? BBox.FromPositionAndSize( Vector3.Zero, 0.4064f );

		return Deformed( Model.RenderBounds );
	}

	//Where the cull sphere is, and how big, from when it last moved.
	private Vector3 cullCenter;
	private float cullRadius = -1.0f;

	//Whether the cached cull sphere stays valid; skinned models' bounds change with the pose
	internal virtual bool CullSphereHolds => true;

	//Where this renderer sits in the flat array of spheres the drawer culls against, or -1 for nowhere.
	internal int CullSlot = -1;

	//Whether it is already on the list of things that moved.
	private bool cullMoved;

	//Renderers moved since last checked, so the shared flat cull-sphere array can be patched
	internal static readonly List<ModelRenderer> CullMoved = new();

	//Called when this has moved, or become a different model.
	internal void ForgetCullSphere()
	{
		cullRadius = -1.0f;

		//Only once between drainings, and only if anybody is keeping a sphere for it: a renderer that has never been drawn has no slot to fix.
		if ( cullMoved || CullSlot < 0 ) return;

		cullMoved = true;
		CullMoved.Add( this );
	}

	//Taken off the list of things that moved, having been dealt with.
	internal void CullSettled() => cullMoved = false;

	//Whether it is already on the list of things that changed.
	private bool cullChanged;

	//Renderers whose drawing changed since last checked, so the table needn't poll every renderer
	internal static readonly List<ModelRenderer> CullChanged = new();

	//Taken off the list of things that changed, having been dealt with.
	internal void CullResolved() => cullChanged = false;

	//Says that something the table resolves this instance from has changed.
	internal void DrawChanged()
	{
		DrawVersion++;

		//Only once between drainings, and only if the table is keeping a slot for it: one that has never been drawn has nothing to put right.
		if ( cullChanged || CullSlot < 0 ) return;

		cullChanged = true;
		CullChanged.Add( this );
	}

	//Last drawn world matrix, for the velocity buffer
	internal System.Numerics.Matrix4x4 LastDrawnWorld;
	internal bool HasLastDrawn;

	//Conservative cached bounding sphere for culling; recomputed when the model or transform changes
	internal void CullSphere( out Vector3 center, out float radius )
	{
		if ( cullRadius < 0.0f || !CullSphereHolds )
		{
			var local = GetLocalBoundsInternal();
			var transform = WorldTransform;

			var scale = MathF.Max( transform.Scale.x,
				MathF.Max( transform.Scale.y, transform.Scale.z ) );

			//Transform the bounds centre fully; most models' bounds aren't centred on their origin
			cullCenter = transform.PointToWorld( local.Center );
			cullRadius = local.Size.Length * 0.5f * scale;

			//Guard against NaN; a NaN sphere fails every frustum test and the object would never draw
			if ( cullCenter.IsNaN || float.IsNaN( cullRadius ) || float.IsInfinity( cullRadius ) )
			{
				cullCenter = transform.Position;
				cullRadius = float.MaxValue;
			}
		}

		center = cullCenter;
		radius = cullRadius;
	}
}
