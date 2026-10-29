namespace Rinten;

public partial class Surface
{
	public struct ScrapeEffectData
	{
		/// <summary>Similar to friction but only affects whether a scrape is rough or smooth.</summary>
		[DefaultValue( 1.0f )]
		public float RoughnessFactor { get; set; } = 1.0f;

		/// <summary>Surface roughness greater than this results in rough scrapes.</summary>
		[DefaultValue( 0.5f )]
		public float RoughThreshold { get; set; } = 0.5f;

		[Obsolete, Hide, ResourceType( "fx" )]
		public List<string> SmoothParticles { get; set; }

		[Obsolete, Hide, ResourceType( "fx" )]
		public List<string> RoughParticles { get; set; }

		[Obsolete, Hide, ResourceType( "decal" )]
		public List<string> SmoothDecal { get; set; }

		[Obsolete, Hide, ResourceType( "decal" )]
		public List<string> RoughDecal { get; set; }

		public ScrapeEffectData() { }
	}

	/// <summary>How things sliding on this surface sound: rough or smooth, by the other surface's roughness.</summary>
	[InlineEditor, Title( "Scraping" )]
	public ScrapeEffectData ScrapeEffects { get; set; } = new();

	internal bool HasScrapeSounds => SoundCollection.ScrapeRough is not null || SoundCollection.ScrapeSmooth is not null;

	//The smooth scrape against a smooth surface, the rough one otherwise; either stands in for the other
	internal SoundEvent GetScrapeSound( Surface other )
	{
		var smooth = other is not null && other.ScrapeEffects.RoughnessFactor < ScrapeEffects.RoughThreshold;
		return smooth
			? SoundCollection.ScrapeSmooth ?? SoundCollection.ScrapeRough
			: SoundCollection.ScrapeRough ?? SoundCollection.ScrapeSmooth;
	}

	/// <summary>Play a collision sound based on this shape's surface. Can return null if sound is invalid, or too quiet to play.</summary>
	public SoundHandle PlayCollisionSound( Vector3 position, float speed = 8.128f )
	{
		//Metres a second: full volume at 25.4, a hard impact from 3.3 - upstream's 1000 and 130 inches
		float volume = speed / 25.4f;
		if ( volume > 1.0f ) volume = 1.0f;

		//I have an inkling that this would be aweomse Scale volume by the mass of the object
		//float mass = self.Body.Mass;
		//if ( mass == 0 ) mass = 100; // static objects don't have a mass
		//float massScale = mass.Remap( 0, 1500, 0, 1 );
		//volume *= massScale;

		if ( volume < 0.001f ) return default;

		var sound = SoundCollection.ImpactHard;

		if ( speed < 3.302f || sound == null )
		{
			sound = SoundCollection.ImpactSoft;
		}

		if ( sound == null )
			return default;

		var s = Sound.Play( sound, position );
		if ( s is not null )
		{
			s.Volume *= volume;
		}
		return s;
	}
}
