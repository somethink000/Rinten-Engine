using System;

namespace Rinten;

public partial class GameObject
{
	internal class GameObjectHandle
	{
		public Texture Texture { get; set; }
		public string Icon { get; set; }

		/// <summary>Base color from the <see cref="EditorHandleAttribute"/>, used when there's no color provider.</summary>
		public Color Color { get; set; }

		/// <summary>Color provider (usually a light), read live each frame so its color stays current.</summary>
		public Component.IColorProvider ColorProvider { get; set; }

		/// <summary>Whether clicking the handle selects the object - see <see cref="EditorHandleAttribute.Clickable"/>.</summary>
		public bool Clickable { get; set; } = true;

		/// <summary>The color to draw the handle with this frame.</summary>
		public Color EffectiveColor
		{
			get
			{
				if ( ColorProvider is null )
					return Color;

				//this is mainly for lights, we don't want any black bulbs, but we do want to indicate light color so if
				//anything else starts using this we should probably move this logic into the light component implementation
				return ((Vector3)ColorProvider.ComponentColor).Normal * 2;
			}
		}
	}

	GameObjectHandle _handle;
	bool handleBuilt;
	int _handleGen = -1;

	[Obsolete( "Use HasGizmoHandle" )]
	public bool HasGimzoHandle { get => HasGizmoHandle; private set => HasGizmoHandle = value; }
	public bool HasGizmoHandle { get; private set; }

	//Whether this object or anything under it has a gizmo to draw.
	private bool subtreeDraws;

	//The scene's shape when that was last worked out.
	private int subtreeVersion;

	//Whether any component in this subtree could draw gizmos; cached until DirectoryVersion changes
	private bool SubtreeDrawsGizmos()
	{
		var scene = Scene;

		//No scene, no version to trust - walk it.
		if ( scene is null ) return true;

		var version = scene.DirectoryVersion;

		if ( subtreeVersion == version ) return subtreeDraws;

		subtreeVersion = version;
		subtreeDraws = SelfDrawsGizmos();
		drawingChildren = 0;

		//All of them, and not until the first that says yes: the walk asks every child in turn anyway, so filling them in here is the same work done once
		//instead of once each. The ones that say yes are kept, because those are the only children the walk ever needs to visit again - see DrawGizmos.
		for ( var i = 0; i < Children.Count; i++ )
		{
			if ( Children[i] is not { } child ) continue;

			if ( !child.SubtreeDrawsGizmos() ) continue;

			subtreeDraws = true;

			drawing ??= new GameObject[Math.Max( 4, Children.Count )];
			if ( drawingChildren == drawing.Length ) Array.Resize( ref drawing, drawing.Length * 2 );

			drawing[drawingChildren++] = child;
		}

		return subtreeDraws;
	}

	//Cached children whose subtrees draw, so the gizmo walk skips the rest
	private GameObject[] drawing;

	private int drawingChildren;

	//Whether this object alone has anything to draw.
	private bool SelfDrawsGizmos()
	{
		//A bone draws its own line to its parent - see DrawBoneGizmo.
		if ( Flags.Contains( GameObjectFlags.Bone ) ) return true;

		foreach ( var c in Components.GetAll() )
		{
			if ( c is null ) continue;

			if ( c.OverridesDrawGizmos ) return true;

			//And the handle, which the object draws on a component's behalf, so a component that overrides nothing can still put one here.
			var typeDesc = Game.TypeLibrary.GetType( c.GetType() );
			if ( typeDesc is null ) continue;

			foreach ( var _ in typeDesc.GetAttributes<EditorHandleAttribute>( true ) ) return true;
		}

		return false;
	}

	void BuildGizmoDetails()
	{
		//Rebuilt only when the component list changes (ClearInternalCache) or a gizmo type is toggled.
		if ( handleBuilt && _handleGen == Gizmo.GizmoTypeGeneration )
			return;

		handleBuilt = true;
		_handleGen = Gizmo.GizmoTypeGeneration;
		_handle = null;

		EditorHandleAttribute handles = null;
		TypeDescription handleType = null;

		foreach ( var c in Components.GetAll() )
		{
			if ( c is null )
				continue;

			var typeDesc = Game.TypeLibrary.GetType( c.GetType() );
			if ( typeDesc is null )
				continue;

			if ( !Gizmo.Settings.IsGizmoEnabled( typeDesc.TargetType ) )
				continue;

			foreach ( var attr in typeDesc.GetAttributes<EditorHandleAttribute>( true ) )
			{
				handles = attr;
				handleType = typeDesc;
				break;
			}

			if ( handles is not null )
				break;
		}

		if ( handles is null )
			return;

		_handle = new GameObjectHandle();
		_handle.Icon = handles.Icon;
		_handle.Color = handles.Color;
		_handle.Clickable = handles.Clickable;
		_handle.ColorProvider = Components.GetAll<Component.IColorProvider>().FirstOrDefault();

		if ( !string.IsNullOrWhiteSpace( handles.Texture ) )
		{
			//Quietly: a handle that names a picture we do not ship is the usual case, not a fault, and it says so once a frame per object.
			_handle.Texture = Texture.Load( handles.Texture, warnOnMissing: false );
		}

		//Handle texture is missing, so fall back to the component's editor icon
		if ( _handle.Texture is null && string.IsNullOrWhiteSpace( _handle.Icon ) )
		{
			_handle.Icon = handleType?.Icon;
		}
	}

	void DrawGizmoHandle( ref bool clicked )
	{
		HasGizmoHandle = false;

		if ( !Gizmo.Settings.GizmosEnabled ) return;

		BuildGizmoDetails();

		if ( _handle is null )
			return;

		var renderDistance = Gizmo.Settings.GizmoRenderDistance;
		if ( renderDistance > 0 )
		{
			var dist = Gizmo.Camera.Position.Distance( Gizmo.Transform.Position );
			if ( dist > renderDistance )
				return;
		}

		bool selected = Gizmo.IsSelected;
		bool worldSpace = Gizmo.Settings.WorldSpaceGizmos;

		using ( Gizmo.Scope( "Handle" ) )
		{
			Gizmo.Transform = Gizmo.Transform.WithScale( 1.0f );

			//World space is metres, screen space is pixels - two different numbers for the same handle, and only the first one is a length.
			float size = worldSpace ? 0.2f : 32;

			float opacity = 0.6f;

			if ( Gizmo.IsHovered || selected ) opacity = 1;

			//Resolve the picture before the hitbox, so an invisible handle doesn't block clicks
			var picture = _handle.Texture;
			var tint = selected ? Color.Yellow : _handle.EffectiveColor;

			if ( picture is null && !string.IsNullOrWhiteSpace( _handle.Icon ) )
			{
				//Rasterised white and coloured at the quad, rather than drawn in the colour it wants: the cache is keyed by what is in
				//the scope, so baking the colour in made a new texture - and threw the old one away - every time a light's colour moved.
				var text = new TextRendering.Scope( _handle.Icon, Color.White, 64, "Material Icons", 400 );
				text.Shadow = new TextRendering.Shadow { Enabled = true, Color = Color.Black, Offset = 2, Size = 8 };

				picture = TextRendering.GetOrCreateTexture( text, flag: TextFlag.Center );
			}

			if ( picture is null )
				return;

			tint = tint.WithAlphaMultiplied( opacity );

			//Measured at the size it is drawn at when it is not being pointed at, so growing under
			//the pointer doesn't widen what has to be stayed inside of to keep pointing at it.
			if ( !selected && _handle.Clickable )
			{
				//Slight bias so the icon beats the mesh it sits inside, but not things truly in front of it
				Gizmo.Hitbox.DepthBias = 0.9f;
				Gizmo.Hitbox.Sprite( 0, size * Gizmo.Settings.GizmoScale, worldSpace );

				clicked = clicked || Gizmo.WasClicked;
			}

			if ( Gizmo.IsHovered && Gizmo.Settings.Selection ) size = worldSpace ? 0.25f : 40;

			Gizmo.Draw.IgnoreDepth = !Gizmo.Settings.GizmoDepthTest;
			Gizmo.Draw.Color = tint;
			Gizmo.Draw.Sprite( Vector3.Zero, size * Gizmo.Settings.GizmoScale, picture, worldSpace );
		}

		HasGizmoHandle = true;
	}

	//Whether anything under this object was clicked while its gizmos drew. A field rather than the local it used to be, so
	//that the two walks below can be delegates this object keeps instead of closures it builds. See drawComponentGizmos.
	private bool gizmoClicked;

	//Cached per object so DrawGizmos doesn't allocate two closures per object every frame
	private Action<Component> drawComponentGizmos;

	//This object's name in gizmo paths, which never changes.
	private string gizmoPath;

	internal void DrawGizmos()
	{
		if ( !Active ) return;

		if ( !SubtreeDrawsGizmos() )
		{
			//Nothing here drew, so nothing here has a handle to be picked by - see ObjectSelection, which asks.
			HasGizmoHandle = false;
			return;
		}

		var parentTx = Gizmo.Transform;

		var tx = LocalTransform;

		//Absolute gameobject transform need to be converted back to local because it's already in worldspace
		if ( Flags.Contains( GameObjectFlags.Absolute ) )
		{
			tx = parentTx.ToLocal( tx );
		}

		gizmoPath ??= $"object-{GetHashCode()}";

		using ( Gizmo.ObjectScopeInternal( this, tx, gizmoPath ) )
		{
			gizmoClicked = Gizmo.WasClicked;

			if ( Gizmo.Settings.GizmosEnabled )
			{
				var clicked = gizmoClicked;
				DrawGizmoHandle( ref clicked );
				gizmoClicked = clicked;

				drawComponentGizmos ??= DrawComponentGizmos;
				Components.ForEach( "DrawGizmos", false, drawComponentGizmos );
			}

			if ( gizmoClicked )
			{
				GizmoSelect();
			}

			//Pressed on this or its hitboxes then dragged: release, so the drag becomes a selection box
			if ( Gizmo.Pressed.ThisOrChild && Gizmo.CursorDragDelta.Length > 10 )
			{
				Gizmo.Pressed.ClearPath();
			}

			//Only the children that draw, which SubtreeDrawsGizmos worked out when the scene last changed shape. Everything the old walk tested per child
			//- that it is there, that it is on, that it is not hidden - is still tested here, because all three change without the scene changing shape.
			for ( var i = 0; i < drawingChildren; i++ )
			{
				var child = drawing[i];

				if ( !child.IsValid() || !child.Active ) continue;

				try
				{
					DrawChildGizmos( child );
				}
				catch ( System.Exception e )
				{
					Log.Warning( e, $"Exception when calling DrawGizmos on {child}: {e.Message}" );
				}
			}

			DrawBoneGizmo();
		}
	}

	private void DrawComponentGizmos( Component c )
	{
		if ( !c.OverridesDrawGizmos || c.Flags.Contains( ComponentFlags.Hidden ) )
			return;

		//A gizmo type that has been switched off draws nothing, so it must catch nothing. The switch used to reach as far as the object's handle
		//icon and stop there, leaving the component's own gizmo drawing itself and its hitboxes live under a scene that was showing neither.
		if ( !Gizmo.Settings.IsGizmoEnabled( c.GetType() ) )
			return;

		using var scope = Gizmo.ScopeInternal();

		c.DrawGizmosInternal();

		gizmoClicked = gizmoClicked || Gizmo.WasClicked;
	}

	private static void DrawChildGizmos( GameObject c )
	{
		if ( c.Flags.Contains( GameObjectFlags.Hidden ) || c.Tags.Has( "hidden" ) )
			return;

		c.DrawGizmos();
	}

	void DrawBoneGizmo()
	{
		if ( !Gizmo.Settings.GizmosEnabled )
			return;

		if ( !Flags.Contains( GameObjectFlags.Bone ) )
			return;

		if ( !Parent.IsValid() )
			return;

		if ( !Parent.Flags.Contains( GameObjectFlags.Bone ) )
			return;

		//Bones are small and there are a lot of them, so they stop being drawn once the skeleton is more than a room away.
		var distance = Root.WorldPosition.Distance( Gizmo.Camera.Position );
		if ( distance > 12.5f )
			return;

		var position = WorldTransform.PointToLocal( Parent.WorldPosition );
		var length = position.Length * 0.5f;
		var width = length * 0.1f;

		if ( width.AlmostEqual( 0.0f ) )
			return;

		using ( Gizmo.Scope( "Bone" ) )
		{
			Gizmo.Hitbox.DepthBias *= 0.2f;

			//The look every skeleton in the editor shares - see Gizmo.DrawBone.
			Gizmo.DrawBone( position, Gizmo.IsSelected, Gizmo.IsHovered );

			Gizmo.Hitbox.Sphere( new Sphere( 0, Gizmo.BoneJointSize ) );

			if ( Gizmo.IsHovered || Gizmo.IsSelected )
			{
				var textPos = Gizmo.Transform.Position + Gizmo.CameraTransform.Up * (Gizmo.BoneJointSize * 2);
				Gizmo.Draw.Text( Name, Gizmo.Transform.ToLocal( new Transform( textPos ) ), flags: TextFlag.Center );
			}

			if ( Gizmo.WasClicked )
				GizmoSelect();
		}
	}

	//Finds the first GameObject in the ancestor chain that we consider a selection base.
	GameObject FindSelectionBase()
	{
		var isSelectionBase = IsNetworkRoot || IsOutermostPrefabInstanceRoot || Components.GetAll().Any( x => Game.TypeLibrary.GetType( x?.GetType() )?.HasAttribute<SelectionBaseAttribute>() == true );

		if ( isSelectionBase ) return this;

		if ( Parent.IsValid() ) return Parent.FindSelectionBase();

		return null;
	}

	//Whether what is selected is this object or something under it.
	static bool HoldsSelection( GameObject root )
	{
		foreach ( var selected in Gizmo.Active.Selection )
		{
			if ( selected is not GameObject go || !go.IsValid() )
				continue;

			//Reads backwards: true when root is go, or stands above it.
			if ( go.IsAncestor( root ) )
				return true;
		}

		return false;
	}

	void GizmoSelect()
	{
		if ( !Gizmo.Settings.Selection )
			return;

		//Find the best candidate to select
		var selectionBase = FindSelectionBase();

		if ( selectionBase != null && selectionBase != this )
		{
			//First click selects the prefab whole; once anything inside is selected, clicks reach parts
			if ( !HoldsSelection( selectionBase ) )
			{
				selectionBase.GizmoSelect();
				return;
			}
		}

		using ( Gizmo.ObjectScope( this, LocalTransform ) )
		{
			using ( Scene.Editor?.UndoScope( $"Select {Name}" ).Push() )
			{
				Gizmo.Select();
			}
		}
	}

}
