global using Microsoft.VisualStudio.TestTools.UnitTesting;
global using Rinten;
global using System.Linq;
global using System.Threading.Tasks;
global using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Rinten.Engine;
using Rinten.Internal;
using System;

[TestClass]
public class TestInit
{
	public static Rinten.AppSystem TestAppSystem;

	[AssemblyInitialize]
	public static void AssemblyInitialize( TestContext context )
	{
		//Where the engine is. A test host runs in its own output folder, and everything under here - the addons, the templates, the exporter - reads the engine
		//off the working directory, the way the launcher leaves it. Set before the engine starts: the filesystems it mounts are resolved once, on the way up.
		if ( EngineEnvironment.EngineFolder is string engine && System.IO.Directory.Exists( engine ) )
			Environment.CurrentDirectory = engine;

		//Integration tier: boots the full native engine for the whole assembly, so tests here run serially against real
		//engine state. Lighter tests belong in Rinten.Test.Engine (interop only) or Rinten.Test.Unit (pure managed).
		TestAppSystem = new TestAppSystem();
		TestAppSystem.Init();

		//Tests bring their own components and helpers; the engine's type library has to know them for
		//Create<T>, JSON and the scene directory to resolve them.
		GlobalContext.Current.TypeLibrary.AddAssembly( typeof( TestInit ).Assembly, true );

		//Trace results resolve the hit Surface by index, and nothing mounts base content
		//in headless tests - install a fallback surface so any test that traces works.
		if ( Surface.All.Count == 0 )
		{
			Surface.All[0] = new Surface();
		}
	}

	[AssemblyCleanup]
	public static void AssemblyCleanup()
	{
		TestAppSystem.Shutdown();
	}
}
