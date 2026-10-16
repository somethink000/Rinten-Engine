using Rinten.Animation;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Rinten;

/// <summary>A model scene object that supports animations and can be rendered within a <see cref="SceneWorld"/>.</summary>
public sealed partial class SceneModel : SceneObject
{
	public record struct FootstepEvent( int FootId, Transform Transform, float Volume, string AttachmentName );

	public record struct GenericEvent( string Type, int Int, float Float, string String, Vector3 Vector );

	public record struct SoundEvent( string Name, Vector3 Position, string AttachmentName );

	public record struct AnimTagEvent( string Name, AnimTagStatus Status );

	/// <summary>Called when a footstep event happens</summary>
	public Action<FootstepEvent> OnFootstepEvent { get; set; }

	/// <summary>Called when a generic event happens</summary>
	public Action<GenericEvent> OnGenericEvent { get; set; }

	/// <summary>Called when a sound event happens</summary>
	public Action<SoundEvent> OnSoundEvent { get; set; }

	/// <summary>Called when a anim tag event happens</summary>
	public Action<AnimTagEvent> OnAnimTagEvent { get; set; }

	/// <summary>Enumeration that describes how the AnimGraph tag state changed. Used in <see cref="AnimTagEvent"/>.</summary>
	public enum AnimTagStatus
	{
		/// <summary>Tag was activated and deactivated on the same frame</summary>
		Fired = 0,

		/// <summary>The tag has become active</summary>
		Start,

		/// <summary>The tag has become inactive</summary>
		End
	}

	//How little of the final pose a clip can account for and still be heard. A clip blended almost out still marks what it marked, and a game that
	//fired a reload sound off a layer fading away at a thousandth would hear it twice. Anything with weight worth calling animation is above this.
	private const float Audible = 0.01f;

	//What was in the middle of being said at the last dispatch, so the same mark held across ten frames is one event rather than ten.
	private readonly HashSet<(GraphContext.SampledKind Kind, Rinten.CoreAnimation.AnimClipData Clip, int Index, string Id)> saying = new();

	//Tags that were active at the last dispatch, for the same reason.
	private readonly HashSet<string> tagged = new();

	//This dispatch's own, kept between frames rather than made each time
	private readonly HashSet<(GraphContext.SampledKind Kind, Rinten.CoreAnimation.AnimClipData Clip, int Index, string Id)> still = new();
	private readonly HashSet<string> active = new();
	private readonly List<string> gone = new();

	/// <summary>Fires animation events that started since last frame; edge-triggered so a long mark fires once</summary>
	public void RunPendingEvents()
	{
		var marks = Marks();

		if ( marks is null )
		{
			saying.Clear();
			return;
		}

		still.Clear();

		for ( var i = 0; i < marks.Count; i++ )
		{
			var mark = marks[i];

			if ( mark.Weight < Audible ) continue;

			if ( mark.Kind is not ( GraphContext.SampledKind.Generic or GraphContext.SampledKind.Foot ) )
				continue;

			var key = ( mark.Kind, mark.Clip, mark.Index, mark.Id );

			still.Add( key );

			//Already going when the last frame was dispatched, so it has been said once already.
			if ( !saying.Add( key ) ) continue;

			if ( mark.Kind == GraphContext.SampledKind.Foot )
			{
				OnFootstepEvent?.Invoke( new FootstepEvent( mark.Data, Transform, mark.Weight, "" ) );
				continue;
			}

			OnGenericEvent?.Invoke( new GenericEvent( mark.Id, mark.Data, mark.Fraction, mark.Id, Vector3.Zero ) );
		}

		saying.IntersectWith( still );
	}

	/// <summary>Tag start/stop events; a span shorter than the frame is reported as fired</summary>
	public void DispatchTagEvents()
	{
		var marks = Marks();

		if ( marks is null )
		{
			foreach ( var name in tagged )
				OnAnimTagEvent?.Invoke( new AnimTagEvent( name, AnimTagStatus.End ) );

			tagged.Clear();
			return;
		}

		active.Clear();

		for ( var i = 0; i < marks.Count; i++ )
		{
			var mark = marks[i];

			if ( mark.Weight < Audible ) continue;
			if ( mark.Kind != GraphContext.SampledKind.Tag ) continue;
			if ( string.IsNullOrEmpty( mark.Id ) ) continue;

			active.Add( mark.Id );
		}

		foreach ( var name in active )
		{
			if ( tagged.Contains( name ) ) continue;

			OnAnimTagEvent?.Invoke( new AnimTagEvent( name, AnimTagStatus.Start ) );
		}

		//Gone since the last dispatch, listed before any is told: the callback is the game's, and it may ask this object about its tags
		gone.Clear();

		foreach ( var name in tagged )
		{
			if ( !active.Contains( name ) ) gone.Add( name );
		}

		foreach ( var name in gone ) OnAnimTagEvent?.Invoke( new AnimTagEvent( name, AnimTagStatus.End ) );

		tagged.Clear();

		foreach ( var name in active ) tagged.Add( name );
	}

	//What the graph is saying, for code that would rather ask than be told - which is most of it: a weapon asks whether it is in the middle of a reload far more often than it wants telling that one began.
	/// <summary>Whether a tag is active on this model right now.</summary>
	public bool HasAnimTag( string name ) => !string.IsNullOrEmpty( name ) && tagged.Contains( name );

	/// <summary>Every tag active on this model right now.</summary>
	public IEnumerable<string> AnimTags => tagged;

	//What the playing clips were in the middle of when they were last posed.
	private IReadOnlyList<GraphContext.Sampled> Marks()
		=> Animation?.RunningGraph?.Context?.Events;
}
