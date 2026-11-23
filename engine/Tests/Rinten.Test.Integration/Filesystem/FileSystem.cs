using System;
using System.Linq;
using Rinten.Diagnostics;

namespace FilesystemTests;

[TestClass]
public partial class FileSystemTest
{
	[TestInitialize]
	public void TestInitialize()
	{
		Logging.Enabled = true;

		if ( System.IO.Directory.Exists( ".rinten/TestFolder" ) )
			System.IO.Directory.Delete( ".rinten/TestFolder", true );

		System.IO.Directory.CreateDirectory( ".rinten/TestFolder" );

		Rinten.EngineFileSystem.Shutdown();
		Rinten.EngineFileSystem.Initialize( System.IO.Path.GetFullPath( ".rinten/TestFolder" ), true );

		Rinten.EngineFileSystem.Root.WriteAllText( "root_text_file.txt", "Hello" );

		Rinten.EngineFileSystem.Root.CreateDirectory( "Addons/Red" );
		Rinten.EngineFileSystem.Root.WriteAllText( "Addons/Red/red.txt", "Red" );
		Rinten.EngineFileSystem.Root.WriteAllText( "Addons/Red/common.txt", "Red" );

		Rinten.EngineFileSystem.Root.CreateDirectory( "Addons/Green" );
		Rinten.EngineFileSystem.Root.WriteAllText( "Addons/Green/green.txt", "Green" );
		Rinten.EngineFileSystem.Root.WriteAllText( "Addons/Green/common.txt", "Green" );

		Rinten.EngineFileSystem.Root.CreateDirectory( "Addons/Blue" );
		Rinten.EngineFileSystem.Root.WriteAllText( "Addons/Blue/blue.txt", "Blue" );
		Rinten.EngineFileSystem.Root.WriteAllText( "Addons/Blue/common.txt", "Blue" );
	}

	[TestCleanup]
	public void TestCleanup()
	{
		Rinten.EngineFileSystem.Shutdown();

		if ( System.IO.Directory.Exists( ".rinten/TestFolder" ) )
			System.IO.Directory.Delete( ".rinten/TestFolder", true );

		Rinten.EngineFileSystem.Initialize( Environment.CurrentDirectory );
	}

	[TestMethod]
	public void FindDirectory()
	{
		var paths = Rinten.EngineFileSystem.Root.FindDirectory( "/", "*", false ).ToArray();

		foreach ( var path in paths )
		{
			Console.WriteLine( path );
		}

		Assert.IsTrue( paths.Contains( "Addons" ) );
		Assert.IsFalse( paths.Contains( "Addons/Blue" ) );
		Assert.IsFalse( paths.Contains( "Addons/Red" ) );
		Assert.IsFalse( paths.Contains( "Addons/Green" ) );
	}

	[TestMethod]
	public void FindDirectoryFolder()
	{
		var paths = Rinten.EngineFileSystem.Root.FindDirectory( "/Addons", "*", false ).ToArray();

		foreach ( var path in paths )
		{
			Console.WriteLine( path );
		}

		Assert.IsTrue( paths.Contains( "Blue" ) );
		Assert.IsTrue( paths.Contains( "Red" ) );
		Assert.IsTrue( paths.Contains( "Green" ) );
	}

	[TestMethod]
	public void FindDirectoryRecursive()
	{
		var paths = Rinten.EngineFileSystem.Root.FindDirectory( "/", "*", true ).ToArray();

		foreach ( var path in paths )
		{
			Console.WriteLine( path );
		}

		Assert.IsTrue( paths.Contains( "Addons/Green" ) );
		Assert.IsTrue( paths.Contains( "Addons" ) );
		Assert.IsTrue( paths.Contains( "Addons/Blue" ) );
		Assert.IsTrue( paths.Contains( "Addons/Red" ) );
	}

	[TestMethod]
	public void FindDirectoryRecursiveSearch()
	{
		var paths = Rinten.EngineFileSystem.Root.FindDirectory( "/", "Red", true ).ToArray();

		foreach ( var path in paths )
		{
			Console.WriteLine( path );
		}

		Assert.IsFalse( paths.Contains( "Addons/Green" ) );
		Assert.IsFalse( paths.Contains( "Addons" ) );
		Assert.IsFalse( paths.Contains( "Addons/Blue" ) );
		Assert.IsTrue( paths.Contains( "Addons/Red" ) );
	}

	[TestMethod]
	public void FindFile()
	{
		var paths = Rinten.EngineFileSystem.Root.FindFile( "/", "*", false ).ToArray();

		foreach ( var path in paths )
		{
			Console.WriteLine( path );
		}

		Assert.IsTrue( paths.Length == 1 );
		Assert.IsTrue( paths.Contains( "root_text_file.txt" ) );
	}

	[TestMethod]
	public void FindFileRecursive()
	{
		var paths = Rinten.EngineFileSystem.Root.FindFile( "/", "*", true ).ToArray();

		foreach ( var path in paths )
		{
			Console.WriteLine( path );
		}

		Assert.IsTrue( paths.Contains( "root_text_file.txt" ) );
		Assert.IsTrue( paths.Contains( "Addons/Blue/blue.txt" ) );
		Assert.IsTrue( paths.Contains( "Addons/Blue/common.txt" ) );
		Assert.IsTrue( paths.Contains( "Addons/Green/common.txt" ) );
		Assert.IsTrue( paths.Contains( "Addons/Green/green.txt" ) );
		Assert.IsTrue( paths.Contains( "Addons/Red/common.txt" ) );
		Assert.IsTrue( paths.Contains( "Addons/Red/red.txt" ) );
		Assert.IsFalse( paths.Contains( "Addons/Red" ) );
	}

	[TestMethod]
	public void FindFileRecursiveSearch()
	{
		var paths = Rinten.EngineFileSystem.Root.FindFile( "/", "red.tx*", true ).ToArray();

		foreach ( var path in paths )
		{
			Console.WriteLine( path );
		}

		Assert.IsTrue( paths.Contains( "Addons/Red/red.txt" ) );
		Assert.IsFalse( paths.Contains( "root_text_file.txt" ) );
		Assert.IsFalse( paths.Contains( "Addons/Blue/blue.txt" ) );
		Assert.IsFalse( paths.Contains( "Addons/Blue/common.txt" ) );
		Assert.IsFalse( paths.Contains( "Addons/Green/common.txt" ) );
		Assert.IsFalse( paths.Contains( "Addons/Green/green.txt" ) );
		Assert.IsFalse( paths.Contains( "Addons/Red/common.txt" ) );
		Assert.IsFalse( paths.Contains( "Addons/Red" ) );
	}

	[TestMethod]
	public void FindFileRecursiveSearchFolder()
	{
		var paths = Rinten.EngineFileSystem.Root.FindFile( "/Addons/", "red.tx*", true ).ToArray();

		foreach ( var path in paths )
		{
			Console.WriteLine( path );
		}

		Assert.IsTrue( paths.Length == 1 );
		Assert.IsTrue( paths.Contains( "Red/red.txt" ) );
	}

	[TestMethod]
	public void Exists()
	{
		Assert.IsTrue( Rinten.EngineFileSystem.Root.FileExists( "/root_text_file.txt" ) );
		Assert.IsTrue( Rinten.EngineFileSystem.Root.DirectoryExists( "/Addons" ) );

		Assert.IsFalse( Rinten.EngineFileSystem.Root.FileExists( "/missing_text_file.txt" ) );
		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "/Windows" ) );

		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "/mnt/c/Windows" ) );
		Assert.ThrowsException<NotSupportedException>( () => Rinten.EngineFileSystem.Root.DirectoryExists( "c:/Windows" ) );
		Assert.ThrowsException<ArgumentException>( () => Rinten.EngineFileSystem.Root.DirectoryExists( "/.." ) );
	}

	[TestMethod]
	public void MountedFolders()
	{
		var fs = new Rinten.AggregateFileSystem();

		var blue = fs.CreateAndMount( Rinten.EngineFileSystem.Root, "Addons/Blue" );
		var red = fs.CreateAndMount( Rinten.EngineFileSystem.Root, "Addons/Red" );
		var green = fs.CreateAndMount( Rinten.EngineFileSystem.Root, "Addons/Green" );

		Assert.IsTrue( fs.FileExists( "red.txt" ) );
		Assert.IsTrue( fs.FileExists( "green.txt" ) );
		Assert.IsTrue( fs.FileExists( "blue.txt" ) );

		Assert.AreEqual( fs.ReadAllText( "red.txt" ), "Red" );
		Assert.AreEqual( fs.ReadAllText( "green.txt" ), "Green" );
		Assert.AreEqual( fs.ReadAllText( "blue.txt" ), "Blue" );

		Assert.IsTrue( fs.FileExists( "common.txt" ) );
		Assert.AreEqual( fs.ReadAllText( "common.txt" ), "Green" );
	}

	[TestMethod]
	public void CreateDeleteDirectories()
	{
		Rinten.EngineFileSystem.Root.CreateDirectory( "NewFolder" );
		Assert.IsTrue( Rinten.EngineFileSystem.Root.DirectoryExists( "NewFolder" ) );

		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "A" ) );
		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C" ) );
		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C/D/E" ) );
		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C/D/E/F" ) );
		Rinten.EngineFileSystem.Root.CreateDirectory( "A/B/C/D/E/F" );
		Assert.IsTrue( Rinten.EngineFileSystem.Root.DirectoryExists( "A" ) );
		Assert.IsTrue( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C" ) );
		Assert.IsTrue( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C/D/E" ) );
		Assert.IsTrue( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C/D/E/F" ) );

		Rinten.EngineFileSystem.Root.CreateDirectory( "NewFolder" );

		Rinten.EngineFileSystem.Root.DeleteDirectory( "NewFolder" );
		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "NewFolder" ) );

		Rinten.EngineFileSystem.Root.DeleteDirectory( "A/B/C", true );
		Assert.IsTrue( Rinten.EngineFileSystem.Root.DirectoryExists( "A" ) );
		Assert.IsTrue( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B" ) );
		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C" ) );
		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C/D/E" ) );
		Assert.IsFalse( Rinten.EngineFileSystem.Root.DirectoryExists( "A/B/C/D/E/F" ) );
	}

	[TestMethod]
	public void Temporary()
	{
		var files = Rinten.EngineFileSystem.Temporary.FindFile( "/", "*", true ).ToArray();
		Assert.AreEqual( 0, files.Length );

		var str = "Hello this is some\ntext in a file";
		Rinten.EngineFileSystem.Temporary.WriteAllText( "textfile.txt", str );

		Assert.AreEqual( str, Rinten.EngineFileSystem.Temporary.ReadAllText( "textfile.txt" ) );

		files = Rinten.EngineFileSystem.Temporary.FindFile( "/", "*", true ).ToArray();
		Assert.AreEqual( 1, files.Length );

		using ( var stream = Rinten.EngineFileSystem.Temporary.OpenWrite( "binary.bin" ) )
		using ( var writer = new System.IO.BinaryWriter( stream ) )
		{
			for ( int i = 0; i < 1024 * 64; i++ )
			{
				writer.Write( i );
			}
		}

		using ( var stream = Rinten.EngineFileSystem.Temporary.OpenRead( "binary.bin" ) )
		using ( var reader = new System.IO.BinaryReader( stream ) )
		{
			for ( int i = 0; i < 1024 * 64; i++ )
			{
				Assert.AreEqual( i, reader.ReadInt32() );
			}
		}
	}

	[TestMethod]
	public void ReadAllBytes()
	{
		var data = Rinten.EngineFileSystem.Root.ReadAllBytes( "Addons/Green/green.txt" );
		Assert.AreNotEqual( 0, data.Length );
	}

	[TestMethod]
	public void GetRelativePathCaseInsensitive()
	{
		//GetRelativePath lowercases the physical path before calling ConvertPathFromInternal. SubFileSystem(isCaseSensitive:false)
		//is required so the base prefix "/Addons/Red" is stripped from the lowercased input "/addons/red/red.txt" without throwing.
		var sub = Rinten.EngineFileSystem.Root.CreateSubSystem( "Addons/Red" );
		var fullPath = Rinten.EngineFileSystem.Root.GetFullPath( "Addons/Red/red.txt" );

		var relative = sub.GetRelativePath( fullPath );

		Assert.AreEqual( "/red.txt", relative );
	}

	[TestMethod]
	public void SubFileSystemCaseInsensitivePath()
	{
		//SubFileSystem is case-insensitive so the prefix strips even when Linux disk casing differs
		var sub = Rinten.EngineFileSystem.Root.CreateSubSystem( "addons/red" );

		Assert.IsTrue( sub.FileExists( "red.txt" ) );
		Assert.IsTrue( sub.FileExists( "common.txt" ) );
		Assert.AreEqual( "Red", sub.ReadAllText( "red.txt" ) );
		Assert.AreEqual( 2, sub.FindFile( "/", "*", false ).Count() );
	}

	[TestMethod]
	public void MountedFoldersCaseInsensitivePaths()
	{
		var fs = new Rinten.AggregateFileSystem();

		fs.CreateAndMount( Rinten.EngineFileSystem.Root, "addons/blue" );
		fs.CreateAndMount( Rinten.EngineFileSystem.Root, "addons/red" );
		fs.CreateAndMount( Rinten.EngineFileSystem.Root, "addons/green" );

		Assert.IsTrue( fs.FileExists( "red.txt" ) );
		Assert.IsTrue( fs.FileExists( "green.txt" ) );
		Assert.IsTrue( fs.FileExists( "blue.txt" ) );

		Assert.AreEqual( "Red", fs.ReadAllText( "red.txt" ) );
		Assert.AreEqual( "Green", fs.ReadAllText( "green.txt" ) );
		Assert.AreEqual( "Blue", fs.ReadAllText( "blue.txt" ) );
		Assert.AreEqual( "Green", fs.ReadAllText( "common.txt" ) );
	}
}
