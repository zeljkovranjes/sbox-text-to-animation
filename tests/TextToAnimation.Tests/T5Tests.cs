using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TextToAnimation.EditorTools.Inference.Onnx;
using TextToAnimation.EditorTools.Inference.UniMate;
using Xunit;
using Xunit.Abstractions;

namespace TextToAnimation.Tests;

/// <summary>T5 tokenizer and encoder against the reference ids/embeddings (needs the dev model cache on D:).</summary>
public class T5Tests
{
    const string TokenizerPath = @"D:\99-scratch\hf-cache\hub\models--google--flan-t5-base\snapshots\7bcac572ce56db69c1ea7c8af255c5d7c9672fc2\tokenizer.json";
    const string EncoderPath = @"D:\99-scratch\hf-cache\xenova\encoder_model.onnx";
    readonly ITestOutputHelper _out;
    public T5Tests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TokenizerMatchesReferenceIds()
    {
        if (!File.Exists(TokenizerPath)) return;
        var tok = T5Tokenizer.Load(TokenizerPath);
        var report = JsonDocument.Parse(File.ReadAllText(@"D:\99-scratch\unimate-fixtures\report.json")).RootElement.GetProperty("t5_ids");
        foreach (var entry in report.EnumerateObject())
        {
            var expected = entry.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(expected, tok.Encode(entry.Name).ToArray());
        }
        Assert.Equal(new[] { 389, 3735, 10681, 1039, 5, 1 }, tok.Encode("An object walks forward.").ToArray());
    }

    /// <summary>
    /// Whatever a user types reaches FLAN-T5 as the same ids upstream's tokenizer gives: every UniML3D caption plus
    /// user-style and adversarial text (fixtures/upstream_text, dev/tools/upstream_prep/make_tokenizer_fixture.py).
    /// </summary>
    [Fact]
    public void TokenizerMatchesUpstreamOnAnyText()
    {
        if (!File.Exists(TokenizerPath)) return;
        var tok = T5Tokenizer.Load(TokenizerPath);
        var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "upstream_text", "t5_token_ids.json"))).RootElement;
        int total = 0, wrong = 0;
        foreach (var c in fixture.GetProperty("cases").EnumerateArray())
        {
            var text = c[0].GetString();
            var expected = c[1].EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var actual = tok.Encode(text).ToArray();
            total++;
            if (expected.SequenceEqual(actual)) continue;
            if (++wrong <= 20) _out.WriteLine($"{JsonSerializer.Serialize(text)}: upstream [{string.Join(",", expected)}] port [{string.Join(",", actual)}]");
        }
        _out.WriteLine($"{total - wrong}/{total} identical ({fixture.GetProperty("tokenizer").GetString()})");
        Assert.Equal(0, wrong);
    }

    [Fact]
    public void EncoderPooledEmbeddingsMatchReference()
    {
        if (!File.Exists(EncoderPath)) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var encoder = new T5TextEncoder(OnnxSession.Load(EncoderPath), T5Tokenizer.Load(TokenizerPath));
        _out.WriteLine($"load {watch.ElapsedMilliseconds} ms");
        using var z = UniMateCoreTests.Open("t5_fixture.npz");
        var texts = JsonDocument.Parse(File.ReadAllText(Fixtures.Path("unimate/strings.json"))).RootElement.GetProperty("t5_fixture.npz:texts").EnumerateArray().Select(e => e.GetString()).ToArray();
        var pooled = z["pooled"];
        var worst = 0f;
        foreach (var i in new[] { 0, 1, 2, 3, 10 })
        {
            watch.Restart();
            var v = encoder.Encode(texts[i]);
            _out.WriteLine($"'{texts[i]}' {watch.ElapsedMilliseconds} ms");
            for (var c = 0; c < 768; c++) worst = MathF.Max(worst, MathF.Abs(v[c] - pooled.Values[i * 768 + c]));
        }
        _out.WriteLine($"max abs error {worst}");
        Assert.True(worst < 1e-4f, $"pooled error {worst}");
    }
}
