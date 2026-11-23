global using Microsoft.VisualStudio.TestTools.UnitTesting;
global using Rinten;
global using System.Linq;
global using System.Threading.Tasks;
global using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Rinten.Engine;
using Rinten.Internal;
using System;

[assembly: Parallelize( Workers = 0, Scope = ExecutionScope.MethodLevel )]

[TestClass]
public class TestInit
{
	[AssemblyInitialize]
	public static void AssemblyInitialize( TestContext context )
	{
		//Core tier: only our native library, no engine loop and no legacy runtime loaded

		var gameFolder = System.Environment.GetEnvironmentVariable( EngineEnvironment.Folder, EnvironmentVariableTarget.Process );
		if ( gameFolder is null ) throw new Exception( $"{EngineEnvironment.Folder} not found" );

		Environment.CurrentDirectory = gameFolder;


		Api.Init();

		Application.IsUnitTest = true;
		GlobalContext.Current.TypeLibrary = new TypeLibrary();

		GlobalContext.Current.UISystem = new UISystem();

		GlobalContext.Current.TypeLibrary.AddIntrinsicTypes();
		GlobalContext.Current.TypeLibrary.AddAssembly( typeof( Vector3 ).Assembly, false );
		GlobalContext.Current.TypeLibrary.AddAssembly( typeof( EngineLoop ).Assembly, false );
		GlobalContext.Current.TypeLibrary.AddAssembly( typeof( Facepunch.ActionGraphs.ActionGraph ).Assembly, false );
		GlobalContext.Current.TypeLibrary.AddAssembly( typeof( TestInit ).Assembly, true );

		//A tool mounts the base addon's assets for the editor's fonts - Inter for text and Material Icons for every
		//icon. Without them a panel measures and lays out with a fallback font, and the layout tests measure that.
		var editorAssets = new LocalFileSystem( gameFolder ).CreateSubSystem( "/addons/base/Assets" );
		Rinten.FontManager.Instance.LoadAll( editorAssets );

		GlobalToolsNamespace.EditorTypeLibrary = new TypeLibrary();
		GlobalToolsNamespace.EditorTypeLibrary.AddIntrinsicTypes();
		GlobalToolsNamespace.EditorTypeLibrary.AddAssembly( typeof( Vector3 ).Assembly, false );
		GlobalToolsNamespace.EditorTypeLibrary.AddAssembly( typeof( EngineLoop ).Assembly, false );
		GlobalToolsNamespace.EditorTypeLibrary.AddAssembly( typeof( Facepunch.ActionGraphs.ActionGraph ).Assembly, false );
		GlobalToolsNamespace.EditorTypeLibrary.AddAssembly( typeof( TestInit ).Assembly, true );

		MountForEditorSheets( gameFolder );
	}

	//Mounts the project once up front so editor sheets can read the theme; per-class mounts race
	private static void MountForEditorSheets( string gameFolder )
	{
		if ( EngineFileSystem.Root is null )
			EngineFileSystem.Initialize( gameFolder, skipBaseFolderInit: true );

		Editor.EditorSheetPreamble.Register();

		//Nothing has read the theme yet in a fresh process, but a run that re-enters this - and the
		//latch behind ThemeText - should not be left holding a nothing read before the mount existed.
		Editor.EditorSheetPreamble.Reload();
	}
}
