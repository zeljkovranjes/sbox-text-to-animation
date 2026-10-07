using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TextToAnimation.EditorTools.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>
/// The port's dopri5 against torchdiffeq's (what upstream's Sampler.sample_ode runs) on a nonlinear test ODE
/// (dy/dt = A tanh(y) + sin 3t, 37 values, rtol 1e-3, atol 1e-6): the same steps (call count) and the same result.
/// </summary>
public class Dopri5Tests
{
    readonly ITestOutputHelper _out;
    public Dopri5Tests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void MatchesTorchdiffeq()
    {
        var j = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "dopri5_reference.json"))).RootElement;
        var a = j.GetProperty("A").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var y0 = j.GetProperty("y0").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var expected = j.GetProperty("y1").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var n = y0.Length;
        var calls = 0;
        float[] F(float[] y, float t)
        {
            calls++;
            var r = new float[n];
            var s = MathF.Sin(3 * t);
            for (var i = 0; i < n; i++)
            {
                var acc = 0f;
                for (var k = 0; k < n; k++) acc += a[i * n + k] * MathF.Tanh(y[k]);
                r[i] = acc + s;
            }
            return r;
        }
        var y1 = UniMateModel.Dopri5(y0, F, 1e-3, 1e-6, null, default);
        var worst = y1.Zip(expected, (p, q) => MathF.Abs(p - q)).Max();
        _out.WriteLine($"{calls} calls (torchdiffeq {j.GetProperty("calls").GetInt32()}); max difference {worst:G3}");
        Assert.Equal(j.GetProperty("calls").GetInt32(), calls);
        Assert.True(worst < 1e-5f, $"differs by {worst}");
    }
}
