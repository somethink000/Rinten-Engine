using System.Collections.Generic;
using Editor;

namespace EngineTests;

//Export platforms decide native library sources and executable names for each target
[TestClass]
public class ExportPlatformTest
{
	//Every platform names a bin folder the engine actually lays out, and the three are distinct
	//- two rows pointing at one folder would silently ship one platform's libraries as another's.
	[TestMethod]
	public void EachPlatformHasItsOwnBinFolder()
	{
		var folders = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );

		foreach ( var platform in ExportPlatform.All )
		{
			Assert.IsFalse( string.IsNullOrWhiteSpace( platform.Id ), "a platform with no id" );
			Assert.IsFalse( string.IsNullOrWhiteSpace( platform.RuntimeIdentifier ),
				$"{platform.Id} has no runtime identifier to publish against" );

			Assert.IsTrue( folders.Add( platform.BinFolder ),
				$"{platform.Id} shares a bin folder with another platform" );
		}

		Assert.AreEqual( 6, ExportPlatform.All.Count, "Windows, Linux, Linux Arm, macOS, Web and Android" );
	}

	//What a build's folder is named after, which is what somebody reads in a folder of builds
	//- and not the bin folder's id, which is the engine's own spelling of the same platform.
	[TestMethod]
	public void EachPlatformNamesItsBuildFolder()
	{
		var names = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );

		foreach ( var platform in ExportPlatform.All )
		{
			Assert.IsFalse( string.IsNullOrWhiteSpace( platform.Folder ),
				$"{platform.Id} has nothing to name a build folder after" );

			Assert.AreEqual( -1, platform.Folder.IndexOfAny( System.IO.Path.GetInvalidFileNameChars() ),
				$"{platform.Folder} cannot be a folder name" );

			Assert.IsTrue( names.Add( platform.Folder ),
				$"{platform.Id} shares a build folder name with another platform" );
		}

		Assert.AreEqual( "win64", ExportPlatform.Windows.Folder );
		Assert.AreEqual( "linux64", ExportPlatform.Linux.Folder );
		Assert.AreEqual( "macos", ExportPlatform.MacOS.Folder );
		Assert.AreEqual( "web", ExportPlatform.Web.Folder );
		Assert.AreEqual( "android", ExportPlatform.Android.Folder );
	}

	//Only Windows decorates a program's name, and only Windows keeps the icon inside the program.
	[TestMethod]
	public void OnlyWindowsNamesItsProgramsWithAnExtension()
	{
		Assert.AreEqual( "MyGame.exe", ExportPlatform.Windows.Executable( "MyGame" ) );
		Assert.AreEqual( "MyGame", ExportPlatform.Linux.Executable( "MyGame" ) );
		Assert.AreEqual( "MyGame", ExportPlatform.MacOS.Executable( "MyGame" ) );
		Assert.AreEqual( "MyGame.html", ExportPlatform.Web.Executable( "MyGame" ) );
		Assert.AreEqual( "MyGame.apk", ExportPlatform.Android.Executable( "MyGame" ) );

		Assert.IsTrue( ExportPlatform.Windows.HasExecutableIcon );
		Assert.IsFalse( ExportPlatform.Linux.HasExecutableIcon,
			"a Linux program carries its icon in the desktop entry beside it" );
		Assert.IsFalse( ExportPlatform.MacOS.HasExecutableIcon,
			"a Mac keeps it in the bundle" );
		Assert.IsFalse( ExportPlatform.Web.HasExecutableIcon,
			"a page is not a program" );
		Assert.IsFalse( ExportPlatform.Android.HasExecutableIcon,
			"an apk carries its icon as a resource" );
	}

	//The bin folders here are the ones the engine's own loader looks in. If the two ever disagree, an
	//exported game is given libraries it will not look for - so they are compared rather than trusted.
	[TestMethod]
	public void TheBinFoldersAreTheOnesTheEngineLoadsFrom()
	{
		//Written the way the loader writes them: forward slashes and a trailing separator (Rinten.NetCore.PathFor).
		Assert.AreEqual( "bin/win64", ExportPlatform.Windows.BinFolder.Replace( '\\', '/' ) );
		Assert.AreEqual( "bin/linuxsteamrt64", ExportPlatform.Linux.BinFolder.Replace( '\\', '/' ) );
		Assert.AreEqual( "bin/osxarm64", ExportPlatform.MacOS.BinFolder.Replace( '\\', '/' ) );
		Assert.AreEqual( "bin/wasm32", ExportPlatform.Web.BinFolder.Replace( '\\', '/' ) );
		Assert.AreEqual( "bin/android-arm64", ExportPlatform.Android.BinFolder.Replace( '\\', '/' ) );
	}

	//An id nobody recognises is the machine this is running on rather than a null - an export
	//with no platform at all is the one outcome that cannot be recovered from further down.
	[TestMethod]
	public void AnUnknownPlatformIsTheHost()
	{
		Assert.AreEqual( ExportPlatform.Windows, ExportPlatform.FromId( "WIN64" ),
			"the id is matched without case" );

		Assert.AreEqual( ExportPlatform.Host, ExportPlatform.FromId( "playstation" ) );
		Assert.AreEqual( ExportPlatform.Host, ExportPlatform.FromId( null ) );

		Assert.IsTrue( ExportPlatform.All.Contains( ExportPlatform.Host ),
			"the host has to be one of the desktops" );
	}
}
