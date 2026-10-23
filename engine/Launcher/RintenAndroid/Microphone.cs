using System;
using System.Linq;
using Android;
using Android.App;
using Android.Content.PM;

namespace Rinten.Android;

//RECORD_AUDIO for voice, asked of the player the first time a game opens the microphone - see VoiceManager.HostMicrophone.
internal static class Microphone
{
	//What the answer comes back to MainActivity with.
	public const int RequestCode = 0x4D49;

	//Whether the system's question is on the screen now.
	private static volatile bool asking;

	//Whether the player said no this run: asked again every frame a game wants the microphone, the question would never go away.
	private static volatile bool refused;

	//What VoiceManager asks before it opens the microphone, or null where the package carries no RECORD_AUDIO - see the csproj.
	public static Func<bool> For( Activity activity, Func<Activity> current )
	{
		try
		{
			var info = activity.PackageManager?.GetPackageInfo( activity.PackageName ?? "", PackageInfoFlags.Permissions );

			if ( info?.RequestedPermissions?.Contains( Manifest.Permission.RecordAudio ) != true ) return null;
		}
		catch ( Exception e )
		{
			global::Android.Util.Log.Warn( "rinten", $"could not read the package's permissions: {e.Message}" );
			return null;
		}

		return () => Allowed( current() );
	}

	//The system's answer, from MainActivity.OnRequestPermissionsResult.
	public static void Answered( bool granted )
	{
		asking = false;
		refused = !granted;
	}

	private static bool Allowed( Activity on )
	{
		if ( on is null ) return false;

		if ( on.CheckSelfPermission( Manifest.Permission.RecordAudio ) == Permission.Granted ) return true;

		if ( asking || refused ) return false;

		asking = true;
		on.RunOnUiThread( () => on.RequestPermissions( [Manifest.Permission.RecordAudio], RequestCode ) );

		return false;
	}
}
