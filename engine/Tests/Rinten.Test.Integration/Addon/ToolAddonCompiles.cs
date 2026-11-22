using System;
using System.IO;
using System.Linq;

namespace AddonTests;

[TestClass]
public class ToolAddonCompiles
{
	[TestMethod]
	public Task CompileMdlEditor() => Compiles( "MdlEditor", "mdleditor" );

	[TestMethod]
	public Task CompileFxEditor() => Compiles( "FxEditor", "fxeditor" );

	[TestMethod]
	public Task CompileMaterialEditor() => Compiles( "MaterialEditor", "materialeditor" );

	[TestMethod]
	public Task CompileUiEditor() => Compiles( "UiEditor", "uieditor" );

	//Compile one of the editors under game/editor the way the editor itself does.
	static async Task Compiles( string folder, string ident )
	{
		var codePath = Path.Combine( Environment.CurrentDirectory, "editor", folder, "Code" );
		Assert.IsTrue( Directory.Exists( codePath ), codePath );

		using var group = new CompileGroup( "Test" );

		//As the editor compiles them: a warning is an error, and a project that warns does not load - see the .rinproj of each.
		var settings = new Compiler.Configuration();
		settings.Clean();
		settings.Unsafe = true;
		settings.TreatWarningsAsErrors = true;

		//The base addon: Button, TextEntry, Popup and the rest of the panels the editor's own controls are built out of. The editor compiles it first
		//and everything else references it - without it the tools addon does not compile, and neither does anything that references the tools addon.
		var baseSettings = new Compiler.Configuration();
		baseSettings.Clean();
		baseSettings.Unsafe = true;

		var baselib = group.CreateCompiler( "base",
			Path.Combine( Environment.CurrentDirectory, "addons", "base", "code" ), baseSettings );
		Reference( baselib );

		//The tools addon, which is what "Editor.NodeEditor" and the rest of the editor's own
		//widgets are. The editor compiles it after base and every tool project references it.
		var toolbaseSettings = new Compiler.Configuration();
		toolbaseSettings.Clean();
		toolbaseSettings.Unsafe = true;

		var toolbase = group.CreateCompiler( "toolbase",
			Path.Combine( Environment.CurrentDirectory, "addons", "tools", "Code" ), toolbaseSettings );
		toolbase.AddBaseReference();
		Reference( toolbase );

		var compiler = group.CreateCompiler( $"test.{ident}", codePath, settings );
		compiler.AddBaseReference();
		compiler.AddToolBaseReference();
		Reference( compiler );

		await group.BuildAsync();

		var errors = string.Join( "\n", group.BuildResult.Diagnostics
			.Where( x => x.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error )
			.Select( x => $"{x.Location?.SourceTree?.FilePath}:{x.Location?.GetLineSpan().StartLinePosition} {x.GetMessage()}" )
			.Take( 30 ) );

		Assert.IsTrue( group.BuildResult.Success, errors );
	}

	//What the editor gives every tool project. See Project.Compiling.
	static void Reference( Compiler compiler )
	{
		compiler.GeneratedCode.AppendLine( "global using Microsoft.AspNetCore.Components;" );
		compiler.GeneratedCode.AppendLine( "global using Microsoft.AspNetCore.Components.Rendering;" );
		compiler.GeneratedCode.AppendLine( "global using static Rinten.Internal.GlobalGameNamespace;" );
		compiler.GeneratedCode.AppendLine( "global using static Rinten.Internal.GlobalToolsNamespace;" );

		foreach ( var reference in new[]
		{
			"Rinten.Tools", "Rinten.Compiling", "Rinten.Bind", "Facepunch.ActionGraphs", "SkiaSharp",
			"Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
			"System.Diagnostics.Process", "System.Net.WebSockets", "System.Net.WebSockets.Client",
			"Microsoft.Win32.Registry", "System.Memory",
		} )
		{
			compiler.AddReference( reference );
		}
	}
}
