using System;
using System.IO;

namespace Editor;

//A .mat per glTF material from the maps and numbers the file names, remapped onto the model
internal static class GltfMaterials
{
	internal static bool Handles( string path )
		=> Path.GetExtension( path ?? "" ).ToLowerInvariant() is ".gltf" or ".glb";

	//Materials go beside the model as <model>_<material>.mat; a file already there is kept as it is
	internal static void Apply( ModelDefinition definition, Asset mesh, string outputPath )
	{
		if ( !string.IsNullOrWhiteSpace( definition.Material ) ) return;

		var file = GltfFile.Load( mesh.AbsolutePath );
		if ( file is null ) return;

		var folder = Path.GetDirectoryName( outputPath );
		var stem = Path.GetFileNameWithoutExtension( outputPath );

		for ( var i = 0; i < file.MaterialNames.Length; i++ )
		{
			var surface = file.MaterialNames[i];

			if ( definition.MaterialRemaps.Any( x => string.Equals( x?.Surface, surface, StringComparison.OrdinalIgnoreCase ) ) )
				continue;

			var target = Path.Combine( folder, $"{stem}_{Safe( surface )}.mat" );

			if ( !File.Exists( target ) )
			{
				var maps = file.MaterialPictures[i];
				var name = $"{stem}_{Safe( surface )}";
				var content = Content( file, maps, file.MaterialSurfaces[i], file.MaterialTwoSided[i], folder, stem, name );

				try
				{
					File.WriteAllText( target, Rinten.Json.Serialize( content ) );
				}
				catch ( Exception e )
				{
					Log.Warning( $"glTF materials: couldn't write '{target}' - {e.Message}" );
					continue;
				}
			}

			if ( AssetSystem.RegisterFile( target ) is not { } asset ) continue;

			definition.MaterialRemaps.Add( new ModelDefinition.SurfaceMaterial { Surface = surface, Material = asset.Path } );
		}
	}

	static Dictionary<string, object> Content( GltfFile file, GltfFile.MaterialMaps maps, Rinten.ModelFormat.Surface surface,
		bool twoSided, string folder, string stem, string name )
	{
		var features = new Dictionary<string, int>();
		var content = new Dictionary<string, object>
		{
			["Shader"] = Rinten.MaterialDefinition.StandardShader,
			["Features"] = features,
		};

		var tint = surface.Says( Rinten.ModelFormat.SurfaceSays.Colour ) ? new[] { surface.r, surface.g, surface.b, surface.a } : new[] { 1.0f, 1.0f, 1.0f, 1.0f };
		content["Tint"] = string.Join( ",", tint.Select( x => x.ToString( System.Globalization.CultureInfo.InvariantCulture ) ) );

		var textures = new Dictionary<string, string>();

		//glTF's rough/metal map is green/blue of one picture, which F_ROUGH_METAL_MAP reads as it is
		if ( Map( file, maps?.RoughMetal, folder, stem, $"{name}_roughmetal" ) is { } roughMetal )
		{
			textures["g_tRoughMetal"] = roughMetal;
			content["Roughness"] = maps.Roughness;
			content["Metalness"] = maps.Metalness;
		}
		else
		{
			content["Roughness"] = maps?.Roughness ?? 1.0f;
			content["Metalness"] = maps?.Metalness ?? 0.0f;
		}

		//Occlusion is red, which is what g_tAO reads; often the same picture as the rough/metal map
		if ( Map( file, maps?.Occlusion, folder, stem, $"{name}_occlusion" ) is { } occlusion )
			textures["g_tAO"] = occlusion;

		if ( textures.Count > 0 ) content["Textures"] = textures;

		if ( Map( file, maps?.Colour, folder, stem, $"{name}_color" ) is { } colourMap ) content["ColorMap"] = colourMap;
		if ( Map( file, maps?.Normal, folder, stem, $"{name}_normal" ) is { } normalMap ) content["Normal"] = normalMap;

		if ( Map( file, maps?.Emission, folder, stem, $"{name}_emission" ) is { } emissionMap )
		{
			content["Emission"] = emissionMap;
			content["EmissionStrength"] = 1.0f;
		}
		else if ( surface.Says( Rinten.ModelFormat.SurfaceSays.Emission ) )
		{
			content["Emission"] = Rinten.MaterialDefinition.FlatMapPath( new Color( surface.emissionR, surface.emissionG, surface.emissionB ), true );
			content["EmissionStrength"] = surface.emissionStrength;
		}

		switch ( maps?.AlphaMode )
		{
			case "MASK":
				features["F_ALPHA"] = 1;
				content["AlphaCutoff"] = maps.AlphaCutoff;
				break;

			case "BLEND":
				features["F_TRANSLUCENT"] = 1;
				break;
		}

		if ( twoSided ) features["F_BACKFACES"] = 1;

		content["__version"] = 2;

		return content;
	}

	//A project path for the picture: its own file where that is in the project, else a copy beside the model named by
	//the image, so a picture several slots or materials share is written once
	static string Map( GltfFile file, GltfFile.Picture picture, string folder, string stem, string name )
	{
		if ( picture is null ) return null;

		string target;

		if ( picture.File is { } source )
		{
			if ( !File.Exists( source ) ) return null;
			if ( InProject( source ) ) return AssetSystem.RegisterFile( source )?.RelativePath;

			target = Path.Combine( folder, $"{stem}_{Path.GetFileName( source )}" );
			if ( !Write( target, () => File.Copy( source, target ) ) ) return null;
		}
		else
		{
			var bytes = file.InlineBytes( picture );
			if ( Extension( bytes ) is not { } extension ) return null;

			target = Path.Combine( folder, picture.View >= 0 ? $"{stem}_image{picture.View}{extension}" : $"{name}{extension}" );
			if ( !Write( target, () => File.WriteAllBytes( target, bytes ) ) ) return null;
		}

		return AssetSystem.RegisterFile( target )?.RelativePath;
	}

	//Writes a file that is not there yet; false where it could not, said in the log
	static bool Write( string target, Action write )
	{
		if ( File.Exists( target ) ) return true;

		try
		{
			write();
			return true;
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			Log.Warning( $"glTF materials: couldn't write '{target}' - {e.Message}" );
			return false;
		}
	}

	static bool InProject( string path )
	{
		var root = Project.Current?.GetAssetsPath();
		if ( string.IsNullOrWhiteSpace( root ) ) return false;

		return Path.GetFullPath( path ).StartsWith( Path.GetFullPath( root ), StringComparison.OrdinalIgnoreCase );
	}

	//By the bytes, since a GLB gives a MIME type at best
	static string Extension( byte[] bytes )
	{
		if ( bytes is not { Length: > 12 } ) return null;
		if ( bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G' ) return ".png";
		if ( bytes[0] == 0xFF && bytes[1] == 0xD8 ) return ".jpg";
		if ( bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E' ) return ".webp";
		return null;
	}

	static string Safe( string name )
	{
		var chars = name.ToCharArray();

		for ( var i = 0; i < chars.Length; i++ )
		{
			if ( !char.IsAsciiLetterOrDigit( chars[i] ) && chars[i] is not '_' and not '-' ) chars[i] = '_';
		}

		return new string( chars ).ToLowerInvariant();
	}
}
