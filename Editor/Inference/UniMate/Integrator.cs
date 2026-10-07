using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TextToAnimation.EditorTools.Inference.Onnx;

namespace TextToAnimation.EditorTools.Inference.UniMate;

/// <summary>
/// ODE integrators for the flow: Euler (1 model call per step, 1st order), Heun (2 calls, 2nd order) and
/// Adams-Bashforth 2 (1 call per step, 2nd order: reuses the previous step's velocity).
/// Dopri5: upstream's text-to-motion sampler (Sampler.sample_ode: torchdiffeq dopri5, adaptive, rtol 1e-3,
/// atol 1e-6, t from 0 to 1) - integrates the flow to convergence.
/// </summary>
public enum Integrator { Euler, Heun, AdamsBashforth2, Dopri5 }
