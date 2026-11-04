using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
namespace Rinten;

/// <summary>A scene file contains a collection of GameObject with Components and their properties.</summary>
[AssetType( Name = "Scene", Extension = "scene", Category = "World", Flags = AssetTypeFlags.NoEmbedding, IconColor = "#4596ec", Icon = "landscape" )]
public partial class SceneFile : GameResource
{
	/// <summary>Load a scene by file path. Also handles mount:// paths.</summary>
	public static SceneFile Load( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return null;

		var existing = ResourceLibrary.Get<SceneFile>( path );
		if ( existing is not null )
			return existing;

		if ( Mounting.Directory.TryLoad( path, out var mounted ) && mounted is SceneFile sf )
			return sf;

		return null;
	}

	[JsonPropertyName( "__guid" )]
	public Guid Id { get; set; }

	public JsonObject[] GameObjects
	{
		get
		{
			if ( !objectsOnDisk ) return gameObjects;

			objectsOnDisk = false;
			FlushWrites();
			LoadFromJson( EngineFileSystem.Mounted.ReadAllText( ResourcePath ) );

			return gameObjects;
		}
		set
		{
			objectsOnDisk = false;
			gameObjects = value;
		}
	}

	private JsonObject[] gameObjects;
	private bool objectsOnDisk;

	//A big scene's objects are gigabytes as json and needed only to load it again, so the editor keeps them in the file
	internal void LetGoOfObjects()
	{
		if ( !Application.IsEditor || gameObjects is null || string.IsNullOrEmpty( ResourcePath ) ) return;
		if ( !EngineFileSystem.Mounted.FileExists( ResourcePath ) ) return;

		gameObjects = null;
		objectsOnDisk = true;
	}
	public JsonObject SceneProperties { get; set; }

	//Its objects are raw json, read against the blobs only when a scene loads them - see Scene.Load
	internal override bool ReadsBlobs => false;

	//A scene this big is read at startup only for its head; its objects wait in the file for whoever opens it
	internal const int HeadOnlyFrom = 4 << 20;

	//The file's top-level properties without GameObjects, as json, and the version it was written at; null where it cannot be cut
	internal static string HeadOf( ReadOnlySpan<byte> utf8, out int version )
	{
		version = 0;

		try
		{
			if ( utf8.StartsWith( (ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF] ) ) utf8 = utf8[3..];

			var reader = new System.Text.Json.Utf8JsonReader( utf8, new System.Text.Json.JsonReaderOptions { MaxDepth = 512, CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true } );
			if ( !reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject ) return null;

			using var stream = new System.IO.MemoryStream();
			using ( var writer = new System.Text.Json.Utf8JsonWriter( stream ) )
			{
				writer.WriteStartObject();

				while ( reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.PropertyName )
				{
					var name = reader.GetString();
					if ( !reader.Read() ) return null;

					if ( name == nameof( GameObjects ) )
					{
						reader.Skip();
						continue;
					}

					if ( name == "__version" && reader.TokenType == System.Text.Json.JsonTokenType.Number ) version = reader.GetInt32();

					var start = (int)reader.TokenStartIndex;
					reader.Skip();

					writer.WritePropertyName( name );
					writer.WriteRawValue( utf8[start..(int)reader.BytesConsumed], skipInputValidation: true );
				}

				writer.WriteEndObject();
			}

			return System.Text.Encoding.UTF8.GetString( stream.GetBuffer(), 0, (int)stream.Length );
		}
		catch ( System.Text.Json.JsonException )
		{
			return null;
		}
	}

	//Loaded from its head: the objects are read from the file on first asking
	internal void LeaveObjectsOnDisk()
	{
		gameObjects = null;
		objectsOnDisk = true;
	}

	public override int ResourceVersion => CurrentVersion;

	internal const int CurrentVersion = 6;

	[Hide, JsonIgnore]
	protected override Type ActionGraphTargetType => null;

	[Hide, JsonIgnore]
	protected override object ActionGraphTarget => null;

	[System.Obsolete( "Use GetMetadata" )]
	public string Title => GetMetadata( "Title" );

	[System.Obsolete( "Use GetMetadata" )]
	public string Description => GetMetadata( "Description" );

	public string GetMetadata( string title, string defaultValue = null )
	{
		if ( SceneProperties is null ) return defaultValue;
		if ( SceneProperties["Metadata"] is not JsonObject metadata ) return defaultValue;

		return metadata.GetPropertyValue( title, defaultValue );
	}

	protected override Bitmap CreateAssetTypeIcon( int width, int height )
	{
		var svg = "<svg fill=\"#86c4fe\" height=\"256px\" width=\"256px\" version=\"1.1\" id=\"Capa_1\" xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"-14.7 -14.7 323.40 323.40\" xml:space=\"preserve\" stroke=\"#86c4fe\"><g id=\"SVGRepo_bgCarrier\" stroke-width=\"0\"></g><g id=\"SVGRepo_tracerCarrier\" stroke-linecap=\"round\" stroke-linejoin=\"round\"></g><g id=\"SVGRepo_iconCarrier\"> <path d=\"M281.504,10.27H12.5C5.597,10.27,0,15.866,0,22.77v248.465c0,6.903,5.597,12.5,12.5,12.5h269.004 c6.903,0,12.5-5.597,12.5-12.5V22.77C294.004,15.866,288.407,10.27,281.504,10.27z M61.01,108.295 c-19.873,0-35.983-16.11-35.983-35.983c0-19.873,16.11-35.983,35.983-35.983s35.983,16.11,35.983,35.983 C96.993,92.185,80.882,108.295,61.01,108.295z M140.88,238.19c1.526,3.06,1.36,6.692-0.439,9.6 c-1.799,2.907-4.975,4.677-8.395,4.677c-10.912,0-80.172,0-90.662,0c-3.42,0-6.596-1.77-8.395-4.677 c-1.799-2.908-1.965-6.54-0.439-9.6l45.331-90.881c1.67-3.35,5.091-5.466,8.834-5.466s7.163,2.116,8.834,5.466L140.88,238.19z M263.926,247.79c-1.799,2.907-4.975,4.677-8.395,4.677h-89.358c1.859-6.428,1.874-13.276,0.003-19.744 c-1.28-4.423,0.46-0.401-33.371-68.229l38.567-77.321c1.671-3.35,5.091-5.466,8.834-5.466c3.743,0,7.163,2.116,8.834,5.466 l75.326,151.016C265.891,241.25,265.725,244.882,263.926,247.79z\"></path> </g></svg>";
		return Bitmap.CreateFromSvgString( svg, width, height );
	}
}
