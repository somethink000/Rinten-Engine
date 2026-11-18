using System.Text.Json;
using System.Text.Json.Serialization;
using Rinten.Build.Steps;

namespace Rinten.Build;

//The JSON this tool reads, turned into serializers at compile time. With PublishAot the runtime turns
//reflection-based serialization off, so a call whose type info is found at runtime throws instead - which is
//what stopped a build on its first step. Every type deserialized here has to be listed.
[JsonSourceGenerationOptions(
	PropertyNameCaseInsensitive = true,
	ReadCommentHandling = JsonCommentHandling.Skip,
	AllowTrailingCommas = true )]
[JsonSerializable( typeof( Dictionary<string, FetchNatives.Package[]> ), TypeInfoPropertyName = "NativeManifest" )]
[JsonSerializable( typeof( EditorControl.EditorStatus ) )]
internal partial class BuildJson : JsonSerializerContext;
