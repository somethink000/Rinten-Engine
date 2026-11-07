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

	/// <summary>How the world's edges are smoothed now: SMAA, MSAA, a temporal upscaler, or "off".</summary>
	public static string AntiAliasing
	{
		get
		{
			//SMAA is the portable frame's only anti-aliasing
			if ( CoreRenderDevice.PortableFrame ) return CoreSmaa.Asked ? "SMAA" : "off";

			var temporal = Rinten.Rendering.DrawSettings.Upscaler switch
			{
				3 => "FSR3",
				4 => "DLSS",
				5 => "TAA",
				_ => null,
			};

			if ( temporal is not null ) return temporal;

			var sampled = VkFrame.WorldSamples > 1 ? $"MSAA {VkFrame.WorldSamples}x" : null;

			return CoreSmaa.Asked ? ( sampled is null ? "SMAA" : sampled + " + SMAA" ) : sampled ?? "off";
		}
	}
}
