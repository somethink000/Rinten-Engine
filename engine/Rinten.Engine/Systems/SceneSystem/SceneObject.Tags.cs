using System.Collections.Frozen;

namespace Rinten;

public partial class SceneObject
{
	/// <summary>The tags this object carries. Used for filtering what a camera draws and what a light lights.</summary>
	public ITagSet Tags { get; private set; }

	internal partial class Internal
	{
		//Scene object tags as interned tokens; mirrored to the native side while one still exists
		internal class SceneObjectTags : ITagSet
		{
			readonly SceneObject SceneObject;
			readonly HashSet<uint> tokens = new();

			//Counts every change to any scene object's tags, so whoever keeps an answer worked out
			//from them - which a camera draws, see CoreSceneTables.Hidden - knows when it is stale.
			internal static int Version;

			//Cached frozen set; traces query tags per object every frame, so building one each time is too slow
			IReadOnlySet<uint> frozen;

			internal SceneObjectTags( SceneObject sceneObject )
			{
				SceneObject = sceneObject;
			}

			public override void Add( string tag )
			{
				var token = StringToken.FindOrCreate( tag );

				if ( !tokens.Add( token ) ) return;

				System.Threading.Interlocked.Increment( ref Version );

				frozen = null;
				//No-op: nothing to hand it to.
			}

			public override IEnumerable<string> TryGetAll()
			{
				foreach ( var token in tokens )
				{
					yield return StringToken.GetValue( token );
				}
			}

			/// <summary>Try to get all tags in the set.</summary>
			public override IReadOnlySet<uint> GetTokens() => frozen ??= tokens.ToFrozenSet();

			public override bool Has( string tag ) => tokens.Contains( StringToken.FindOrCreate( tag ) );

			public override void Remove( string tag )
			{
				var token = StringToken.FindOrCreate( tag );

				if ( !tokens.Remove( token ) ) return;

				System.Threading.Interlocked.Increment( ref Version );

				frozen = null;
				//No-op: nothing to hand it to.
			}

			public override void RemoveAll()
			{
				tokens.Clear();
				frozen = null;

				System.Threading.Interlocked.Increment( ref Version );
				//No-op: nothing to hand it to.
			}

			//By token, and nothing at all where the two already agree - a pooled object takes its owner's tags every time it is handed out
			public override void SetFrom( ITagSet set )
			{
				if ( set is null || set == this ) return;

				var wanted = ITagSet.Copy( set.GetTokens() );

				if ( wanted.Length == tokens.Count )
				{
					var same = true;

					foreach ( var token in wanted )
					{
						if ( tokens.Contains( token ) ) continue;

						same = false;
						break;
					}

					if ( same ) return;
				}

				tokens.Clear();
				foreach ( var token in wanted ) tokens.Add( token );

				frozen = null;
				System.Threading.Interlocked.Increment( ref Version );
			}
		}
	}
}
