namespace Rinten;

//Short shadows off the depth towards the sun, too small for the sun's maps - see contact_shadows.shader;
//drawn after the depth, read by SunShadow for the pixel it shades
internal static class CoreContactShadows
{
	internal const string Picture = "ContactShadow";

	private static Material Shader => field ??= Material.FromShader( "shaders/postprocess/contact_shadows.shader" );

	internal static Rendering.CommandList List
	{
		get
		{
			if ( field is not null ) return field;

			var list = new Rendering.CommandList( "Contact shadows" );
			var picture = list.GetPicture( Picture, Rendering.PictureFormat.Single );

			list.SetRenderTarget( picture );
			list.Blit( Shader );
			list.ClearRenderTarget();

			return field = list;
		}
	}
}
