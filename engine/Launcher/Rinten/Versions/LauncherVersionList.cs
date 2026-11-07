using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rinten;

//What the versions tab shows: the GitHub releases plus whatever is unpacked here.
internal sealed class LauncherVersionList
{
	//One version, and what can be done about it.
	public sealed class Row
	{
		public string Version { get; init; }

		public string Notes { get; init; }

		public DateTimeOffset Published { get; init; }

		//The download for this machine in bytes, or nought where there is none.
		public long Size { get; init; }

		//Whether the server has a build of it for this machine.
		public bool Available { get; init; }

		public bool Installed { get; init; }

		//What the installer is handed, or null for a version only on the disk.
		public EngineReleaseInfo Release { get; init; }

		//How far its download has got, while it has one.
		public float Progress;

		//Whether this is the row being worked on.
		public bool Working;

		//Whether part of its download is on the disk, waiting for Resume
		public bool Paused;
	}

	public IReadOnlyList<Row> Rows { get; private set; } = [];

	//The line under the list: what happened, or what is happening.
	public string Status { get; private set; } = "";

	public bool Loading { get; private set; }

	//Whether something is being installed or removed - one at a time.
	public bool Busy { get; private set; }

	//Whether the server has been asked yet.
	public bool Asked { get; private set; }

	//Moves on every change, so the panel has one number to hash instead of a list to walk.
	public int Revision => revision;

	private int revision;

	private CancellationTokenSource stopping;

	//Whether the stopped download is thrown away rather than kept for Resume
	private bool discarding;

	private void Changed() => Interlocked.Increment( ref revision );

	//Reads the releases again; what is on the disk is listed either way.
	public async Task Refresh()
	{
		if ( Loading ) return;

		Asked = true;
		Loading = true;
		Status = $"Reading the releases of {LauncherPreferences.ReleasesRepository}";

		Changed();

		var rows = new List<Row>();
		string status;

		try
		{
			using var api = Api();

			var releases = await api.GetReleasesAsync();

			foreach ( var release in releases ) rows.Add( FromRelease( release ) );

			AddOrphans( rows );

			status = rows.Count == 0
				? "Nothing released yet"
				: $"{releases.Count} released, {rows.Count( x => x.Installed )} installed";
		}
		catch ( Exception e )
		{
			//An engine unpacked here is one that can be launched, whatever the server says.
			AddOrphans( rows );

			status = $"Couldn't read the releases: {e.Message}";
		}

		//The row being downloaded keeps its object, which the download reports into
		foreach ( var working in Rows.Where( x => x.Working ) )
		{
			var at = rows.FindIndex( x => x.Version == working.Version );

			if ( at >= 0 ) rows[at] = working;
		}

		Rows = rows;
		Status = status;
		Loading = false;

		Changed();
	}

	public async Task Install( Row row )
	{
		if ( Busy || row?.Release is null ) return;

		Busy = true;
		row.Working = true;
		row.Paused = false;
		row.Progress = Fraction( row );

		stopping = new CancellationTokenSource();
		discarding = false;

		Changed();

		try
		{
			using var api = Api();

			var progress = new Progress<DownloadProgress>( p => Report( row, (float)( p.Fraction ?? 0.0 ) ) );

			await EngineVersions.InstallAsync( api, row.Release, progress, stopping.Token );

			Status = $"Installed {row.Version}";
		}
		catch ( OperationCanceledException ) when ( stopping.IsCancellationRequested )
		{
			if ( discarding ) EngineVersions.DiscardDownload( row.Version );

			row.Paused = !discarding;

			Status = discarding ? $"Cancelled {row.Version}" : $"Paused {row.Version} - Resume carries on, even after a restart";
		}
		catch ( Exception e )
		{
			Status = $"{row.Version} did not install: {e.Message}";
		}
		finally
		{
			row.Working = false;
			row.Progress = row.Paused ? Fraction( row ) : 0.0f;
			Busy = false;

			stopping.Dispose();
			stopping = null;

			Changed();
		}

		await Refresh();
	}

	//Stops the download and keeps what came, for Resume
	public void Pause()
	{
		stopping?.Cancel();
	}

	//Stops the download, or forgets a paused one, and deletes what came
	public void Cancel( Row row )
	{
		if ( row is null ) return;

		if ( row.Working )
		{
			discarding = true;
			stopping?.Cancel();
			return;
		}

		try
		{
			EngineVersions.DiscardDownload( row.Version );
			Status = $"Cancelled {row.Version}";
		}
		catch ( Exception e )
		{
			Status = $"Couldn't delete the download of {row.Version}: {e.Message}";
		}

		row.Paused = false;
		row.Progress = 0.0f;

		Changed();
	}

	public async Task Uninstall( Row row )
	{
		if ( Busy || row is null ) return;

		Busy = true;
		Changed();

		try
		{
			EngineVersions.Uninstall( row.Version );

			Status = $"Removed {row.Version}";
		}
		catch ( Exception e )
		{
			Status = $"Couldn't remove {row.Version}: {e.Message}";
		}
		finally
		{
			Busy = false;
		}

		await Refresh();
	}

	private static ReleaseApi Api() => new( LauncherPreferences.ReleasesRepository );

	//Says the download moved, but only when the hundredth the bar draws has.
	private void Report( Row row, float fraction )
	{
		var before = (int)( row.Progress * 100.0f );

		row.Progress = fraction;

		if ( (int)( fraction * 100.0f ) != before ) Changed();
	}

	private static float Fraction( Row row ) =>
		row.Size > 0 ? Math.Clamp( (float)EngineVersions.Downloaded( row.Version ) / row.Size, 0.0f, 1.0f ) : 0.0f;

	private static Row FromRelease( EngineReleaseInfo release )
	{
		var artifact = release.Artifacts?.FirstOrDefault( x => x.Platform == ReleaseApi.CurrentPlatform );
		var installed = EngineVersions.IsInstalled( release.Version );

		var row = new Row
		{
			Version = release.Version,
			Notes = release.Notes,
			Published = release.PublishedAt,
			Size = artifact is { Available: true } ? artifact.SizeBytes : 0,
			Available = artifact is { Available: true },
			Installed = installed,
			Release = release,
			Paused = !installed && EngineVersions.Downloaded( release.Version ) > 0,
		};

		if ( row.Paused ) row.Progress = Fraction( row );

		return row;
	}

	//The engines on this machine the releases do not mention.
	private static void AddOrphans( List<Row> rows )
	{
		foreach ( var installed in EngineVersions.ListInstalled() )
		{
			if ( rows.Any( x => x.Version == installed.Version ) ) continue;

			rows.Add( new Row
			{
				Version = installed.Version,
				Notes = "Installed here, not released",
				Installed = true,
				Available = false,
			} );
		}
	}
}
