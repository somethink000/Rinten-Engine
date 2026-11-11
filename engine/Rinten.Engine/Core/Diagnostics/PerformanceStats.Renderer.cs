namespace Rinten.Diagnostics;

public static partial class PerformanceStats
{
	/// <summary>Graphics API, frame tier and device name, so frame numbers from two machines can be compared</summary>
	public static string Renderer
	{
		get
		{
			if ( OperatingSystem.IsBrowser() )
			{
				var api = BrowserDevice.ShaderFormat == CoreShaderCompiler.Format.Glsl ? "WebGL2" : "WebGPU";
				var name = BrowserDevice.deviceNameText;
				return string.IsNullOrEmpty( name ) ? api : $"{api}, {name}";
			}

			var frame = CoreRenderDevice.PortableFrame ? "portable" : "full";
			var device = CoreRenderDevice.DeviceName;
			return string.IsNullOrEmpty( device ) ? $"Vulkan {frame}" : $"Vulkan {frame}, {device}";
		}
	}

	/// <summary>How the world's edges are smoothed now: SMAA, FXAA, MSAA, a temporal upscaler, or "off".</summary>
	public static string AntiAliasing
	{
		get
		{
			//SMAA is the portable frame's only anti-aliasing
			var post = CoreSmaa.S2x ? ( CoreSmaa.T2x ? "SMAA 4x" : "SMAA S2x" ) : CoreSmaa.T2x ? "SMAA T2x" : CoreSmaa.Smaa ? "SMAA" : CoreSmaa.Cmaa ? "CMAA2" : CoreSmaa.Fxaa ? "FXAA" : null;

			if ( CoreRenderDevice.PortableFrame ) return post ?? "off";

			var temporal = Rinten.Rendering.DrawSettings.Upscaler switch
			{
				3 => "FSR3",
				4 => "DLSS",
				5 => "TAA",
				_ => null,
			};

			if ( temporal is not null ) return temporal;

			var sampled = VkFrame.WorldSamples > 1 ? $"MSAA {VkFrame.WorldSamples}x" : null;

			//S2x and 4x already name their MSAA 2x
			return post is null ? sampled ?? "off" : sampled is null || CoreSmaa.S2x ? post : $"{sampled} + {post}";
		}
	}
}
