using Rinten.UI;
﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Editor.MdlEditor;

sealed class TransformDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.Transform;
	public override string Icon => "open_with";
	public override bool CanToggle( ModelDocItem item ) => true;

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		items.Add( new ModelDocItem
		{
			Kind = Kind,
			Title = "Transform",
			Enabled = ctx.Definition.Transform?.Enabled ?? true
		} );
	}

	public override bool TrySetEnabled( ModelDocContext ctx, ModelDocItem item, bool enabled )
	{
		ctx.Definition.Transform ??= new ModelDefinition.ModelTransform();
		ctx.Definition.Transform.Enabled = enabled;
		return true;
	}

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		ctx.Definition.Transform ??= new ModelDefinition.ModelTransform();
		ctx.BindObject( sheet, ctx.Definition.Transform );
	}

	public override ScenePlacement Placement( ModelDocContext ctx, ModelDocItem item )
	{
		var transform = ctx.Definition.Transform ??= new ModelDefinition.ModelTransform();
		return new ScenePlacement
		{
			Name = "model.transform",
			GetPosition = () => transform.Position,
			GetRotation = () => transform.Rotation,
			GetScale = () => transform.Scale,
			Set = ( position, rotation ) =>
			{
				transform.Position = position;
				transform.Rotation = rotation;
				ctx.Host.NotifyDefinitionChanged();
			},
			SetScale = scale =>
			{
				transform.Scale = scale;
				ctx.Host.NotifyDefinitionChanged();
			},
		};
	}
}

sealed class MirrorDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.Mirror;
	public override string Icon => "flip";
	public override bool CanToggle( ModelDocItem item ) => true;

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		items.Add( new ModelDocItem
		{
			Kind = Kind,
			Title = "Mirror",
			Enabled = ctx.Definition.Mirror?.Enabled ?? true
		} );
	}

	public override bool TrySetEnabled( ModelDocContext ctx, ModelDocItem item, bool enabled )
	{
		ctx.Definition.Mirror ??= new ModelDefinition.ModelMirror();
		ctx.Definition.Mirror.Enabled = enabled;
		return true;
	}

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		ctx.Definition.Mirror ??= new ModelDefinition.ModelMirror();
		ctx.BindObject( sheet, ctx.Definition.Mirror );
	}
}

sealed class MaterialsDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.MaterialGroups;
	public override IEnumerable<ModelDocKind> AlsoHandles => [ModelDocKind.DefaultMaterials, ModelDocKind.MaterialGroup];
	public override bool CanDelete( ModelDocItem item ) => item.Kind == ModelDocKind.MaterialGroup;

	public override string IconFor( ModelDocItem item ) => item.Kind == ModelDocKind.MaterialGroups ? "palette" : "texture";

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		var groups = ctx.Definition.MaterialGroups;
		if ( !ModelDocContext.AddFolder( items, Kind, "Material Group", groups.Count + 1 ) ) return;
		if ( ctx.IsCollapsed( Kind ) ) return;

		//"Default" while the panel under it led with a Default Material field. It leads with the materials the
		//art names now, and a material group is the thing beside it that swaps them - so this is Materials.
		items.Add( new ModelDocItem { Kind = ModelDocKind.DefaultMaterials, Title = "Materials", Depth = 1 } );

		for ( var i = 0; i < groups.Count; i++ )
		{
			var set = groups[i];
			items.Add( new ModelDocItem
			{
				Kind = ModelDocKind.MaterialGroup,
				Title = string.IsNullOrWhiteSpace( set.Name ) ? $"Group {i + 1}" : set.Name,
				Depth = 1,
				Index = i
			} );
		}
	}

	public override IEnumerable<ModelDocAdd> AddActions( ModelDocContext ctx )
	{
		yield return new( "Appearance", "palette", "Material Group", "texture", () =>
		{
			ctx.Definition.MaterialGroups.Add( new ModelDefinition.MaterialSet { Name = "group" } );
			ctx.Host.Outliner.OnStructureChanged( ModelDocKind.MaterialGroup, ctx.Definition.MaterialGroups.Count - 1 );
		} );
	}

	public override bool TryDelete( ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind != ModelDocKind.MaterialGroup || !ModelDocContext.InRange( ctx.Definition.MaterialGroups, item.Index ) )
			return false;

		ctx.Definition.MaterialGroups.RemoveAt( item.Index );
		return true;
	}

	public override System.Collections.IList ListFor( ModelDocContext ctx, ModelDocKind kind )
		=> kind == ModelDocKind.MaterialGroup ? ctx.Definition.MaterialGroups : null;

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		if ( item.IsFolder )
		{
			ModelDocInspect.BindFolder( this, ctx, body, "Alternative material sets for this model." );
			return;
		}

		if ( item.Kind == ModelDocKind.DefaultMaterials )
		{
			body.AddChild( new ModelMaterialsPanel( ctx ) );
			return;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.MaterialGroups, item.Index ) )
			return;

		var set = ctx.Definition.MaterialGroups[item.Index];
		set.Materials ??= new List<string>();

		var nameSheet = new Inspectors.ControlSheet { ShowPropertyNameInTooltip = true };
		ctx.BindObject( nameSheet, set );
		body.AddChild( nameSheet );

		var names = ModelDocInspect.MeshSlotNames( ctx );
		var count = Math.Max( names.Count, set.Materials.Count );
		if ( count == 0 )
		{
			body.AddChild( ModelDocInspect.Note( "No meshes to assign yet." ) );
			return;
		}

		body.AddChild( ModelDocInspect.Note( "Empty slot keeps the default." ) );

		for ( var i = 0; i < count; i++ )
		{
			var title = i < names.Count ? names[i] : $"Slot {i}";
			body.AddChild( ModelDocInspect.Note( title ) );

			var slot = new Inspectors.ControlSheet { ShowPropertyNameInTooltip = false };
			ctx.BindObject( slot, new MaterialSlotBind( set.Materials, i ) );
			body.AddChild( slot );
		}
	}
}

sealed class MeshDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.Mesh;
	public override bool CanDelete( ModelDocItem item ) => item.Kind == ModelDocKind.Mesh && !item.IsFolder;
	public override bool CanToggle( ModelDocItem item ) => !item.IsFolder;

	public override string Icon => "view_in_ar";

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		var meshes = ctx.Definition.Meshes;
		if ( !ModelDocContext.AddFolder( items, Kind, "Mesh", meshes.Count ) ) return;
		if ( ctx.IsCollapsed( Kind ) ) return;

		for ( var i = 0; i < meshes.Count; i++ )
		{
			var part = meshes[i];
			if ( part is null ) continue;

			items.Add( new ModelDocItem
			{
				Kind = ModelDocKind.Mesh,
				Title = TitleOf( part, i ),
				Depth = 1,
				Index = i,
				Enabled = part.Enabled
			} );
		}
	}

	//What a part is called in the tree: its name, its file, or its place.
	static string TitleOf( ModelDefinition.MeshPart part, int index )
	{
		if ( !string.IsNullOrWhiteSpace( part.Name ) ) return part.Name.Trim();
		if ( !string.IsNullOrWhiteSpace( part.File ) ) return Path.GetFileName( part.File );

		return $"Mesh {index}";
	}

	//The exports a part can be built out of - what a drag has to be.
	static readonly string[] Exports = [".fbx", ".gltf", ".glb", ".obj", ".dae", ".stl"];

	public override IEnumerable<ModelDocAdd> AddActions( ModelDocContext ctx )
	{
		//Creates a named part first; its file is dropped on or picked afterwards
		yield return new( "Mesh", "view_in_ar", "New Mesh", "view_in_ar", () =>
			//Filled in rather than empty: an empty box turns Confirm into a button that does
			//nothing, and this is the name the part would have had anyway - see TitleOf.
			ModelOutliner.AskName( ctx.Host.Outliner, $"Mesh {ctx.Definition.Meshes.Count}",
				"What do you want to call it?", name =>
				{
					ctx.Definition.Meshes.Add( new ModelDefinition.MeshPart { Name = name } );
					ctx.Host.Outliner.OnStructureChanged( ModelDocKind.Mesh, ctx.Definition.Meshes.Count - 1 );
				}, Icon ) );
	}

	public override bool CanTake( ModelDocContext ctx, ModelDocItem item, string path )
	{
		if ( item.Kind != ModelDocKind.Mesh || item.IsFolder ) return false;
		if ( !ModelDocContext.InRange( ctx.Definition.Meshes, item.Index ) ) return false;

		return Exports.Contains( Path.GetExtension( path ), StringComparer.OrdinalIgnoreCase );
	}

	public override void Take( ModelDocContext ctx, ModelDocItem item, string path )
	{
		var part = ctx.Definition.Meshes[item.Index];

		//An asset arrives already written against the assets folder; a file off the desktop arrives as wherever it is on the disk.
		part.File = Path.IsPathRooted( path )
			? ModelDocContext.ToAssetsPath( path )
			: path.Replace( '\\', '/' );

		TakeClips( ctx, part );
	}

	//Adds the mesh file's animations as clips, skipping ones already listed
	static void TakeClips( ModelDocContext ctx, ModelDefinition.MeshPart part )
	{
		var added = ModelClipImport.Add( ctx, part.File, part.Scale );
		if ( added <= 0 ) return;

		Log.Info( $"Model: {added} animation{( added == 1 ? "" : "s" )} added from {Path.GetFileName( part.File )}" );

		//Which redraws the tree the new clips are now in - see ModelPage.
		ctx.Host.NotifyDefinitionChanged();
	}

	public override bool TrySetEnabled( ModelDocContext ctx, ModelDocItem item, bool enabled )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.Meshes, item.Index ) )
			return false;

		if ( item.Kind != ModelDocKind.Mesh ) return false;

		ctx.Definition.Meshes[item.Index].Enabled = enabled;
		return true;
	}

	public override bool TryDelete( ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind != ModelDocKind.Mesh ) return false;

		if ( ModelDocContext.InRange( ctx.Definition.Meshes, item.Index ) )
			ctx.Definition.Meshes.RemoveAt( item.Index );

		return true;
	}

	public override System.Collections.IList ListFor( ModelDocContext ctx, ModelDocKind kind )
		=> kind == ModelDocKind.Mesh ? ctx.Definition.Meshes : null;

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		if ( item.IsFolder )
		{
			//The whole model's, so it sits with the meshes rather than on one of them: which of the project's detail groups every copy of it keeps.
			ctx.BindDefinition( sheet, nameof( ModelDefinition.LodGroup ) );

			//And how it comes down with distance: the chain of levels made when it is compiled, its impostor, and what single meshes do along it.
			ctx.BindDefinition( sheet, nameof( ModelDefinition.Detail ), nameof( ModelDefinition.Impostor ),
				nameof( ModelDefinition.DetailLevels ), nameof( ModelDefinition.DetailStep ),
				nameof( ModelDefinition.ShadowLevelOffset ), nameof( ModelDefinition.DetailMeshes ) );

			ModelDocInspect.BindFolder( this, ctx, body, "Source mesh files this model is built from." );
			return;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.Meshes, item.Index ) ) return;

		var part = ctx.Definition.Meshes[item.Index];
		var named = part.File;

		ctx.BindObject( sheet, part, property =>
		{
			if ( property?.Name != nameof( ModelDefinition.MeshPart.File ) ) return;
			if ( string.Equals( part.File, named, StringComparison.OrdinalIgnoreCase ) ) return;

			//The box is typed into a character at a time, so most of what arrives here is half a path
			//naming nothing. There is no export to read takes or sub-meshes out of until it is a file.
			if ( !ModelSourceCatalog.Exists( ctx.Asset, part.File ) ) return;

			named = part.File;

			TakeClips( ctx, part );

			//The file is what the sub-mesh list below is read out of, so the panel is stale the moment it becomes another file.
			ctx.Host.NotifyDefinitionChanged( rebuildInspector: true );
		} );

		var names = ModelSourceCatalog.Meshes( ctx.Asset, part );
		if ( names.Count == 0 )
		{
			body.AddChild( ModelDocInspect.Note( "File not found or no sub-meshes" ) );
			return;
		}

		body.AddChild( ModelDocInspect.Note( "Meshes in this file" ) );

		foreach ( var name in names )
		{
			var child = name;

			//Qualified: Editor.Checkbox is the widget of the same name.
			var include = body.AddChild<Rinten.UI.Checkbox>();
			include.LabelText = child;
			include.Value = part.Includes( child );
			include.ValueChanged = on =>
			{
				part.SetIncluded( child, on, names );
				ctx.Host.NotifyDefinitionChanged();
			};
		}

		var missing = ( part.Include ?? [] )
			.Where( written => !names.Contains( written, StringComparer.OrdinalIgnoreCase ) )
			.ToList();

		if ( missing.Count > 0 )
		{
			body.AddChild( ModelDocInspect.Note(
				$"Not in the export: {string.Join( ", ", missing )}",
				Theme.Red.WithAlpha( 0.7f ) ) );
		}
	}

	public override ScenePlacement Placement( ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind != ModelDocKind.Mesh || item.IsFolder ) return null;
		if ( !ModelDocContext.InRange( ctx.Definition.Meshes, item.Index ) ) return null;

		var part = ctx.Definition.Meshes[item.Index];
		return new ScenePlacement
		{
			Name = $"mesh.{item.Index}",
			GetPosition = () => part.Position,
			GetRotation = () => part.Rotation,
			GetScale = () => new Vector3( part.Scale ),
			UniformScale = true,
			Set = ( position, rotation ) =>
			{
				part.Position = position;
				part.Rotation = rotation;
				ctx.Host.NotifyDefinitionChanged();
			},
			SetScale = scale =>
			{
				part.Scale = scale.x;
				ctx.Host.NotifyDefinitionChanged();
			},
		};
	}

	public override void DrawOverlay( Model model, ModelDefinition definition, ModelDocItem item, Action changed = null, bool resize = false )
	{
		if ( model is null ) return;

		Gizmo.Draw.LineBBox( model.Bounds );
	}
}

sealed class AnimationDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.Animations;
	public override IEnumerable<ModelDocKind> AlsoHandles => [ModelDocKind.Animation, ModelDocKind.AnimEvent];
	public override bool CanDelete( ModelDocItem item )
		=> item.Kind is ModelDocKind.Animation or ModelDocKind.AnimEvent;
	public override bool CanToggle( ModelDocItem item ) => item.Kind == ModelDocKind.Animation;

	public override string IconFor( ModelDocItem item ) => item.Kind switch
	{
		ModelDocKind.Animation => "play_circle",
		ModelDocKind.AnimEvent => "bookmark",
		_ => "movie",
	};

	//What a clip marks, under the clip that marks it. Their editor hangs these off the animation and so does this: an event is not
	//a thing on its own, it is a moment of one clip, and a list of them away from the clip they belong to reads as nothing at all.
	static void Marks( int index, ModelDefinition.ModelClip clip, List<ModelDocItem> items, int depth )
	{
		if ( clip?.Events is not { Count: > 0 } ) return;

		for ( var at = 0; at < clip.Events.Count; at++ )
		{
			var marked = clip.Events[at];
			var span = marked.EndFrame > marked.StartFrame
				? $"{marked.StartFrame:0.#}-{marked.EndFrame:0.#}"
				: $"{marked.StartFrame:0.#}";

			items.Add( new ModelDocItem
			{
				Kind = ModelDocKind.AnimEvent,
				Title = $"{( string.IsNullOrWhiteSpace( marked.Id ) ? marked.Kind.ToString() : marked.Id )}  ·  {span}",
				Depth = depth,
				Index = at,
				ParentIndex = index,
				Icon = marked.Kind == ModelDefinition.ModelClip.EventKind.Tag ? "sell" : "bookmark",
			} );
		}
	}

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		var clips = ctx.Definition.AllClips();
		if ( !ModelDocContext.AddFolder( items, Kind, "Animations", clips.Count ) ) return;
		if ( ctx.IsCollapsed( Kind ) ) return;

		ModelDocFolders.Collect( items, ctx, Kind, clips, x => x.Folder, ( i, clip ) => new ModelDocItem
		{
			Kind = ModelDocKind.Animation,
			Title = clip.SequenceName,
			Depth = 1,
			Index = i,
			Enabled = clip.Enabled
		}, Marks );
	}

	public override IEnumerable<ModelDocAdd> AddActions( ModelDocContext ctx )
	{
		yield return new( "Animation", "movie", "Anim File", "movie", () =>
		{
			var fd = new FileDialog( null )
			{
				Title = "Add Animation File",
				Directory = Project.Current.GetAssetsPath()
			};
			fd.SetFindFile();
			fd.SetNameFilter( "Animation (*.fbx *.gltf *.glb *.smd)" );
			if ( !fd.Execute() || string.IsNullOrWhiteSpace( fd.SelectedFile ) )
				return;

			var file = ModelDocContext.ToAssetsPath( fd.SelectedFile );

			//One animation out of the file. Which of its sequences it is, is picked under the file in the clip's
			//inspector - a file holding twenty takes is added as often as there are takes wanted, not twenty at once.
			var added = ModelClipImport.AddOne( ctx, file );
			if ( added < 0 )
			{
				EditorUtility.DisplayDialog( "No animations",
					$"{System.IO.Path.GetFileName( fd.SelectedFile )} has no animation in it - only the mesh was exported. " +
					"Export it again with its animation baked in (in Blender's FBX export: Bake Animation, with the actions to include)." );
				return;
			}

			ctx.Host.Outliner.OnStructureChanged( ModelDocKind.Animation, added );
		} );
	}

	public override bool TrySetEnabled( ModelDocContext ctx, ModelDocItem item, bool enabled )
	{
		var clips = ctx.Definition.Clips;
		if ( !ModelDocContext.InRange( clips, item.Index ) ) return false;

		clips[item.Index].Enabled = enabled;
		return true;
	}

	public override bool TryDelete( ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind == ModelDocKind.AnimEvent )
		{
			if ( Marked( ctx, item ) is not { } clip ) return false;

			clip.Events.RemoveAt( item.Index );
			return true;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.Clips, item.Index ) )
			return false;

		ctx.Definition.Clips.RemoveAt( item.Index );
		return true;
	}

	//The clip an event row belongs to.
	static ModelDefinition.ModelClip Marked( ModelDocContext ctx, ModelDocItem item )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.Clips, item.ParentIndex ) ) return null;

		var clip = ctx.Definition.Clips[item.ParentIndex];

		return clip.Events is not null && item.Index >= 0 && item.Index < clip.Events.Count ? clip : null;
	}

	public override System.Collections.IList ListFor( ModelDocContext ctx, ModelDocKind kind )
		=> kind == ModelDocKind.Animation ? ctx.Definition.Clips : null;

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		if ( item.IsFolder )
		{
			ModelDocInspect.BindFolder( this, ctx, body, "Takes compiled into this model." );
			return;
		}

		if ( item.Kind == ModelDocKind.AnimEvent )
		{
			if ( Marked( ctx, item ) is { } marked )
				ctx.BindObject( sheet, marked.Events[item.Index] );

			return;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.Clips, item.Index ) )
			return;

		var clip = ctx.Definition.Clips[item.Index];
		var named = clip.File;

		//The file first and, right under it, which of its animations this is; then the rest of the clip. Two sheets over the one object with the
		//sequence box between them, so the box stands by the file it lists. The folder is not a row: the tree sorts a clip by dragging it into one.
		var rest = new Inspectors.ControlSheet { ShowPropertyNameInTooltip = true };

		ctx.BindObject( sheet, rest, clip,
			first: property => property.Name == nameof( ModelDefinition.ModelClip.File ),
			shown: property => property.Name != nameof( ModelDefinition.ModelClip.Folder ),
			changed: property =>
			{
				if ( property?.Name != nameof( ModelDefinition.ModelClip.File ) ) return;
				if ( string.Equals( clip.File, named, StringComparison.OrdinalIgnoreCase ) ) return;
				if ( !ModelSourceCatalog.Exists( ctx.Asset, clip.File ) ) return;

				named = clip.File;

				//The sequence box is this file's sequences, so it is another file's the moment the field moves.
				ctx.Host.NotifyDefinitionChanged( rebuildInspector: true );
			} );

		BindSequencePicker( ctx, clip, body );

		body.AddChild( rest );
	}

	static void BindSequencePicker( ModelDocContext ctx, ModelDefinition.ModelClip clip, Panel body )
	{
		body.AddChild( ModelDocInspect.Note( "Sequence" ) );

		if ( string.IsNullOrWhiteSpace( clip.File ) )
		{
			body.AddChild( ModelDocInspect.Note( "Pick an animation file to list its sequences." ) );
			return;
		}

		var takes = ModelSourceCatalog.Takes( ctx.Asset, clip.File );
		if ( takes.Count == 0 )
		{
			body.AddChild( ModelDocInspect.Note( "No animation sequences in this file." ) );
			return;
		}

		//Which of them this clip is. By the file's own name for it where the clip carries one; by
		//its place in the file where it does not, which is every animation an exporter left unnamed.
		var at = string.IsNullOrWhiteSpace( clip.TakeName )
			? takes.FindIndex( take => take.Index == clip.Take )
			: takes.FindIndex( take => string.Equals( take.Name, clip.TakeName, StringComparison.OrdinalIgnoreCase ) );

		//What is written but is not in the export is still offered, marked, so picking something else does not silently throw the old name away.
		var notInExport = $"{clip.TakeName} (not in export)";

		var combo = body.AddChild<Inspectors.ControlDropDown>();

		//Numbered rather than named: a take an exporter left unnamed has no name to round-trip through the box, and two of them would be the same option.
		combo.Options = takes
			.Select( take => new Rinten.UI.Option( take.Title, take.Index.ToString() ) )
			.Concat( at < 0 ? [new Rinten.UI.Option( notInExport, "-1" )] : [] )
			.ToList();

		combo.Value = at < 0 ? "-1" : at.ToString();

		combo.ValueChanged = picked =>
		{
			if ( !int.TryParse( picked, out var index ) ) return;
			if ( index < 0 || index >= takes.Count ) return;

			var take = takes[index];
			if ( string.Equals( clip.TakeName, take.Name, StringComparison.Ordinal ) && clip.Take == take.Index )
				return;

			//The name follows the take while it is still the one this gave it. Somebody who
			//typed their own name for the clip keeps it - that is what the field is for.
			if ( at >= 0 && string.Equals( clip.Name, takes[at].Title, StringComparison.Ordinal ) )
				clip.Name = take.Title;

			clip.TakeName = take.Name;
			clip.Take = take.Index;

			ctx.Host.NotifyDefinitionChanged();
			ctx.Host.Outliner.RefreshTree();
		};
	}

	public override void AddContextOptions( Menu menu, ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind != ModelDocKind.Animation ) return;
		if ( !ModelDocContext.InRange( ctx.Definition.Clips, item.Index ) ) return;

		var clip = ctx.Definition.Clips[item.Index];

		if ( menu.HasOptions ) menu.AddSeparator();

		menu.AddOption( "Add Event", "bookmark", () => Mark( ctx, item, clip,
			new ModelDefinition.ModelClip.Event
			{
				Id = "event",
				Kind = ModelDefinition.ModelClip.EventKind.Generic,
			} ) );

		menu.AddOption( "Add Tag", "sell", () => Mark( ctx, item, clip,
			new ModelDefinition.ModelClip.Event
			{
				Id = "tag",
				Kind = ModelDefinition.ModelClip.EventKind.Tag,

				//A span rather than a moment, because that is what a tag is - ten frames of one is somewhere to start dragging from.
				EndFrame = 10.0f,
			} ) );
	}

	static void Mark( ModelDocContext ctx, ModelDocItem item, ModelDefinition.ModelClip clip,
		ModelDefinition.ModelClip.Event marked )
	{
		clip.Events ??= new List<ModelDefinition.ModelClip.Event>();
		clip.Events.Add( marked );

		ctx.Host.NotifyDefinitionChanged();
		ctx.Host.Outliner.OnStructureChanged( ModelDocKind.AnimEvent, clip.Events.Count - 1 );
	}

	public override ModelDefinition.ModelClip Clip( ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind != ModelDocKind.Animation ) return null;
		var clips = ctx.Definition.AllClips();
		return item.Index >= 0 && item.Index < clips.Count ? clips[item.Index] : null;
	}
}


sealed class HullDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.Hulls;
	public override IEnumerable<ModelDocKind> AlsoHandles => [ModelDocKind.Hull];
	public override string Icon => "hexagon";
	public override bool CanDelete( ModelDocItem item ) => item.Kind == ModelDocKind.Hull;
	public override bool CanToggle( ModelDocItem item ) => item.Kind == ModelDocKind.Hull;

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		var hulls = ctx.Definition.Hulls;
		if ( !ModelDocContext.AddFolder( items, Kind, "Colliders", hulls.Count ) ) return;
		if ( ctx.IsCollapsed( Kind ) ) return;

		ModelDocFolders.Collect( items, ctx, Kind, hulls, x => x.Folder, ( i, hull ) => new ModelDocItem
		{
			Kind = ModelDocKind.Hull,
			Title = string.IsNullOrWhiteSpace( hull.Name ) ? hull.Title : hull.Name,
			Depth = 1,
			Index = i,
			Enabled = hull.Enabled,
			Icon = hull.Icon
		} );
	}

	static void Add( ModelDocContext ctx, ModelDefinition.Hull hull )
	{
		hull.Bone ??= "";
		if ( string.IsNullOrWhiteSpace( hull.Bone ) )
			hull.Bone = ModelDocPhysics.SelectedBone( ctx );

		ctx.Definition.Hulls.Add( hull );
		ctx.Host.Outliner.OnStructureChanged( ModelDocKind.Hull, ctx.Definition.Hulls.Count - 1 );
	}

	public override IEnumerable<ModelDocAdd> AddActions( ModelDocContext ctx )
	{
		var radius = ModelDocPhysics.DefaultRadius( ctx.Host.CompiledModel );

		yield return new( "Collision", "hexagon", "Sphere", "circle",
			() => Add( ctx, new ModelDefinition.SphereHull { Radius = radius } ) );
		yield return new( "Collision", "hexagon", "Capsule", "medication",
			() => Add( ctx, new ModelDefinition.CapsuleHull
			{
				Radius = radius * 0.55f,

				//Standing up, which is y - it was z, which stood a new capsule up when z was the height and lays it down pointing at the camera now.
				PointA = Vector3.Down * radius,
				PointB = Vector3.Up * radius
			} ) );
		yield return new( "Collision", "hexagon", "Box", "crop_square",
			() => Add( ctx, new ModelDefinition.BoxHull { Extents = new Vector3( radius ) } ) );
		yield return new( "Collision", "hexagon", "Hull", "hexagon",
			() => Add( ctx, new ModelDefinition.RenderHull() ) );
		yield return new( "Collision", "hexagon", "Render Mesh", "grid_on",
			() => Add( ctx, new ModelDefinition.RenderMesh() ) );
	}

	public override void AddContextOptions( Menu menu, ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind != ModelDocKind.Hull || !ModelDocContext.InRange( ctx.Definition.Hulls, item.Index ) )
			return;

		var hull = ctx.Definition.Hulls[item.Index];
		menu.AddOption( "Ball Joint from this", "link", () =>
		{
			var joint = ModelDocPhysics.JointOn( ctx, ModelDefinition.JointKind.Ball, hull.Bone ?? "" );
			joint.Name = string.IsNullOrWhiteSpace( hull.Name ) ? hull.Title : hull.Name;
			ctx.Definition.Joints.Add( joint );
			ctx.Host.Outliner.OnStructureChanged( ModelDocKind.Joint, ctx.Definition.Joints.Count - 1 );
		} );
	}

	public override bool TrySetEnabled( ModelDocContext ctx, ModelDocItem item, bool enabled )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.Hulls, item.Index ) ) return false;
		ctx.Definition.Hulls[item.Index].Enabled = enabled;
		return true;
	}

	public override bool TryDelete( ModelDocContext ctx, ModelDocItem item )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.Hulls, item.Index ) ) return false;
		ctx.Definition.Hulls.RemoveAt( item.Index );
		return true;
	}

	public override System.Collections.IList ListFor( ModelDocContext ctx, ModelDocKind kind )
		=> kind == ModelDocKind.Hull ? ctx.Definition.Hulls : null;

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		if ( item.IsFolder )
		{
			ModelDocInspect.BindFolder( this, ctx, body, "Collision shapes the compiled model is hit with." );
			return;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.Hulls, item.Index ) ) return;

		var hull = ctx.Definition.Hulls[item.Index];
		ctx.BindObject( sheet, hull );

		if ( hull is ModelDefinition.RenderMesh )
			body.AddChild( ModelDocInspect.Note( "Static only - a triangle soup has no mass and cannot be pushed." ) );
	}

	public override ScenePlacement Placement( ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind != ModelDocKind.Hull || !ModelDocContext.InRange( ctx.Definition.Hulls, item.Index ) )
			return null;

		var hull = ctx.Definition.Hulls[item.Index];
		if ( !hull.CanPlace ) return null;

		var model = ctx.Host.CompiledModel;

		//The same frame and scale the hull is drawn in, so the handle sits on the shape rather than on where the shape would be with no
		//whole-model Transform - and so dragging it writes back a number in the hull's own space rather than one with the Transform folded into it.
		Transform Frame() => ModelDocPhysics.HullFrame( model, hull, ctx.Definition.Transform );
		Vector3 Scale() => ctx.Definition.Transform is { Enabled: true } t ? t.Scale : Vector3.One;

		return new ScenePlacement
		{
			Name = $"hull.{item.Index}",
			GetPosition = () => Frame().PointToWorld( hull.Origin * Scale() ),
			GetRotation = () => ( Frame().Rotation * hull.Turn.ToRotation() ).Angles(),
			Set = ( position, rotation ) =>
			{
				var frame = Frame();
				hull.Place( frame.PointToLocal( position ) / Scale(),
					( frame.Rotation.Inverse * rotation.ToRotation() ).Angles() );
				ctx.Host.NotifyDefinitionChanged();
			}
		};
	}

	public override void DrawOverlay( Model model, ModelDefinition definition, ModelDocItem item, Action changed = null, bool resize = false )
	{
		if ( definition?.Hulls is not { Count: > 0 } ) return;

		var selected = item.Kind == ModelDocKind.Hull ? item.Index : -1;

		for ( var i = 0; i < definition.Hulls.Count; i++ )
		{
			var hull = definition.Hulls[i];
			if ( hull is null || !hull.Enabled ) continue;

			Gizmo.Draw.Color = i == selected
				? Theme.Primary.WithAlpha( 0.95f )
				: Theme.Primary.WithAlpha( 0.35f );
			ModelDocPhysics.DrawHull( model, definition, hull, i, i == selected, changed, resize );
		}
	}
}

sealed class JointDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.Joints;
	public override IEnumerable<ModelDocKind> AlsoHandles => [ModelDocKind.Joint];
	public override string Icon => "link";
	public override bool CanDelete( ModelDocItem item ) => item.Kind == ModelDocKind.Joint;
	public override bool CanToggle( ModelDocItem item ) => item.Kind == ModelDocKind.Joint;

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		var joints = ctx.Definition.Joints;
		if ( !ModelDocContext.AddFolder( items, Kind, "Joints", joints.Count ) ) return;
		if ( ctx.IsCollapsed( Kind ) ) return;

		ModelDocFolders.Collect( items, ctx, Kind, joints, x => x.Folder, ( i, joint ) => new ModelDocItem
		{
			Kind = ModelDocKind.Joint,
			Title = joint.Title,
			Depth = 1,
			Index = i,
			Enabled = joint.Enabled
		} );
	}

	public override IEnumerable<ModelDocAdd> AddActions( ModelDocContext ctx )
	{
		yield return Add( ctx, ModelDefinition.JointKind.Ball, "Ball Joint", "radio_button_unchecked" );
		yield return Add( ctx, ModelDefinition.JointKind.Hinge, "Hinge Joint", "door_front" );
		yield return Add( ctx, ModelDefinition.JointKind.Slider, "Slider Joint", "swap_vert" );
		yield return Add( ctx, ModelDefinition.JointKind.Fixed, "Fixed Joint", "lock" );
	}

	static ModelDocAdd Add( ModelDocContext ctx, ModelDefinition.JointKind kind, string title, string icon )
		=> new( "Collision", "hexagon", title, icon, () =>
		{
			ctx.Definition.Joints.Add(
				ModelDocPhysics.JointOn( ctx, kind, ModelDocPhysics.SelectedBone( ctx ) ) );
			ctx.Host.Outliner.OnStructureChanged( ModelDocKind.Joint, ctx.Definition.Joints.Count - 1 );
		} );

	public override bool TrySetEnabled( ModelDocContext ctx, ModelDocItem item, bool enabled )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.Joints, item.Index ) ) return false;
		ctx.Definition.Joints[item.Index].Enabled = enabled;
		return true;
	}

	public override bool TryDelete( ModelDocContext ctx, ModelDocItem item )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.Joints, item.Index ) ) return false;
		ctx.Definition.Joints.RemoveAt( item.Index );
		return true;
	}

	public override System.Collections.IList ListFor( ModelDocContext ctx, ModelDocKind kind )
		=> kind == ModelDocKind.Joint ? ctx.Definition.Joints : null;

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		if ( item.IsFolder )
		{
			ModelDocInspect.BindFolder( this, ctx, body, "Constraints between two hulls, matched by bone name." );
			return;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.Joints, item.Index ) )
			return;

		var joint = ctx.Definition.Joints[item.Index];
		if ( string.IsNullOrWhiteSpace( joint.BoneA ) || string.IsNullOrWhiteSpace( joint.BoneB ) )
			body.AddChild( ModelDocInspect.Note( "joint will be ignored", Theme.Red ) );

		if ( RestOutsideCone( joint ) is { } complaint )
			body.AddChild( ModelDocInspect.Note( complaint, Theme.Red.WithAlpha( 0.8f ) ) );

		ctx.BindObject( sheet, joint );
	}

	//Warns when a ball joint's swing offset starts it outside its cone, which makes it shake
	static string RestOutsideCone( ModelDefinition.Joint joint )
	{
		if ( joint.Type != ModelDefinition.JointKind.Ball || !joint.EnableSwingLimit )
			return null;

		var rest = Vector3.GetAngle( joint.SwingOffset.ToRotation().Forward, Vector3.Forward );
		if ( rest <= joint.SwingLimit ) return null;

		return $"Swing Offset leans this {rest:0.#} degrees off the cone's axis, and the cone is "
			+ $"only {joint.SwingLimit:0.#} degrees wide - the joint starts outside its own limit "
			+ "and is pushed on every frame. Widen the limit, or shorten the offset.";
	}

	public override ScenePlacement Placement( ModelDocContext ctx, ModelDocItem item )
	{
		if ( item.Kind != ModelDocKind.Joint || !ModelDocContext.InRange( ctx.Definition.Joints, item.Index ) )
			return null;

		var joint = ctx.Definition.Joints[item.Index];
		var model = ctx.Host.CompiledModel;
		return new ScenePlacement
		{
			Name = $"joint.{item.Index}",
			GetPosition = () => ModelDocPhysics.WorldAnchor( model, joint ),
			GetRotation = () => ModelDocPhysics.WorldTurn( model, joint ),
			Set = ( position, rotation ) =>
			{
				var bone = ModelDocPhysics.BoneOf( model, joint.BoneA );
				joint.Position = bone.PointToLocal( position );
				joint.Rotation = ( bone.Rotation.Inverse * rotation.ToRotation() ).Angles();
				joint.ModelSpace = false;
				ctx.Host.NotifyDefinitionChanged();
			}
		};
	}

	public override void DrawOverlay( Model model, ModelDefinition definition, ModelDocItem item, Action changed = null, bool resize = false )
	{
		if ( definition?.Joints is not { Count: > 0 } ) return;

		var selected = item.Kind == ModelDocKind.Joint ? item.Index : -1;

		for ( var i = 0; i < definition.Joints.Count; i++ )
		{
			var joint = definition.Joints[i];
			if ( joint is null || !joint.Enabled ) continue;

			Gizmo.Draw.Color = i == selected
				? Theme.Primary.WithAlpha( 0.95f )
				: Theme.Primary.WithAlpha( 0.4f );
			Gizmo.Draw.LineThickness = i == selected ? 2.5f : 1.5f;
			ModelDocPhysics.DrawJoint( model, joint, i == selected );
		}
	}
}

sealed class LookAtDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.LookAt;
	public override IEnumerable<ModelDocKind> AlsoHandles => [ModelDocKind.LookChain];
	public override bool CanDelete( ModelDocItem item ) => item.Kind == ModelDocKind.LookChain;

	public override string IconFor( ModelDocItem item )
		=> item.Kind == ModelDocKind.LookChain ? "link" : "visibility";

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		var chains = ctx.Definition.LookAtChains;
		if ( !ModelDocContext.AddFolder( items, Kind, "Look At", chains.Count ) ) return;
		if ( ctx.IsCollapsed( Kind ) ) return;

		for ( var i = 0; i < chains.Count; i++ )
		{
			var chain = chains[i];
			items.Add( new ModelDocItem
			{
				Kind = ModelDocKind.LookChain,
				Title = string.IsNullOrWhiteSpace( chain.Name ) ? $"Chain {i}" : chain.Name,
				Depth = 1,
				Index = i
			} );
		}
	}

	public override IEnumerable<ModelDocAdd> AddActions( ModelDocContext ctx )
	{
		yield return new( "Animation", "movie", "Look At Chain", "visibility", () =>
		{
			ctx.Definition.LookAtChains.Add( new ModelDefinition.LookChain { Name = "look" } );
			ctx.Host.Outliner.OnStructureChanged( ModelDocKind.LookChain, ctx.Definition.LookAtChains.Count - 1 );
		} );
	}

	public override bool TryDelete( ModelDocContext ctx, ModelDocItem item )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.LookAtChains, item.Index ) ) return false;
		ctx.Definition.LookAtChains.RemoveAt( item.Index );
		return true;
	}

	public override System.Collections.IList ListFor( ModelDocContext ctx, ModelDocKind kind )
		=> kind == ModelDocKind.LookChain ? ctx.Definition.LookAtChains : null;

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		if ( item.IsFolder )
		{
			ModelDocInspect.BindFolder( this, ctx, body, "Bone chains that turn to look at a target." );
			return;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.LookAtChains, item.Index ) )
			return;

		body.AddChild( ModelDocInspect.Note( "Weights should add to about 1, heaviest first." ) );
		ctx.BindObject( sheet, ctx.Definition.LookAtChains[item.Index] );
	}
}

sealed class IkChainDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.IkChains;
	public override IEnumerable<ModelDocKind> AlsoHandles => [ModelDocKind.IkChain];
	public override bool CanDelete( ModelDocItem item ) => item.Kind == ModelDocKind.IkChain;
	public override bool CanToggle( ModelDocItem item ) => item.Kind == ModelDocKind.IkChain;

	public override string IconFor( ModelDocItem item )
		=> item.Kind == ModelDocKind.IkChain ? "polyline" : "pan_tool_alt";

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		var chains = ctx.Definition.IkChains;
		if ( !ModelDocContext.AddFolder( items, Kind, "IK Chains", chains.Count ) ) return;
		if ( ctx.IsCollapsed( Kind ) ) return;

		for ( var i = 0; i < chains.Count; i++ )
		{
			var chain = chains[i];
			items.Add( new ModelDocItem
			{
				Kind = ModelDocKind.IkChain,
				Title = string.IsNullOrWhiteSpace( chain.Name ) ? $"Chain {i}" : chain.Name,
				Depth = 1,
				Index = i,
				Enabled = chain.Enabled
			} );
		}
	}

	public override IEnumerable<ModelDocAdd> AddActions( ModelDocContext ctx )
	{
		yield return new( "Animation", "movie", "IK Chain", "pan_tool_alt", () =>
		{
			var chains = ctx.Definition.IkChains;

			chains.Add( new ModelDefinition.IkChain { Name = ModelDocIk.Unused( chains, "ik_chain" ) } );
			ctx.Host.Outliner.OnStructureChanged( ModelDocKind.IkChain, chains.Count - 1 );
		} );
	}

	public override bool TrySetEnabled( ModelDocContext ctx, ModelDocItem item, bool enabled )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.IkChains, item.Index ) ) return false;

		ctx.Definition.IkChains[item.Index].Enabled = enabled;
		return true;
	}

	public override bool TryDelete( ModelDocContext ctx, ModelDocItem item )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.IkChains, item.Index ) ) return false;

		ctx.Definition.IkChains.RemoveAt( item.Index );
		return true;
	}

	public override System.Collections.IList ListFor( ModelDocContext ctx, ModelDocKind kind )
		=> kind == ModelDocKind.IkChain ? ctx.Definition.IkChains : null;

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		if ( item.IsFolder )
		{
			ModelDocInspect.BindFolder( this, ctx, body,
				"Limbs a graph reaches with, named once here so a graph can ask for them by name." );
			return;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.IkChains, item.Index ) )
			return;

		var chain = ctx.Definition.IkChains[item.Index];
		var solved = ModelDocIk.Solve( ctx.Host.CompiledModel, chain );

		body.AddChild( ModelDocInspect.Note( solved.IsValid
			? $"Turns  {solved}"
			: "Set the tip bone - a hand, a foot. The root is two bones above it unless one is named, and everything between them turns.",
			solved.IsValid ? null : Theme.Yellow.WithAlpha( 0.8f ) ) );

		ctx.BindObject( sheet, chain );
	}

	public override void DrawOverlay( Model model, ModelDefinition definition, ModelDocItem item,
		Action changed = null, bool resize = false )
	{
		if ( definition?.IkChains is not { Count: > 0 } chains ) return;

		var selected = item.Kind == ModelDocKind.IkChain ? item.Index : -1;

		for ( var i = 0; i < chains.Count; i++ )
		{
			var chain = chains[i];
			if ( chain is null || !chain.Enabled ) continue;

			var solved = ModelDocIk.Solve( model, chain );
			if ( !solved.IsValid ) continue;

			var chosen = i == selected;

			Gizmo.Draw.Color = chosen ? Theme.Primary.WithAlpha( 0.95f ) : Theme.Primary.WithAlpha( 0.35f );
			Gizmo.Draw.LineThickness = chosen ? 2.5f : 1.5f;

			ModelDocIk.Draw( model, chain, solved, chosen );
		}
	}
}

//Which bones one of a model's IK chains means, and how it is drawn.
static class ModelDocIk
{
	/// <summary>The run of bones a chain turns, root first.</summary>
	public readonly record struct Solved( string[] Bones )
	{
		public bool IsValid => Bones is { Length: >= 2 };

		public string Root => IsValid ? Bones[0] : "";
		public string Tip => IsValid ? Bones[^1] : "";

		public override string ToString() => IsValid ? string.Join( " → ", Bones ) : "";
	}

	//The same walk the runtime does, so what the page draws is what a graph will turn.
	/// <summary>The run a chain names - the two bones it was given and whatever the skeleton puts between them.</summary>
	public static Solved Solve( Model model, ModelDefinition.IkChain chain )
	{
		if ( chain is null || string.IsNullOrWhiteSpace( chain.Bone ) ) return default;

		var tip = chain.Bone;
		var middle = chain.MiddleBone;

		var root = chain.StartBone;

		if ( string.IsNullOrWhiteSpace( root ) )
		{
			root = string.IsNullOrWhiteSpace( middle )
				? Above( model, Above( model, tip ) )
				: Above( model, middle );
		}

		if ( string.IsNullOrWhiteSpace( root ) ) return default;

		//A middle bone pins the run to three, the way a chain used to be written.
		if ( !string.IsNullOrWhiteSpace( middle ) )
		{
			return Runs( model, root, middle ) && Runs( model, middle, tip )
				? new Solved( [root, middle, tip] )
				: default;
		}

		return new Solved( RunBetween( model, root, tip ) );
	}

	static string Above( Model model, string bone )
	{
		if ( model is not { IsValid: true } || string.IsNullOrWhiteSpace( bone ) ) return "";
		return model.Bones?.GetBone( bone )?.Parent?.Name ?? "";
	}

	//Whether one bone is somewhere above another.
	static bool Runs( Model model, string above, string below )
	{
		for ( var at = Above( model, below ); !string.IsNullOrWhiteSpace( at ); at = Above( model, at ) )
		{
			if ( string.Equals( at, above, StringComparison.OrdinalIgnoreCase ) ) return true;
		}

		return false;
	}

	//Every bone from a root down to a tip. Null where the two are not a run.
	static string[] RunBetween( Model model, string root, string tip )
	{
		if ( string.Equals( root, tip, StringComparison.OrdinalIgnoreCase ) ) return null;

		var run = new List<string> { tip };

		for ( var at = Above( model, tip ); !string.IsNullOrWhiteSpace( at ); at = Above( model, at ) )
		{
			run.Add( at );

			if ( !string.Equals( at, root, StringComparison.OrdinalIgnoreCase ) ) continue;

			run.Reverse();
			return run.ToArray();
		}

		return null;
	}

	/// <summary>A name no other chain has, so two added in a row are not both "ik_chain".</summary>
	public static string Unused( List<ModelDefinition.IkChain> chains, string wanted )
	{
		if ( chains.All( c => !string.Equals( c?.Name, wanted, StringComparison.OrdinalIgnoreCase ) ) )
			return wanted;

		for ( var at = 2; ; at++ )
		{
			var next = $"{wanted}_{at}";
			if ( chains.All( c => !string.Equals( c?.Name, next, StringComparison.OrdinalIgnoreCase ) ) )
				return next;
		}
	}

	public static void Draw( Model model, ModelDefinition.IkChain chain, Solved solved, bool selected )
	{
		var size = ModelDocPhysics.DefaultRadius( model );
		var places = new Vector3[solved.Bones.Length];

		for ( var i = 0; i < places.Length; i++ )
			places[i] = ModelDocPhysics.BoneOf( model, solved.Bones[i] ).Position;

		for ( var i = 0; i < places.Length - 1; i++ ) Gizmo.Draw.Line( places[i], places[i + 1] );

		//The ends are what an author named; everything between them came off the skeleton, and is drawn smaller for saying so.
		for ( var i = 0; i < places.Length; i++ )
		{
			var end = i == 0 || i == places.Length - 1;
			Gizmo.Draw.LineSphere( new Sphere( places[i], size * ( end ? 0.5f : 0.7f ) ), end ? 6 : 8 );
		}

		if ( !selected || !chain.UseKneeDirection ) return;

		//Which way the run is pushed as it folds, drawn from the joint it moves most - the one half way along.
		var knee = chain.KneeDirection.Normal;
		if ( knee.IsNearlyZero( 0.0001f ) ) return;

		var from = places[places.Length / 2];

		Gizmo.Draw.Color = Theme.Green.WithAlpha( 0.9f );
		Gizmo.Draw.Line( from, from + ( knee * size * 4.0f ) );
		Gizmo.Draw.LineSphere( new Sphere( from + ( knee * size * 4.0f ), size * 0.25f ), 5 );
	}
}

sealed class BoundsDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.Bounds;
	public override string Icon => "crop_free";

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
		=> items.Add( new ModelDocItem { Kind = Kind, Title = "Bounds" } );

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
		=> ctx.BindDefinition( sheet,
			nameof( ModelDefinition.Surface ),
			nameof( ModelDefinition.Mass ),
			nameof( ModelDefinition.OverrideBounds ),
			nameof( ModelDefinition.Bounds ) );
}

static class ModelDocInspect
{
	public static void BindFolder( ModelDocHandler handler, ModelDocContext ctx, Panel body, string hint )
	{
		body.AddChild( Note( hint ) );

		foreach ( var action in handler.AddActions( ctx ) )
		{
			var run = action.Run;

			var button = body.AddChild<TextButtonPanel>();
			button.Text = action.Title;
			button.Icon = action.Icon;
			button.Pressed = run;
		}
	}

	//A bridge, and meant to go: the two table painters and the materials list are what is left of the widget side of this dock. Named so they can be found and taken out one at a time.
	/// <summary>Puts a widget in a panel, for the last few things here that are still widgets.</summary>
	public static WidgetPanel HostWidget( Panel body, Widget widget )
	{
		var host = body.AddChild<WidgetPanel>();

		host.AddClass( "widget-island" );
		host.Widget = widget;

		return host;
	}

	/// <summary>A line of explanation under a sheet - what this node is for.</summary>
	public static Rinten.UI.Label Note( string text, Color? color = null )
	{
		var label = new Rinten.UI.Label();

		label.AddClass( "note" );
		label.Text = text;

		//Written into the style because a note that is a complaint is red and the rest are not, which is per note rather than per class.
		if ( color is { } tint )
			label.Style.FontColor = tint;

		return label;
	}

	public static List<string> MeshSlotNames( ModelDocContext ctx )
	{
		var names = ctx.Definition.AllMeshes()
			.SelectMany( p => ModelSourceCatalog.Meshes( ctx.Asset, p ) )
			.ToList();

		if ( names.Count > 0 )
			return names;

		var meshes = ctx.Host.CompiledModel?.MeshInfo?.Meshes;
		if ( meshes is null )
			return names;

		foreach ( var mesh in meshes )
		{
			var name = ModelImportCatalog.Bare( mesh.Name );
			if ( !string.IsNullOrWhiteSpace( name ) )
				names.Add( name );
		}

		return names;
	}
}

sealed class MaterialSlotBind
{
	readonly List<string> _list;
	readonly int _index;

	public MaterialSlotBind( List<string> list, int index )
	{
		_list = list;
		_index = index;

		while ( _list.Count <= _index )
			_list.Add( "" );
	}

	[Property, ResourceType( "mat" )]
	public string Material
	{
		get => _index < _list.Count ? _list[_index] ?? "" : "";
		set
		{
			while ( _list.Count <= _index )
				_list.Add( "" );

			_list[_index] = value ?? "";
		}
	}
}

static class ModelDocPhysics
{
	public static string SelectedBone( ModelDocContext ctx )
	{
		var item = ctx.Host.Outliner?.Selected;
		if ( item?.Kind == ModelDocKind.Hull && ModelDocContext.InRange( ctx.Definition.Hulls, item.Index ) )
			return ctx.Definition.Hulls[item.Index].Bone ?? "";
		if ( item?.Kind == ModelDocKind.Joint && ModelDocContext.InRange( ctx.Definition.Joints, item.Index ) )
			return ctx.Definition.Joints[item.Index].BoneA ?? "";
		return "";
	}

	/// <summary>A joint on <paramref name="bone"/>, anchored where it meets the bone above it.</summary>
	public static ModelDefinition.Joint JointOn( ModelDocContext ctx, ModelDefinition.JointKind kind, string bone )
	{
		var joint = new ModelDefinition.Joint { Type = kind, BoneA = bone ?? "" };

		var model = ctx.Host.CompiledModel;
		if ( model is not { IsValid: true } || string.IsNullOrWhiteSpace( bone ) )
			return joint;

		var parent = model.Bones?.GetBone( bone )?.Parent?.Name;
		if ( string.IsNullOrWhiteSpace( parent ) )
			return joint;

		//The pivot is the child's own origin: the parent's origin is a whole bone back.
		joint.BoneA = parent;
		joint.BoneB = bone;
		joint.Position = BoneOf( model, parent ).PointToLocal( BoneOf( model, bone ).Position );
		joint.Rotation = AnchorAngles( model, parent, bone );
		return joint;
	}

	//New joint's anchor frame, aimed down the limb so swing and twist mean what they say
	static Angles AnchorAngles( Model model, string parent, string bone )
	{
		var along = AlongLimb( model, bone );
		if ( along.IsNearZeroLength ) return default;

		//LookAt needs an up that is not the direction it is looking down.
		var up = MathF.Abs( along.Dot( Vector3.Up ) ) > 0.99f ? Vector3.Backward : Vector3.Up;

		return ( BoneOf( model, parent ).Rotation.Inverse * Rotation.LookAt( along, up ) ).Angles();
	}

	//Which way the limb hanging below bone runs. Where its own children sit, which is where the limb goes. A bone at
	//the end of a chain has nothing below it to point at, so that one carries on the way it came from its parent.
	static Vector3 AlongLimb( Model model, string bone )
	{
		if ( model.Bones?.GetBone( bone ) is not { } found ) return Vector3.Zero;

		var origin = BoneOf( model, bone ).Position;

		if ( found.Children is { Count: > 0 } children )
		{
			var below = Vector3.Zero;
			foreach ( var child in children )
				below += BoneOf( model, child.Name ).Position;

			var toChildren = ( below / children.Count ) - origin;
			if ( !toChildren.IsNearZeroLength ) return toChildren.Normal;
		}

		if ( found.Parent is { } above )
		{
			var fromParent = origin - BoneOf( model, above.Name ).Position;
			if ( !fromParent.IsNearZeroLength ) return fromParent.Normal;
		}

		return Vector3.Zero;
	}

	public static float DefaultRadius( Model model )
	{
		if ( model is null || !model.IsValid ) return 0.15f;
		return Math.Clamp( model.Bounds.Size.Length * 0.04f, 0.03f, 0.4f );
	}

	/// <summary>Where a bone actually is while something else drives it - a null answer leaves the compiled rest pose.</summary>
	public static Func<string, Transform?> PosedBone { get; set; }

	//The model's own frame while a simulation moves the whole body; identity otherwise.
	static Transform RootFrame() => PosedBone?.Invoke( "" ) ?? Transform.Zero;

	public static Transform BoneOf( Model model, string bone )
	{
		if ( model is not { IsValid: true } || string.IsNullOrWhiteSpace( bone ) )
			return Transform.Zero;

		return PosedBone?.Invoke( bone ) ?? RestBone( model, bone );
	}

	static Transform RestBone( Model model, string bone )
	{
		var found = model.Bones?.GetBone( bone );
		if ( found is null )
			return Transform.Zero;

		//Strip scale only at the end; the root bone's scale must still apply to child offsets
		return RestInModel( found ).WithScale( 1f );
	}

	//A compiled body's frame, carried to wherever its bone is now.
	static Transform PartFrame( Model model, string bone, Transform frame )
	{
		if ( string.IsNullOrWhiteSpace( bone ) )
			return RootFrame().ToWorld( frame );

		if ( PosedBone?.Invoke( bone ) is not { } posed )
			return frame;

		return posed.ToWorld( RestBone( model, bone ).ToLocal( frame ) );
	}

	//Parent-local rest poses composed into model space.
	static Transform RestInModel( BoneCollection.Bone bone )
	{
		var local = bone.LocalTransform;
		return bone.Parent is null ? local : RestInModel( bone.Parent ).ToWorld( local );
	}

	public static Vector3 WorldAnchor( Model model, ModelDefinition.Joint joint )
	{
		if ( joint.ModelSpace ) return RootFrame().PointToWorld( joint.Position );
		return BoneOf( model, joint.BoneA ).PointToWorld( joint.Position );
	}

	public static Angles WorldTurn( Model model, ModelDefinition.Joint joint )
	{
		if ( joint.ModelSpace ) return ( RootFrame().Rotation * joint.Rotation.ToRotation() ).Angles();
		return ( BoneOf( model, joint.BoneA ).Rotation * joint.Rotation.ToRotation() ).Angles();
	}

	public static void DrawJoint( Model model, ModelDefinition.Joint joint, bool selected = false )
	{
		var origin = WorldAnchor( model, joint );
		var frame = WorldTurn( model, joint ).ToRotation();
		var size = DefaultRadius( model ) * 2.0f;

		Gizmo.Draw.LineSphere( new Sphere( origin, size * 0.12f ), 6 );

		//A ball's cone opens along the frame's forward and twists about it; a hinge turns about its up, which is the axis Box3D reads a revolute joint from.
		var axis = joint.Type == ModelDefinition.JointKind.Hinge ? frame.Up : frame.Forward;
		Gizmo.Draw.Line( origin, origin + axis * size );

		if ( joint.Type != ModelDefinition.JointKind.Ball )
			return;

		//Twist is measured about the anchor frame; the cone is aimed off it by the swing offset. Drawn
		//from the same two frames the solver is given, so what is on screen is what is being simulated.
		if ( joint.EnableSwingLimit )
		{
			var cone = frame * joint.SwingOffset.ToRotation();

			//The offset itself, so it is visible that the cone has been aimed away from the frame rather than the frame simply turned.
			if ( !joint.SwingOffset.AsVector3().IsNearlyZero( 0.01f ) )
				Gizmo.Draw.Line( origin, origin + cone.Forward * size );

			DrawSwingCone( origin, cone, joint.SwingLimit, size, selected );
		}

		if ( joint.EnableTwistLimit )
			DrawTwistArc( origin, frame, joint.MinTwist, joint.MaxTwist, size * 0.55f );
	}

	//Draws the swing cone by rotating the axis, so swings past 90 degrees draw correctly
	static void DrawSwingCone( Vector3 origin, Rotation axis, float swing, float size, bool selected )
	{
		const int steps = 48;

		var lean = Math.Clamp( swing, 0f, 180f );

		Vector3 Rim( int i )
		{
			var around = Rotation.FromAxis( axis.Forward, i / (float)steps * 360f );
			var direction = Rotation.FromAxis( around * axis.Right, lean ) * axis.Forward;
			return origin + direction * size;
		}

		var previous = Rim( 0 );
		for ( var i = 1; i <= steps; i++ )
		{
			var point = Rim( i );
			Gizmo.Draw.Line( previous, point );
			previous = point;
		}

		//Ribs on the two axes always, the diagonals only when this is the joint being edited - every joint drawing eight of these is a haystack.
		var ribs = selected ? 8 : 4;
		for ( var i = 0; i < ribs; i++ )
			Gizmo.Draw.Line( origin, Rim( i * steps / ribs ) );
	}

	//How far the child may twist about the axis, as an arc across the span.
	static void DrawTwistArc( Vector3 origin, Rotation frame, float min, float max, float radius )
	{
		const int steps = 24;

		if ( max - min <= 0.01f ) return;

		Vector3 At( float degrees )
		{
			var turn = Rotation.FromAxis( frame.Forward, degrees );
			return origin + turn * frame.Right * radius;
		}

		var previous = At( min );
		for ( var i = 1; i <= steps; i++ )
		{
			var point = At( min + ( max - min ) * i / steps );
			Gizmo.Draw.Line( previous, point );
			previous = point;
		}

		//The two ends closed back to the axis, so a span reads as a wedge.
		Gizmo.Draw.Line( origin, At( min ) );
		Gizmo.Draw.Line( origin, At( max ) );
	}

	//The one number a radius scales by, when the three axes disagree - there is no "which axis" for a sphere or a capsule's own thickness.
	static float AverageOf( Vector3 scale ) => ( scale.x + scale.y + scale.z ) / 3f;

	/// <summary>Frame a hull is measured from: its bone, or the model transform when it has no bone</summary>
	public static Transform HullFrame( Model model, ModelDefinition.Hull hull, ModelDefinition.ModelTransform transform )
	{
		if ( !string.IsNullOrWhiteSpace( hull.Bone ) )
			return BoneOf( model, hull.Bone );

		return transform is { Enabled: true }
			? RootFrame().ToWorld( new Transform( transform.Position, transform.Rotation.ToRotation() ) )
			: RootFrame();
	}

	/// <summary>Draws a sphere/capsule/box hull where the compiler puts it, scaled the same way</summary>
	public static void DrawHull( Model model, ModelDefinition definition, ModelDefinition.Hull hull, int index,
		bool selected, Action changed, bool resize = false )
	{
		var transform = definition?.Transform;
		var bone = HullFrame( model, hull, transform );
		var scale = transform is { Enabled: true } ? transform.Scale : Vector3.One;
		var radiusScale = AverageOf( scale );
		var inverse = Vector3.One / scale;
		var inverseRadius = radiusScale != 0f ? 1f / radiusScale : 1f;

		switch ( hull )
		{
			case ModelDefinition.SphereHull sphere:
				using ( Gizmo.Scope( $"hull.sphere.{index}", bone.ToWorld( new Transform( sphere.Center * scale ) ) ) )
				{
					var drawRadius = sphere.Radius * radiusScale;
					Gizmo.Draw.LineSphere( new Sphere( 0, drawRadius ) );
					if ( selected && resize && Gizmo.Control.Sphere( $"r.{index}", drawRadius, out var radius, Theme.Primary ) )
					{
						sphere.Radius = Math.Max( 0.01f, radius * inverseRadius );
						changed?.Invoke();
					}
				}
				break;

			case ModelDefinition.CapsuleHull capsule:
				using ( Gizmo.Scope( $"hull.capsule.{index}", bone ) )
				{
					var shape = new Capsule( capsule.PointA * scale, capsule.PointB * scale, capsule.Radius * radiusScale );
					Gizmo.Draw.LineCapsule( shape );
					if ( selected && resize && Gizmo.Control.Capsule( $"c.{index}", shape, out var next, Theme.Primary ) )
					{
						capsule.PointA = next.CenterA * inverse;
						capsule.PointB = next.CenterB * inverse;
						capsule.Radius = Math.Max( 0.01f, next.Radius * inverseRadius );
						changed?.Invoke();
					}
				}
				break;

			case ModelDefinition.BoxHull box:
				using ( Gizmo.Scope( $"hull.box.{index}", bone.ToWorld( new Transform( box.Center * scale, box.Rotation.ToRotation() ) ) ) )
				{
					var extents = box.Extents * scale;
					var bounds = new BBox( -extents, extents );
					Gizmo.Draw.LineBBox( bounds );
					if ( selected && resize && Gizmo.Control.BoundingBox( $"b.{index}", bounds, out var next ) )
					{
						box.Center += box.Rotation.ToRotation() * ( next.Center * inverse );
						box.Extents = next.Size * 0.5f * inverse;
						changed?.Invoke();
					}
				}
				break;

			default:
				if ( model?.Physics is not { Parts.Count: > 0 } physics ) break;

				var part = physics.Parts.FirstOrDefault( p =>
					string.Equals( p.BoneName ?? "", hull.Bone ?? "", StringComparison.OrdinalIgnoreCase ) );
				if ( part is null ) break;

				using ( Gizmo.Scope( $"hull.compiled.{index}", PartFrame( model, part.BoneName, part.Transform ) ) )
				{
					if ( MakesHull( hull ) )
					{
						var shapes = part.Hulls;
						var (start, count) = HullsOf( definition, hull, index, shapes.Count );

						for ( var i = start; i < start + count && i < shapes.Count; i++ )
						{
							var lines = shapes[i].GetLines().ToList();
							if ( lines.Count > 0 ) Gizmo.Draw.Lines( lines );
							else Gizmo.Draw.LineBBox( shapes[i].Bounds );
						}

						break;
					}

					//Anything else is drawn whole; nothing here knows which piece a node stands for.
					foreach ( var compiled in part.Meshes )
						Gizmo.Draw.LineMesh( compiled );
					foreach ( var compiled in part.Spheres )
						Gizmo.Draw.LineSphere( compiled.Sphere );
					foreach ( var compiled in part.Capsules )
						Gizmo.Draw.LineCapsule( compiled.Capsule );
				}
				break;
		}
	}

	//Whether a node compiles to a convex hull; a box does, as its eight corners.
	static bool MakesHull( ModelDefinition.Hull hull )
		=> hull is ModelDefinition.BoxHull or ModelDefinition.RenderHull;

	//Whether a node compiles to a hull per mesh, so to however many that turns out to be.
	static bool MakesMany( ModelDefinition.Hull hull )
		=> hull is ModelDefinition.RenderHull { HasFile: true } or ModelDefinition.RenderHull { PerElement: true };

	//The node's run of compiled hulls, found by counting from both ends of its bone's hulls
	static (int Start, int Count) HullsOf( ModelDefinition definition, ModelDefinition.Hull hull, int index, int total )
	{
		if ( definition?.Hulls is not { Count: > 0 } hulls ) return (0, total);

		var before = 0;
		var after = 0;
		var beforeKnown = true;
		var afterKnown = true;

		for ( var i = 0; i < hulls.Count; i++ )
		{
			if ( i == index ) continue;

			var other = hulls[i];

			if ( other is null || !other.Enabled || !MakesHull( other ) ) continue;
			if ( !string.Equals( other.Bone ?? "", hull.Bone ?? "", StringComparison.OrdinalIgnoreCase ) ) continue;

			var many = MakesMany( other );

			if ( i < index )
			{
				before++;
				beforeKnown &= !many;
			}
			else
			{
				after++;
				afterKnown &= !many;
			}
		}

		var whole = (Start: 0, Count: total);

		//A node of one hull only needs the end it is counted from to be certain.
		if ( !MakesMany( hull ) )
		{
			if ( beforeKnown ) return Within( (before, 1), total, whole );
			if ( afterKnown ) return Within( (total - after - 1, 1), total, whole );

			return whole;
		}

		if ( !beforeKnown || !afterKnown ) return whole;

		return Within( (before, total - before - after), total, whole );
	}

	//A run of shapes, or the whole list where it does not fit inside one.
	static (int Start, int Count) Within( (int Start, int Count) run, int total, (int Start, int Count) whole )
		=> run.Start >= 0 && run.Count > 0 && run.Start + run.Count <= total ? run : whole;
}

//Blend shapes from the art: which ones the model ships, and their preview values
sealed class MorphDoc : ModelDocHandler
{
	public override ModelDocKind Kind => ModelDocKind.Morphs;
	public override IEnumerable<ModelDocKind> AlsoHandles => [ModelDocKind.Morph];
	public override bool CanDelete( ModelDocItem item ) => item.Kind == ModelDocKind.Morph;
	public override bool CanToggle( ModelDocItem item ) => item.Kind == ModelDocKind.Morph;

	public override string IconFor( ModelDocItem item )
		=> item.Kind == ModelDocKind.Morph ? "sentiment_satisfied" : "face_retouching_natural";

	public override void Collect( ModelDocContext ctx, List<ModelDocItem> items )
	{
		var morphs = ctx.Definition.Morphs;
		if ( !ModelDocContext.AddFolder( items, Kind, "Morphs", morphs.Count ) ) return;
		if ( ctx.IsCollapsed( Kind ) ) return;

		for ( var i = 0; i < morphs.Count; i++ )
		{
			var morph = morphs[i];

			items.Add( new ModelDocItem
			{
				Kind = ModelDocKind.Morph,
				Title = string.IsNullOrWhiteSpace( morph.Name ) ? $"Morph {i}" : morph.Name,
				Depth = 1,
				Index = i,
				Enabled = morph.Enabled
			} );
		}
	}

	//What the art has that the model is not carrying yet.
	private static List<string> Unlisted( ModelDocContext ctx )
	{
		var found = new List<string>();

		if ( ctx.Host.CompiledModel is not { IsValid: true } model ) return found;

		for ( var i = 0; i < model.MorphCount; i++ )
		{
			var name = model.GetMorphName( i );
			if ( string.IsNullOrWhiteSpace( name ) ) continue;

			if ( ctx.Definition.Morphs.Any( m => string.Equals( m?.Name, name, StringComparison.OrdinalIgnoreCase ) ) )
				continue;

			found.Add( name );
		}

		return found;
	}

	public override IEnumerable<ModelDocAdd> AddActions( ModelDocContext ctx )
	{
		var unlisted = Unlisted( ctx );

		if ( unlisted.Count > 1 )
		{
			yield return new( "Animation", "movie", $"All {unlisted.Count} Shapes", "done_all", () =>
			{
				foreach ( var name in unlisted )
					ctx.Definition.Morphs.Add( new ModelDefinition.Morph { Name = name } );

				ctx.Host.Outliner.OnStructureChanged( ModelDocKind.Morph, ctx.Definition.Morphs.Count - 1 );
			} );
		}

		foreach ( var name in unlisted )
		{
			var wanted = name;

			yield return new( "Animation", "movie", wanted, "sentiment_satisfied", () =>
			{
				ctx.Definition.Morphs.Add( new ModelDefinition.Morph { Name = wanted } );
				ctx.Host.Outliner.OnStructureChanged( ModelDocKind.Morph, ctx.Definition.Morphs.Count - 1 );
			} );
		}
	}

	public override bool TrySetEnabled( ModelDocContext ctx, ModelDocItem item, bool enabled )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.Morphs, item.Index ) ) return false;

		ctx.Definition.Morphs[item.Index].Enabled = enabled;
		return true;
	}

	public override bool TryDelete( ModelDocContext ctx, ModelDocItem item )
	{
		if ( !ModelDocContext.InRange( ctx.Definition.Morphs, item.Index ) ) return false;

		ctx.Definition.Morphs.RemoveAt( item.Index );
		return true;
	}

	public override System.Collections.IList ListFor( ModelDocContext ctx, ModelDocKind kind )
		=> kind == ModelDocKind.Morph ? ctx.Definition.Morphs : null;

	public override void Bind( ModelDocContext ctx, ModelDocItem item, Inspectors.ControlSheet sheet, Panel body )
	{
		if ( item.IsFolder )
		{
			ModelDocInspect.BindFolder( this, ctx, body,
				"Blend shapes the art brought in. Only the ones listed here are in the compiled model." );
			return;
		}

		if ( !ModelDocContext.InRange( ctx.Definition.Morphs, item.Index ) ) return;

		var morph = ctx.Definition.Morphs[item.Index];
		var known = ctx.Host.CompiledModel is { IsValid: true } model
			&& Enumerable.Range( 0, model.MorphCount )
				.Any( i => string.Equals( model.GetMorphName( i ), morph.Name, StringComparison.OrdinalIgnoreCase ) );

		body.AddChild( ModelDocInspect.Note( known
			? "Drag Preview to see it here. What a game sets is its own business."
			: $"The art has no shape called '{morph.Name}' - it was renamed or the export changed.",
			known ? null : Theme.Yellow.WithAlpha( 0.8f ) ) );

		ctx.BindObject( sheet, morph );
	}
}
