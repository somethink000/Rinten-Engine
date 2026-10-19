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

	/// <summary>How the edges of the world are smoothed right now: MSAA on a page or the portable frame, the edge filter or a temporal upscaler, or "off".</summary>
	public static string AntiAliasing
	{
		get
		{
			if ( OperatingSystem.IsBrowser() )
				return Rinten.Rendering.DrawSettings.Msaa >= 2 ? "MSAA 4x" : "off";

			//The portable frame has no picture of the world for an upscaler to read; its pass multisampled, or its finish softened
			if ( CoreRenderDevice.PortableFrame )
				return VkFrame.LastMainSamples > 1 ? $"MSAA {VkFrame.LastMainSamples}x" : Rinten.Rendering.DrawSettings.Fxaa ? "FXAA" : "off";

			return Rinten.Rendering.DrawSettings.Upscaler switch
			{
				3 => "FSR3",
				4 => "DLSS",
				5 => "TAA",
				_ => Rinten.Rendering.DrawSettings.Fxaa ? "FXAA" : "off",
			};
		}
	}
}
