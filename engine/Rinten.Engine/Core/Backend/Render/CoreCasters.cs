using System;
using System.Collections.Generic;

namespace Rinten;

//Tracks shadow casters and their changes once a frame, so sun pages and lamp maps know what to rebake
internal sealed class CoreCasters
{
	//One instance whose part in the still world changed: its sphere as it was and as it is - w below nought where it was or is not part of it.
	internal readonly struct Change
	{
		public readonly int Slot;
		public readonly System.Numerics.Vector4 Was, Now;

		public Change( int slot, System.Numerics.Vector4 was, System.Numerics.Vector4 now )
		{
			Slot = slot; Was = was; Now = now;
		}
	}

	private static readonly System.Numerics.Vector4 Nothing = new( 0.0f, 0.0f, 0.0f, -1.0f );

	//Each instance as it was on the last look: its sphere where it casts at all - w below nought where it does not -
	//whether it holds still or moves, and how many times it had been placed, so a change of shape with no move is seen too.
	private System.Numerics.Vector4[] sphere = [];
	private bool[] still = [];
	private bool[] moving = [];

	//Whether the slot was a stand-in on its last look, for when it is gone by the next - see LodStandIn
	private bool[] standIn = [];

	//Whether the frame has a bake, so that what is not static counts as moving - see Look
	private bool movable;
	private uint[] moves = [];
	private int[] triangles = [];

	//The slots that move, and where in that list each is.
	private readonly List<int> movers = new();
	private int[] moverAt = [];

	private long changesSeen = -1;
	private readonly List<int> changed = new();

	//Casters the tables do not hold that moved - the ground under a brush - as world spheres: the last StirredMost,
	//and how many there have ever been. Every drawer's maps read them as changes of the still world; see Stir
	private static readonly List<System.Numerics.Vector4> stirred = new();
	private static long stirredEver;
	private const int StirredMost = 256;
	private long stirredSeen;

	//Something drawn by hand that holds still changed inside a box of the world - the ground was painted or
	//raised - so every page and lamp face that can see the box is baked again, in every drawer, on its next plan.
	internal static void Stir( BBox box )
	{
		var centre = ( box.Mins + box.Maxs ) * 0.5f;
		var radius = ( box.Maxs - box.Mins ).Length * 0.5f;

		lock ( stirred )
		{
			if ( stirred.Count == StirredMost ) stirred.RemoveAt( 0 );
			stirred.Add( new System.Numerics.Vector4( centre.x, centre.y, centre.z, radius ) );
			stirredEver++;
		}
	}

	//This frame's changes to the still world - see Change.
	internal readonly List<Change> Changes = new();

	//Whether this frame's look was at everything rather than at what the log said changed: the first, or one
	//after the drawer fell out of the log. What a consumer derived from the casters is to be worked out again.
	internal bool Whole { get; private set; }

	//How many slots there are to ask about.
	internal int Count { get; private set; }

	//The triangles of everything that moves together - what one map of it can hold at the most.
	internal long MoverTriangles { get; private set; }

	//The slots of everything that moves.
	internal List<int> Movers => movers;

	//What the frame draws by hand and moves - code's models, geometry made on the card - as world spheres. The tables do
	//not hold them, so the drawer says them each frame; they cast into the maps of what moves beside the movers.
	internal readonly List<System.Numerics.Vector4> Hand = new();

	//How many moving casters there are this frame, the tables' and the hand-drawn, and where the one at an index is.
	internal int MovingCount => movers.Count + Hand.Count;

	//One thrown to NaN casts nothing: every test against it fails, and a frustum fitted round it is NaN too
	internal System.Numerics.Vector4 MovingSphere( int at )
	{
		var found = at < movers.Count ? SphereOf( movers[at] ) : Hand[at - movers.Count];
		return float.IsFinite( found.X + found.Y + found.Z + found.W ) ? found : Nothing;
	}

	//The world box of the moving caster at an index, inside its sphere; null for the hand-drawn, which have only spheres
	internal BBox? MovingBox( int at )
		=> at < movers.Count && seen?.EntryAt( movers[at] )?.Renderer is { } renderer && renderer.IsValid() ? renderer.Bounds : null;

	//The tables last looked at, which the boxes are asked of
	private CoreSceneTables seen;

	//Where a slot's caster is - w below nought where it casts nothing.
	internal System.Numerics.Vector4 SphereOf( int slot ) => slot < Count ? sphere[slot] : Nothing;

	//Whether a slot is part of the still world - what a bake holds.
	internal bool Still( int slot ) => slot < Count && still[slot];

	internal int TrianglesOf( int slot ) => slot < Count ? triangles[slot] : 0;

	//What the tables say changed since the last look, into Changes.
	internal void Note( CoreSceneTables tables )
	{
		seen = tables;

		Changes.Clear();
		Whole = false;

		if ( tables is null ) return;

		var count = tables.InstanceCount;

		if ( sphere.Length < count )
		{
			var room = Math.Max( count, sphere.Length * 2 );
			var from = sphere.Length;

			Array.Resize( ref sphere, room );
			Array.Resize( ref still, room );
			Array.Resize( ref moving, room );
			Array.Resize( ref standIn, room );
			Array.Resize( ref moves, room );
			Array.Resize( ref triangles, room );
			Array.Resize( ref moverAt, room );

			for ( var i = from; i < room; i++ )
			{
				sphere[i] = Nothing;
				moverAt[i] = -1;
			}
		}

		Count = count;

		//Everything, where this drawer has fallen out of the tables' log of changes - its first look among them -
		//or where a bake came or went, which decides what of the world is still - see Movable
		var baked = tables.Movable;

		if ( !tables.ChangesSince( ref changesSeen, changed ) | baked != movable )
		{
			movable = baked;
			Whole = true;

			for ( var slot = 0; slot < count; slot++ ) Look( tables, slot );

			return;
		}

		foreach ( var slot in changed )
			if ( slot >= 0 && slot < count ) Look( tables, slot );

		//And what was stirred outside the tables since the last look - see Stir. A drawer that fell further
		//behind than the log keeps looks at everything instead, as it does when it falls out of the tables' log.
		lock ( stirred )
		{
			var behind = stirredEver - stirredSeen;
			stirredSeen = stirredEver;

			if ( behind > stirred.Count )
			{
				Whole = true;
				for ( var slot = 0; slot < count; slot++ ) Look( tables, slot );
				return;
			}

			for ( var i = stirred.Count - (int)behind; i < stirred.Count; i++ )
				Changes.Add( new Change( -1, stirred[i], stirred[i] ) );
		}
	}

	//One instance, against what it was on the last look.
	private void Look( CoreSceneTables tables, int slot )
	{
		var entry = tables.EntryAt( slot );
		ref var record = ref tables.RecordAt( slot );

		//What its clusters draw, or - drawn the classic way - what the drawer draws into
		//the maps beside them: loose instances cast too, see CoreSceneDrawer.DrawLooseView.
		var shown = entry is { Live: true, Mesh: not null } &&
			( entry.Loose || ( ( record.Flags | entry.RunFlags ) & CoreMeshShaders.InstanceDrawn ) != 0 );

		var casts = shown && ( record.Flags & CoreMeshShaders.InstanceNoShadow ) == 0;

		//Moving: posed, deformed, moved since still, or NeverStill; wind does not count. With a bake all that is not static
		//moves too, as UE's movable: a baked light's still world comes from the bake, so the rest casts only into movers
		var nowMoves = casts && ( entry.Posed || ( entry.Loose && ( entry.Skinned || entry.Morphed ) ) ||
			record.Moving != 0 || ( record.Flags & CoreMeshShaders.InstanceNeverStill ) != 0 ||
			( movable && entry.Renderer is { } renderer && renderer.GameObject is { IsStatic: false } ) );

		//All triangles a light's list may hold, including self-moving surfaces the pre-pass skips
		var nowTriangles = casts ? entry.OneSided + entry.TwoSided + entry.CutOut + entry.Moving : 0;

		//What moves, listed - the maps of it are drawn only while there is any - and its triangles: this one's share taken out as it was and put back as it is.
		if ( moving[slot] ) MoverTriangles -= triangles[slot];
		if ( nowMoves ) MoverTriangles += nowTriangles;
		if ( MoverTriangles < 0 ) MoverTriangles = 0;

		if ( nowMoves != moving[slot] )
		{
			if ( nowMoves )
			{
				moverAt[slot] = movers.Count;
				movers.Add( slot );
			}
			else
			{
				var at = moverAt[slot];
				var last = movers[^1];
				movers[at] = last;
				moverAt[last] = at;
				movers.RemoveAt( movers.Count - 1 );
				moverAt[slot] = -1;
			}

			moving[slot] = nowMoves;
		}

		var nowSphere = casts ? record.Sphere : Nothing;
		var nowStill = casts && !nowMoves;

		//The still world, where it changed: where it was and where it is now are both to be baked again, by whoever keeps depth of it.
		var was = still[slot] ? sphere[slot] : Nothing;
		var now = nowStill ? nowSphere : Nothing;

		//A stand-in for a surface coming or going is that surface at another detail, not a change to it - see LodStandIn
		var isStandIn = entry?.LodStandIn ?? standIn[slot];
		var swapped = isStandIn && ( was.W < 0.0f ) != ( now.W < 0.0f );
		standIn[slot] = isStandIn;

		if ( !swapped && ( was != now || ( nowStill && moves[slot] != record.MoveCount ) ) )
			Changes.Add( new Change( slot, was, now ) );

		sphere[slot] = nowSphere;
		still[slot] = nowStill;
		moves[slot] = record.MoveCount;
		triangles[slot] = nowTriangles;
	}
}
