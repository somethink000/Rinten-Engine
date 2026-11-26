namespace Rinten;

//Compiler hook installed by Rinten.Code; null in builds without a compiler, e.g. the web player
internal abstract class ProjectCompiling
{
	internal static ProjectCompiling Current { get; set; }

	//A project's compilers, made empty - see ProjectCode.Update.
	internal abstract ProjectCode For( Project project );

	//Whether the group every local project builds in has been made. Asked every frame.
	internal abstract bool HasGroup { get; }

	//Lets go of that group; whatever compiles next makes another.
	internal abstract void DropGroup();

	//Builds the group where something in it changed and it is not building already.
	internal abstract void BuildIfNeeded();

	//Builds the group, after waiting out a build already running.
	internal abstract Task<bool> CompileAsync();

	//What stopped the last build, a line for every diagnostic worse than a warning.
	internal abstract string ErrorsText();

	//A remote package's code archives compiled here, for a package that shipped no assemblies - mounted onto its assembly filesystem as if it had.
	internal abstract Task<bool> CompileCodeArchives( PackageManager.ActivePackage package );

	//The code archives a server sends a client that joins it, to be compiled here.
	internal abstract IServerCode NewServerCode();

	//A game assembly's code archive with everything behind SERVER taken out, for a dedicated server's clients - or null where no compiler here built it.
	internal abstract byte[] WithoutServerCode( string assemblyName );

	//The editor's fast hotload for one package loader: a changed method's body swapped into the running assembly in place of loading another - see
	//PackageLoader.TryFastHotload. It patches methods with MonoMod, which a build that never compiles has no use for, so it lives with the compiler.
	internal abstract IFastHotload NewFastHotload( string name );
}

//Swaps changed methods into a running assembly - see ProjectCompiling.NewFastHotload.
internal interface IFastHotload : IDisposable
{
	//The bodies of what changed between oldAssembly and newAssembly patched into
	//baseAssembly's methods. False where that cannot be done and a full hotload is needed.
	bool Replace( System.Reflection.Assembly baseAssembly, System.Reflection.Assembly oldAssembly, System.Reflection.Assembly newAssembly );

	//Undo and forget the detours applied to an assembly a full hotload is about to replace, so they
	//don't keep it loaded for the rest of the session.
	void RemoveDetours( System.Reflection.Assembly assembly );
}

//One project's compilers: its game code's and its editor code's.
internal abstract class ProjectCode
{
	internal abstract bool HasCompiler { get; }

	//Makes, remakes or drops the compilers to match the project's config and code.
	internal abstract void Update();

	//Lets go of them; the next update makes them again.
	internal abstract void Forget();

	//A fast hotload of what one of them built: its reference rolls back to the last version that was not
	//fast hotloaded, which is what the compilers that reference it may build against - see PackageLoader.
	internal abstract void NotifyFastHotload( bool editorCode, System.Version version );
}

//The code archives a server sends, compiled as they arrive - see ProjectCompiling.NewServerCode.
internal interface IServerCode
{
	//Whether an archive has changed since the last build.
	bool NeedsBuild { get; }

	//Takes in one archive, as it came over the network, to be built with the rest.
	void Update( byte[] archive );

	//Builds what changed and loads what it made into enroller. False where it did not build; the reason is in the log.
	bool BuildAndLoad( PackageLoader.Enroller enroller );
}
