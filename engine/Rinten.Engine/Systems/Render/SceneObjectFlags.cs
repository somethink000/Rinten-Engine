namespace Rinten.Rendering;

[Flags]
internal enum SceneObjectFlags : ulong
{
	None = 0,
	IsOpaque = 0x0000000000000001,
	IsTranslucent = 0x0000000000000002,
	IsLight = 0x0000000000000004,
	IsSunLight = 0x0000000000000008,
	IsLightVolume = 0x0000000000000010,
	IsDecal = 0x0000000000000020,
	IsDynamicDecals = 0x0000000000000040,
	IsEnvMap = 0x0000000000000080,
	IsDirectLight = 0x0000000000000100,
	IsIndirectLight = 0x0000000000000200,

	//Rendering order: Logical layers we organize a scene into
	ViewModelLayer = 0x0000000000001000,   //Render only in viewmodel pass
	Skybox3DLayer = 0x0000000000002000,
	DisabledInLowQuality = 0x0000000000004000,
	//What the editor draws the grid on: the surfaces that are being laid out with it. See CoreSceneDrawer.DrawSurfaceGrid. It took the bit the dead IsHammerGeometry held - nothing set it and nothing read it.
	/// <summary>An editable polygon mesh - a MeshComponent - rather than a model somebody made elsewhere.</summary>
	IsEditableMesh = 0x0000000000008000,
	EffectsBloomLayer = 0x0000000000010000,
	GameOverlayLayer = 0x0000000000020000,
	ExcludeGameLayer = 0x0000000000040000,
	UIOverlayLayer = 0x0000000000080000,

	//Map editor / Tools-specific rendering order
	ToolsUnlitLayer = 0x0000000000100000,
	ToolSceneOverlayLayer = 0x0000000000200000,
	HammerPrefabStencilLayer = 0x0000000000400000,
	HammerSelectionStencilLayer = 0x0000000000800000,
	HammerEnabledStencilLayer = 0x0000000001000000,

	//Rendering properties of a scene object
	HasAOProxies = 0x0000000010000000,
	AlphaTestZPrepass = 0x0000000020000000,
	AddsDependentView = 0x0000000040000000, //Adds a dependent view, doesn't render itself (also safe to draw multiple times).  Hint to kick this work off early
	NeedsDynamicReflectionMap = 0x0000000080000000, //Meshes with this flag set will cause a dynamic reflection to launch and have it bound into the context
	Reflects = 0x0000000100000000,
	CastShadowsEnabled = 0x0000000200000000,
	DoesNotAcceptDecals = 0x0000000400000000,
	WantsFrameBufferCopyTexture = 0x0000000800000000,
	IssuesQueries = 0x0000001000000000,
	StaticObject = 0x0000002000000000, //Static, permanent part of the "world"
	EnvironmentMapped = 0x0000004000000000,
	MaterialSupportsShadows = 0x0000008000000000, //The material supports casting shadows, but the object will only cast shadows if MATERIAL_SUPPORTS_SHADOWS and CAST_SHADOWS_ENABLED are set
	NoZPrepass = 0x0000010000000000, //Opt out of z-prepass
	ForwardLayerOnly = 0x0000020000000000, //Render only in a forward layer
	NoOcclusionCulling = 0x0000040000000000, //Don't occlusion cull this scene object
	NoPVSCulling = 0x0000080000000000, //Don't pvs cull this scene object

	CastShadows = MaterialSupportsShadows | CastShadowsEnabled,

	/// <summary>Not rendered in cubemaps</summary>
	HideInCubemaps = 1LU << 52,

	/// <summary>Never baked into the sun's still-world pages; for short-lived things like debug overlays.</summary>
	NeverStill = 1LU << 53,
	ExecuteBefore = 1LU << 57,
	ExecuteAfter = 1LU << 58,

	NeedsLightProbe = 0x0000400000000000,


	IsLoaded = 0x2000000000000000
}
