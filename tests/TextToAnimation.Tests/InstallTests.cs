using System;
using System.Threading;
using System.Threading.Tasks;
using TextToAnimation.EditorTools.Generation;
using TextToAnimation.EditorTools.Generation.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>Real download from Hugging Face (opt-in: set T2A_RUN_DOWNLOAD=1).</summary>
public class InstallTests
{
    readonly ITestOutputHelper _out;
    public InstallTests(ITestOutputHelper o) => _out = o;

    sealed class Sink : IProgress<string>
    {
        public string Last = ""; int _lastPercent = -1; readonly ITestOutputHelper _o;
        public Sink(ITestOutputHelper o) => _o = o;
        public void Report(string value)
        {
            Last = value;
            var i = value.IndexOf('%');
            if (i > 0 && int.TryParse(value.Substring(0, i).Split('·')[^1].Trim(), out var p) && p / 10 != _lastPercent / 10) { _lastPercent = p; _o.WriteLine(value); }
            else if (i < 0) _o.WriteLine(value);
        }
    }

    [Fact]
    public async Task DownloadsAndLoadsUniMate()
    {
        if (Environment.GetEnvironmentVariable("T2A_RUN_DOWNLOAD") != "1") return;
        var backend = new UniMateBackend();
        _out.WriteLine($"model folder {UniMateBackend.ModelDirectory}, state {backend.Inspect()}, {backend.DownloadBytes / 1e6:0} MB");
        var sink = new Sink(_out);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await backend.InstallAsync(sink, CancellationToken.None);
        _out.WriteLine($"installed in {watch.Elapsed.TotalSeconds:0} s");
        Assert.Equal(ModelState.Ready, backend.Inspect());
        var generator = await backend.LoadAsync(sink, CancellationToken.None);
        Assert.Equal("UniMate", generator.Name);
    }
}
