namespace Rinten.Engine;

/// <summary>A centralized place to access the protocols</summary>
public static class Protocol
{
	/// <summary>We cannot play packages with an Api version higher than this.</summary>
	public static int Api => 28;

	/// <summary>We cannot talk to servers or clients with a network protocol different to this.</summary>
	public static int Network => 1104;
}

//Api versions: 28 (Jul 2026) map lights become Light components ... 18 (Mar 2025) first staging


//Network versions: 1104 (Oct 2026) ParticleFloat has an explicit BytePack format ... 1085 (Mar 2025) first staging
