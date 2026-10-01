using System;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Editor.Generation;
using TextToAnimation.Generation;

namespace TextToAnimation.Editor.UniMate;

public sealed partial class UniMateBackend
{
	// Filled in once the checkpoint package is pinned (see UniMatePackage).
	public long DownloadBytes => 0;
	public ModelState Inspect() => ModelState.NotInstalled;
	public Task InstallAsync( IProgress<string> progress, CancellationToken token )
		=> throw new NotSupportedException( "The UniMate download isn't configured yet." );
	public Task<IMotionGenerator> LoadAsync( IProgress<string> progress, CancellationToken token )
		=> throw new NotSupportedException( "The UniMate model isn't installed." );
	public void Remove() { }
}
