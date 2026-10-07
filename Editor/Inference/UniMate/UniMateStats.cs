using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TextToAnimation.EditorTools.Inference.UniMate;

using Vector3 = System.Numerics.Vector3; // s&box declares a global Vector3 that would shadow System.Numerics

/// <summary>Per-joint feature normalisation (dataset_stats.npy, "mixamo" family for humanoids).</summary>
public sealed class UniMateStats
{
	public double[] MeanRoot { get; init; }
	public double[] StdRoot { get; init; }
	public double[] MeanLocal { get; init; }
	public double[] StdLocal { get; init; }

	/// <summary>The "mixamo" family (identical in the mixamo and uniml3d v2 checkpoints).</summary>
	public static UniMateStats Mixamo { get; } = new()
	{
		MeanRoot = new[] { 0, .7316110, 0, .8149023, 0, .0301355, 0, 1, 0, 1.17816e-4, -3.75700e-4, 5.10041e-3 },
		StdRoot = new[] { 1e-8, .2257179, 1e-8, .4232829, 1e-8, .3947881, 1e-8, 1e-8, 1e-8, .01295892, .01285423, .02288527 },
		MeanLocal = new[] { 4.378096e-3, .7289839, .06604796, .8552106, -4.192105e-4, -.01129483, -1.251617e-3, .8568652, .03533302, 1.560537e-4, -3.841781e-4, 5.194266e-3 },
		StdLocal = new[] { .1735992, .4494819, .1803030, .3065769, .2421027, .3404175, .2567018, .2735683, .3518474, .01786926, .01843701, .02884578 },
	};

	/// <summary>The "truebones" family: animals and creatures (Truebones ZOO), from the v2 checkpoint's dataset_stats.npy.</summary>
	public static UniMateStats Truebones { get; } = new()
	{
		MeanRoot = new[] { 0.0, 0.48069225491455897, 0.0, 0.903859583454801, 0.0, -0.014345269650504892, 0.0, 1.0, 0.0, -0.00014077468388700855, -0.000404235698352694, 0.004545269960350057 },
		StdRoot = new[] { 1e-08, 0.5120161120918413, 1e-08, 0.34060428431706286, 1e-08, 0.2584971724031606, 1e-08, 1e-08, 1e-08, 0.010741314496406322, 0.022964643986454293, 0.038637982336550476 },
		MeanLocal = new[] { 0.0034326478177160564, 0.4201169709789045, 0.17302418616708506, 0.931803532615029, -8.806489049812035e-05, -0.0013611709276153117, -0.0010395087357191405, 0.8737699146943557, 0.015044935017066588, -0.00011217976101921654, -0.00038734163543707083, 0.004444652326470307 },
		StdLocal = new[] { 0.18108968845406656, 0.5458403371137666, 0.3594064892402845, 0.20210082777971936, 0.20512571015928063, 0.22095025346633343, 0.21187683541200106, 0.2430231804211249, 0.36379478545017124, 0.019208565156364925, 0.030045344004136665, 0.04252806832560322 },
	};

	/// <summary>The "objaverse" family: other rigged objects and characters, from the v2 checkpoint's dataset_stats.npy.</summary>
	public static UniMateStats Objaverse { get; } = new()
	{
		MeanRoot = new[] { 0.0, 0.6201127556231308, 0.0, 0.9125788953879085, 0.0, 0.02000543143185833, 0.0, 1.0, 0.0, -5.151556896122859e-06, -0.0002666258097478285, 0.0010638856244688723 },
		StdRoot = new[] { 1e-08, 0.20926810086490216, 1e-08, 0.2797461636510511, 1e-08, 0.297559450073977, 1e-08, 1e-08, 1e-08, 0.006560634281071742, 0.010966058516502264, 0.009710697991196067 },
		MeanLocal = new[] { 0.00508068690387248, 0.665100225663909, 0.09798075323139123, 0.8866661588056203, -0.0006223255379864393, -0.00473312197030773, -0.0017531213071317981, 0.8647077970053488, 0.011217327098931291, -1.5249411222662456e-05, -0.00022626020801684053, 0.0011629571811297447 },
		StdLocal = new[] { 0.22360356966103292, 0.38426552225471805, 0.23085540877950397, 0.2335058275779389, 0.2983238165839611, 0.26510047537141035, 0.30265997500502617, 0.24755531713729867, 0.3150632133772523, 0.019350011463484817, 0.024180478839908665, 0.029932337028051675 },
	};

	/// <summary>The statistics for a rig family (humanoid: Mixamo, animal: Truebones, object: Objaverse).</summary>
	public static UniMateStats For( TextToAnimation.Core.Generation.RigFamily family ) => family switch
	{
		TextToAnimation.Core.Generation.RigFamily.Animal => Truebones,
		TextToAnimation.Core.Generation.RigFamily.Object => Objaverse,
		_ => Mixamo,
	};

	public double Mean( int joint, int channel ) => joint == 0 ? MeanRoot[channel] : MeanLocal[channel];
	public double Std( int joint, int channel ) => joint == 0 ? StdRoot[channel] : StdLocal[channel];

	public float Normalize( int joint, int channel, double value )
	{
		var v = (value - Mean( joint, channel )) / Std( joint, channel );
		return double.IsFinite( v ) ? (float)v : 0f;
	}

	public double Denormalize( int joint, int channel, float value ) => value * Std( joint, channel ) + Mean( joint, channel );
}
