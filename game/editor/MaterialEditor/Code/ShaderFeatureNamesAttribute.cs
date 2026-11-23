using System;

namespace Editor.Materials;

/// <summary>Names of a shader feature's values, lowest first; also selects ShaderFeatureControl</summary>
[AttributeUsage( AttributeTargets.All )]
public sealed class ShaderFeatureNamesAttribute : EditorAttribute
{
	/// <summary>What a property asks for to be given the feature dropdown.</summary>
	public const string EditorName = "material.feature";

	public string[] Names { get; }

	public ShaderFeatureNamesAttribute( string[] names )
	{
		Value = EditorName;
		Names = names ?? [];
	}
}
