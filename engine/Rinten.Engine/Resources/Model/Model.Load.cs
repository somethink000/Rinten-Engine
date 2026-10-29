using System.IO;
using Rinten.Mounting;

namespace Rinten;

public partial class Model
{
	/// <summary>Load a model by file path.</summary>
	public static Model Load( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		if ( string.IsNullOrWhiteSpace( filename ) )
			return Error;

		filename = filename?.Replace( ".vmdl_c", ".vmdl" );

		//Our own format. Checked before anything else because nothing else knows what to do with
		//it - the legacy loader would return its error model and the reason would be a mystery.
		if ( IsOurs( filename ) )
			return FromCoreCached( filename ) ?? Missing( filename );

		if ( Rinten.Mounting.Directory.TryLoad( filename, out object model ) && model is Model m )
			return m;

		if ( Game.Resources.TryGet<Model>( filename, out var resource ) )
			return resource;

		//A model named the way a ported project names them. Our core knows the four the engine builds itself, and finds the compiled file
		//beside any other - so a project that has been through the importer loads here without anything above it knowing the name changed.
		if ( FromCoreCached( filename ) is { } ours )
			return ours;

		return Missing( filename );
	}

	//Error model that remembers the missing name, so saving a scene doesn't lose the reference
	private static Model Missing( string filename )
	{
		if ( string.IsNullOrWhiteSpace( filename ) ) return Error;

		if ( missing.TryGetValue( filename, out var known ) )
		{
			if ( known is { IsValid: true } ) return known;

			missing.Remove( filename );
		}

		var made = FromCore( CoreModel.Load( ErrorModel ) );

		//The error model itself will not load - see Builtin, which says so once and gives up. Nothing to stand in with, so the old answer stands.
		if ( made is null ) return Error;

		made.MarkMissing( filename );
		missing[filename] = made;

		return made;
	}

	//What a name that found nothing is drawn as.
	private const string ErrorModel = "models/dev/error.mdl";

	//One stand-in per name that found nothing.
	private static readonly Dictionary<string, Model> missing =
		new( StringComparer.OrdinalIgnoreCase );

	//Core-loaded models by requested name, so repeated loads (e.g. gizmos per frame) don't hit disk
	private static readonly Dictionary<string, Model> fromCore =
		new( StringComparer.OrdinalIgnoreCase );

	//When each missed name was last probed; retried after a second so new imports show up
	private static readonly Dictionary<string, float> missedAt =
		new( StringComparer.OrdinalIgnoreCase );

	//How long a name that came back with nothing stays missing.
	private const float MissLasts = 1.0f;

	//Off with -nomodelcache , to measure what it is worth.
	private static readonly bool CacheCoreModels =
		!Rinten.Utility.CommandLine.HasSwitch( "-nomodelcache" );

	//Memoised core model if still valid; reloads keep the same instance
	private static Model FromCoreCached( string filename )
	{
		if ( !CacheCoreModels )
			return FromCore( CoreModel.Load( filename ) );

		if ( fromCore.TryGetValue( filename, out var known ) )
		{
			if ( known is { IsValid: true } )
				return known;

			fromCore.Remove( filename );
		}

		if ( missedAt.TryGetValue( filename, out var when ) && RealTime.Now - when < MissLasts )
			return null;

		var loaded = FromCore( CoreModel.Load( filename ) );

		if ( loaded is not null )
		{
			fromCore[filename] = loaded;
			missedAt.Remove( filename );
		}
		else
		{
			missedAt[filename] = RealTime.Now;
		}

		return loaded;
	}

	/// <summary>Read the compiled file again and keep the same Model instance.</summary>
	public static Model ReloadFromDisk( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		if ( string.IsNullOrWhiteSpace( filename ) )
			return null;

		var targets = fromCore.Values
			.Where( model => model is { IsValid: true } && ( SameModelName( model.Name, filename ) || SameModelName( model.ResourcePath, filename ) ) )
			.Distinct()
			.ToList();

		if ( fromCore.TryGetValue( filename, out var known ) && known is { IsValid: true } && !targets.Contains( known ) )
			targets.Add( known );

		//Also reload stand-ins, so a scene's missing model fixes itself once the model is compiled
		var stoodIn = new List<string>();

		foreach ( var (name, stood) in missing )
		{
			if ( stood is not { IsValid: true } ) continue;
			if ( !SameModelName( name, filename ) ) continue;

			stoodIn.Add( name );

			if ( !targets.Contains( stood ) ) targets.Add( stood );
		}

		if ( targets.Count == 0 )
		{
			var created = FromCore( CoreModel.Load( filename ) );
			if ( created is not null )
				fromCore[filename] = created;

			return created;
		}

		var mended = false;

		foreach ( var target in targets )
		{
			//A compile that has not finished yet is a file that will not load, and the model already in hand is the better answer until it does.
			var fresh = CoreModel.Load( filename );
			if ( fresh is not { IsValid: true } ) continue;

			target.ReplaceCore( fresh );
			target.OnReloaded();

			mended = true;
		}

		//A stand-in that now holds the real thing is not a stand-in. Left in the missing memo it would be handed to
		//the next ask for that name as though nothing had been found, and taken out of it the ask reads the file.
		if ( mended )
		{
			foreach ( var name in stoodIn )
			{
				missing.Remove( name );
				missedAt.Remove( name );
			}
		}

		return targets[0];
	}

	//Same model ignoring extension; the shorter path must be a suffix of the longer, not just file name
	static bool SameModelName( string left, string right )
	{
		if ( string.IsNullOrWhiteSpace( left ) || string.IsNullOrWhiteSpace( right ) )
			return false;

		if ( string.Equals( left, right, StringComparison.OrdinalIgnoreCase ) )
			return true;

		var longer = Bare( left );
		var shorter = Bare( right );

		if ( longer.Length < shorter.Length ) (longer, shorter) = (shorter, longer);

		if ( longer.Length == shorter.Length )
			return string.Equals( longer, shorter, StringComparison.OrdinalIgnoreCase );

		return longer.EndsWith( "/" + shorter, StringComparison.OrdinalIgnoreCase );
	}

	//A model's path, without the extension or the compiled suffix.
	static string Bare( string path )
	{
		var name = path.Replace( '\\', '/' ).TrimStart( '/' );

		if ( name.EndsWith( "_c", StringComparison.OrdinalIgnoreCase ) )
			name = name[..^2];

		var dot = name.LastIndexOf( '.' );
		if ( dot > name.LastIndexOf( '/' ) ) name = name[..dot];

		return name;
	}

	//Forget what has been loaded from the core, so the next ask reads the file again. For the importer: a model
	//rebuilt on disk is a different file under the same name, and the memo has no way of noticing on its own.
	internal static void ForgetCoreModels()
	{
		ThreadSafe.AssertIsMainThread();

		fromCore.Clear();
		missedAt.Clear();

		//The stand-ins too: a name that found nothing is exactly the name a re-import is most likely to have just made a file for.
		missing.Clear();
	}

	//Whether this is a model in our own format rather than the legacy compiled one.
	internal static bool IsOurs( string filename )
		=> filename.EndsWith( ".mdl", StringComparison.OrdinalIgnoreCase )
		|| filename.EndsWith( ".mdl_c", StringComparison.OrdinalIgnoreCase );

	/// <summary>Load a model by file path.</summary>
	public static async Task<Model> LoadAsync( string filename )
	{
		ThreadSafe.AssertIsMainThread();

		if ( string.IsNullOrWhiteSpace( filename ) )
			return Error;

		filename = filename?.Replace( ".vmdl_c", ".vmdl" );

		//Our own format, through the same memo so reloads reach models dropped into the scene
		if ( IsOurs( filename ) )
			return FromCoreCached( filename ) ?? Missing( filename );

		if ( await Rinten.Mounting.Directory.TryLoadAsync( filename ) is Model m )
			return m;

		if ( Game.Resources.TryGet<Model>( filename, out var resource ) )
			return resource;

		using var manifest = AsyncResourceLoader.Load( filename );
		if ( manifest is not null )
		{
			await manifest.WaitForLoad();
		}

		//TODO - make async
		return Load( filename );
	}
}
