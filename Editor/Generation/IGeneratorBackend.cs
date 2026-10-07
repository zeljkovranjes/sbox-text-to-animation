using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.Core.Animation;
using TextToAnimation.Core.Generation;

namespace TextToAnimation.EditorTools.Generation;

/// <summary>
/// A motion model the editor can download, load and run. The UI only talks to this and to
/// <see cref="IMotionGenerator"/>, so another model can be added by implementing this interface.
/// </summary>
public interface IGeneratorBackend
{
	string Name { get; }
	string Description { get; }

	/// <summary>Total download size in bytes (for the Download button).</summary>
	long DownloadBytes { get; }

	/// <summary>Fast check of what is on disk (no hashing).</summary>
	ModelState Inspect();

	/// <summary>Downloads and verifies the model files. Progress lines use the format
	/// "Downloading &lt;name&gt; · &lt;n&gt;% · &lt;size&gt; left" (the loading indicator turns them into a bar).</summary>
	Task InstallAsync( IProgress<string> progress, CancellationToken token );

	/// <summary>Prepares the model for inference (conversion and loading). Safe to call repeatedly.</summary>
	Task<IMotionGenerator> LoadAsync( IProgress<string> progress, CancellationToken token );

	/// <summary>Deletes the downloaded and converted files.</summary>
	void Remove();
}
