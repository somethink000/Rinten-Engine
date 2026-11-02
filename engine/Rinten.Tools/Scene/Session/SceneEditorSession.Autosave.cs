using System;
using System.Diagnostics;

namespace Editor;

/// <summary>Keeping a scene on the disk without anybody having to ask for it.</summary>
public partial class SceneEditorSession
{
	//How long the scene has to be left alone before it is written out. Long enough that a drag, a nudge and a typed number are
	//one write rather than thirty; short enough that walking away from the machine for a moment leaves the work on the disk.
	private const float QuietBeforeWriting = 1.5f;

	//The journal already holds every edit, so a slow write can wait: writing takes at most this share of the editor's time.
	private const double ShareOfTimeWriting = 0.05;

	//How long the last edit has to be the last one before it is passed on. Shorter than the wait before writing, because this is what
	//keeps a prefab's instances looking like the prefab and somebody is watching it happen. Long enough that a drag is one of them.
	private const float QuietBeforePropagating = 0.25f;

	//Since the last edit that made this scene want writing.
	private RealTimeSince sinceEdited;

	//Whether the edits to this session have stopped coming.
	internal bool EditsHaveSettled => sinceEdited >= QuietBeforePropagating;

	//How long the last checkpoint took, in milliseconds.
	internal double LastCheckpointMilliseconds { get; private set; }

	//What the last save spent making the resource out of the scene.
	internal double LastResourceMilliseconds { get; private set; }

	//And what it spent turning that resource into the text on the disk.
	internal double LastWriteMilliseconds { get; private set; }

	//Whether this session has already said it has nowhere to be written.
	private bool saidWhyNot;

	//Only scenes saved once have a path to autosave to; we never pop a file dialog for this
	private bool CanCheckpoint
	{
		get
		{
			if ( IsMounted ) return false;
			if ( Scene is null ) return false;
			if ( Scene.Source is null ) return false;

			return AssetSystem.FindByPath( Scene.Source.ResourcePath ) is not null;
		}
	}

	/// <summary>Whether there is work here that is on no disk anywhere.</summary>
	public bool WorkIsOnlyInMemory => HasUnsavedChanges && !CanCheckpoint;

	/// <summary>Writes the scene out if it has been edited and left alone since.</summary>
	[EditorEvent.Frame]
	public void CheckpointWhenQuiet()
	{
		if ( !HasUnsavedChanges ) return;
		if ( sinceEdited < Math.Max( QuietBeforeWriting, LastCheckpointMilliseconds / 1000.0 / ShareOfTimeWriting ) ) return;

		Checkpoint();
	}

	//Everything that wants the scene on the disk calls this: the quiet moment above, the window losing focus, the session closing, the editor closing, and ctrl+S for the fingers that still press it.
	/// <summary>Writes the scene out now, if there is anything to write and somewhere to write it.</summary>
	public void Checkpoint()
	{
		if ( !HasUnsavedChanges ) return;

		if ( !CanCheckpoint )
		{
			//Once. A scene that cannot be written keeps the star beside its name for the rest of the session, and there is otherwise
			//nothing at all to say why - it looks like the writing is broken rather than like there being nowhere to write to.
			if ( !saidWhyNot )
			{
				saidWhyNot = true;

				Log.Info( $"scene '{Scene?.Name}' is not being written: " +
					( Scene?.Source is null
						? "it has never been saved, so there is nowhere to write it"
						: $"'{Scene.Source.ResourcePath}' is not in the asset system" ) );
			}

			return;
		}

		var started = Stopwatch.GetTimestamp();

		//Serialised here, flushed to the disk on its own - see ResourceWrites. The flush is what a save mostly costs
		//and none of what it means: a scene measured 52ms in one frame, and making the text was a small part of that.
		Save( false, background: true );

		var written = Stopwatch.GetElapsedTime( started ).TotalMilliseconds;

		//The file holds everything the journal was holding, so the journal can start again from it.
		RebaseHistory();

		LastCheckpointMilliseconds = Stopwatch.GetElapsedTime( started ).TotalMilliseconds;

		//Log slow checkpoints under -uitrace only; nearly every checkpoint exceeds 8 ms
		if ( tracing && LastCheckpointMilliseconds > 8.0 )
		{
			Log.Info( $"scene '{Scene?.Name}' checkpointed in {LastCheckpointMilliseconds:0.#}ms " +
				$"(serialise {written:0.#}ms - resource {LastResourceMilliseconds:0.#}ms, " +
				$"text {LastWriteMilliseconds:0.#}ms - journal {LastCheckpointMilliseconds - written:0.#}ms)" );
		}
	}

	private static readonly bool tracing = Rinten.Utility.CommandLine.HasSwitch( "-uitrace" );

	/// <summary>Writes out every session that has something to write.</summary>
	public static void CheckpointAll()
	{
		foreach ( var session in All.ToArray() )
		{
			try
			{
				session?.Checkpoint();
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"Could not write scene '{session?.Scene?.Name}'" );
			}
		}
	}
}
