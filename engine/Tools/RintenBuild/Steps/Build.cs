using static Rinten.Build.Constants;

namespace Rinten.Build.Steps;

internal static class Build
{
	internal static ExitCode Run(
		bool clean = false,
		bool skipNative = false,
		bool skipManaged = false,
		bool aot = true )
	{
		//Somebody else's libraries - Slang, SDL - which the repository does not carry and nothing here builds. Before the
		//core, because the shader step that follows the build asks the core for a compiler and the core opens Slang by name.
		if ( new FetchNatives().Run() != ExitCode.Success )
			return ExitCode.Failure;

		//And a headset's: the Android package carries the OpenXR loader, and whoever exports for one is on a
		//desktop - so it is fetched here, where the desktop's are, rather than by a build that runs on no phone.
		if ( new FetchNatives( platformOverride: "android-arm64" ).Run() != ExitCode.Success )
			return ExitCode.Failure;

		//The native core next: the managed build loads it, and the shader step after this one asks it for a compiler.
		if ( !skipNative && new BuildNative( clean ).Run() != ExitCode.Success )
			return ExitCode.Failure;

		if ( !skipManaged && new BuildManaged( clean, aot ).Run() != ExitCode.Success )
			return ExitCode.Failure;

		//The desktop entry last, because it names the binaries the build has just written. Linux only, skipped
		//on a build machine, and never fatal - a menu entry is not worth failing a build over. See DesktopEntry.
		new DesktopEntry( required: false ).Run();

		return ExitCode.Success;
	}
}
