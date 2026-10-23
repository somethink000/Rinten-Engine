using System.Diagnostics;

namespace Editor;

internal static partial class ShaderHooks
{
	[Event( "content.changed" )]
	public static void OnShaderWasEdited( string filename ) => CompileShader( filename.Trim( '/' ) );

	private static ICodeEditor _editor;
	private static ICodeEditor Editor
	{
		get
		{
			if ( _editor != null )
				return _editor;

			var editor = new CodeEditors.VisualStudioCode();
			if ( editor.IsInstalled() )
			{
				_editor = editor;
				return editor;
			}

			return null;
		}
	}

	[Event( "open.shader" )]
	public static void OpenShader( string filename )
	{
		Editor?.OpenFile( filename );
	}

	[Event( "compile.shader" )]
	public static void CompileShader( string shader )
	{
		if ( !shader.EndsWith( ".shader" ) || !FileSystem.Mounted.FileExists( shader ) ) return;

		//Shaders rebuild from source when next drawn: only an asset with a builder of its own is compiled here
		var asset = AssetSystem.FindByPath( shader );
		if ( asset is null || !asset.CanRecompile ) return;

		Log.Info( $"Compiling: {shader}" );

		var sw = Stopwatch.StartNew();

		if ( !asset.Compile( full: true ) )
		{
			Log.Warning( $"Shader compile failed after {sw.Elapsed.TotalMilliseconds:0.00}ms" );
			return;
		}

		Log.Info( $"Done in {sw.Elapsed.TotalMilliseconds:0.00}ms" );
	}
}
