namespace SwingTrader.Agents.Backtesting;

// Minimal (mu/mu_w, lambda)-CMA-ES: a gradient-free evolutionary optimizer for
// small continuous search spaces with a noisy, non-smooth objective - exactly
// what a backtest score is (a 1pp weight nudge can flip a handful of trades
// across the Buy threshold and jump the result around, which rules out any
// gradient-based method). Standard algorithm per Hansen's "The CMA Evolution
// Strategy: A Tutorial"; kept to plain double[]/double[,] since the search
// dimension here (7: six live weights + Buy threshold) is tiny - no need for
// a matrix library, and a from-scratch Jacobi eigen-decomposition is both
// simple and exact at this size.
public static class CmaEs
{
    public sealed record Evaluation(double[] X, double Fitness); // fitness: lower is better

    // Hansen's default population size for a given search dimension.
    public static int ComputeLambda(int dimensions) => 4 + (int)Math.Floor(3 * Math.Log(dimensions));

    // Population size (lambda), generation count, and the resulting exact
    // evaluation budget (generations * lambda) for a target budget - exposed
    // so callers can know the REAL candidate count up front (for progress
    // bars) without duplicating the population-size formula.
    public static (int Lambda, int Generations, int ActualBudget) PlanBudget(int dimensions, int targetBudget)
    {
        var lambda = ComputeLambda(dimensions);
        var generations = Math.Max(1, targetBudget / lambda);
        return (lambda, generations, generations * lambda);
    }

    // Minimises `evaluate` over R^n starting from initialMean with isotropic
    // step size initialSigma. Runs exactly PlanBudget(n, targetBudget).ActualBudget
    // evaluations (a whole number of generations - no partial-generation
    // recombination edge cases). Deterministic for a given rngSeed regardless
    // of maxParallelism (offspring are sampled sequentially before any
    // evaluation launches, and fitness lands by offspring index, so the
    // search path never depends on task completion order); callers running
    // multiple independent restarts should pass a distinct seed per restart
    // so they don't all sample the same relative noise sequence around their
    // different starting means.
    public static async Task<List<Evaluation>> MinimizeAsync(
        int dimensions, double[] initialMean, double initialSigma, int targetBudget,
        Func<double[], CancellationToken, Task<double>> evaluate, CancellationToken ct,
        int rngSeed = 20260711, int maxParallelism = 1)
    {
        var (_, generations, _) = PlanBudget(dimensions, targetBudget);
        var search = new CmaEsSearch(dimensions, initialMean, initialSigma, rngSeed);
        await search.RunGenerationsAsync(generations, evaluate, ct, maxParallelism);
        return search.History;
    }

    internal static double NextGaussian(Random rng)
    {
        // Box-Muller.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    internal static double[,] Identity(int n)
    {
        var m = new double[n, n];
        for (var i = 0; i < n; i++) m[i, i] = 1.0;
        return m;
    }

    internal static double[,] ZeroMatrix(int n) => new double[n, n];

    internal static double[] MatVec(double[,] m, double[] v)
    {
        var n = v.Length;
        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            double sum = 0;
            for (var j = 0; j < n; j++) sum += m[i, j] * v[j];
            r[i] = sum;
        }
        return r;
    }

    // M^T * v
    internal static double[] MatTVec(double[,] m, double[] v)
    {
        var n = v.Length;
        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            double sum = 0;
            for (var j = 0; j < n; j++) sum += m[j, i] * v[j];
            r[i] = sum;
        }
        return r;
    }

    internal static double[] ElementwiseMul(double[] a, double[] b)
    {
        var r = new double[a.Length];
        for (var i = 0; i < a.Length; i++) r[i] = a[i] * b[i];
        return r;
    }

    // target += scale * (a outer b)
    internal static void AddScaledOuter(double[,] target, double[] a, double[] b, double scale)
    {
        var n = a.Length;
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            target[i, j] += scale * a[i] * b[j];
    }

    // Classic cyclic Jacobi eigenvalue algorithm for a symmetric matrix C.
    // Returns (B, D) such that C = B * diag(D^2) * B^T, i.e. B's columns are
    // the (orthonormal) eigenvectors and D holds the eigenvalues' square
    // roots - exactly the form CMA-ES needs for sampling (y = B * (D .* z))
    // and for C^{-1/2} (B * D^-1 * B^T). Reliable and exact at n~7; not
    // intended for large matrices.
    internal static (double[,] B, double[] D) EigenDecompose(double[,] c)
    {
        var n = c.GetLength(0);
        var a = (double[,])c.Clone();
        var v = Identity(n);

        for (var sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (var p = 0; p < n; p++)
            for (var q = 0; q < n; q++)
                if (p != q) off += a[p, q] * a[p, q];
            if (off < 1e-14) break;

            for (var p = 0; p < n - 1; p++)
            for (var q = p + 1; q < n; q++)
            {
                if (Math.Abs(a[p, q]) < 1e-15) continue;

                var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                var sign = theta >= 0 ? 1.0 : -1.0;
                var t = sign / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                var cosT = 1 / Math.Sqrt(t * t + 1);
                var sinT = t * cosT;

                var app = a[p, p];
                var aqq = a[q, q];
                var apq = a[p, q];
                a[p, p] = cosT * cosT * app - 2 * sinT * cosT * apq + sinT * sinT * aqq;
                a[q, q] = sinT * sinT * app + 2 * sinT * cosT * apq + cosT * cosT * aqq;
                a[p, q] = 0;
                a[q, p] = 0;

                for (var i = 0; i < n; i++)
                {
                    if (i == p || i == q) continue;
                    var aip = a[i, p];
                    var aiq = a[i, q];
                    a[i, p] = cosT * aip - sinT * aiq;
                    a[p, i] = a[i, p];
                    a[i, q] = sinT * aip + cosT * aiq;
                    a[q, i] = a[i, q];
                }
                for (var i = 0; i < n; i++)
                {
                    var vip = v[i, p];
                    var viq = v[i, q];
                    v[i, p] = cosT * vip - sinT * viq;
                    v[i, q] = sinT * vip + cosT * viq;
                }
            }
        }

        var eigenvalues = new double[n];
        for (var i = 0; i < n; i++) eigenvalues[i] = Math.Max(a[i, i], 1e-14); // guard tiny negative rounding
        var d = eigenvalues.Select(Math.Sqrt).ToArray();
        return (v, d);
    }
}

// One resumable CMA-ES search: holds the full evolving state (mean, step
// size, covariance, evolution paths, RNG) so a caller can run a few
// generations, rank this search against sibling searches, and only continue
// the promising ones - the successive-halving pattern MlSweepOptimizer uses.
// A paused-and-resumed search behaves identically to an uninterrupted one:
// the generation counter, paths and RNG all carry across RunGenerationsAsync
// calls.
public sealed class CmaEsSearch
{
    private readonly int _n;
    private readonly int _lambda;
    private readonly int _mu;
    private readonly double[] _weights;
    private readonly double _muEff;
    private readonly double _cc, _cs, _c1, _cmu, _damps, _chiN;

    private readonly double[] _mean;
    private double _sigma;
    private readonly double[] _pc;
    private readonly double[] _ps;
    private double[,] _c;
    private double[,] _b;
    private double[] _d;
    private readonly Random _rng;
    private int _generation;

    public List<CmaEs.Evaluation> History { get; } = [];

    // Best (lowest) fitness seen so far - the ranking key for
    // successive-halving survivor selection. PositiveInfinity until the
    // first generation has run.
    public double BestFitness { get; private set; } = double.PositiveInfinity;

    public CmaEsSearch(int dimensions, double[] initialMean, double initialSigma, int rngSeed)
    {
        _n = dimensions;
        _lambda = CmaEs.ComputeLambda(dimensions);
        _mu = Math.Max(1, _lambda / 2);

        var rawWeights = Enumerable.Range(1, _mu).Select(i => Math.Log(_mu + 0.5) - Math.Log(i)).ToArray();
        var weightSum = rawWeights.Sum();
        _weights = rawWeights.Select(w => w / weightSum).ToArray();
        _muEff = 1.0 / _weights.Sum(w => w * w);

        _cc = (4 + _muEff / _n) / (_n + 4 + 2 * _muEff / _n);
        _cs = (_muEff + 2) / (_n + _muEff + 5);
        _c1 = 2.0 / (Math.Pow(_n + 1.3, 2) + _muEff);
        _cmu = Math.Min(1 - _c1, 2 * (_muEff - 2 + 1 / _muEff) / (Math.Pow(_n + 2, 2) + _muEff));
        _damps = 1 + 2 * Math.Max(0, Math.Sqrt((_muEff - 1) / (_n + 1.0)) - 1) + _cs;
        _chiN = Math.Sqrt(_n) * (1 - 1.0 / (4 * _n) + 1.0 / (21.0 * _n * _n));

        _mean = (double[])initialMean.Clone();
        _sigma = initialSigma;
        _pc = new double[_n];
        _ps = new double[_n];
        _c = CmaEs.Identity(_n);
        (_b, _d) = CmaEs.EigenDecompose(_c);
        _rng = new Random(rngSeed);
    }

    public async Task RunGenerationsAsync(
        int generations,
        Func<double[], CancellationToken, Task<double>> evaluate,
        CancellationToken ct,
        int maxParallelism = 1)
    {
        for (var g = 0; g < generations; g++)
        {
            ct.ThrowIfCancellationRequested();

            // Sample the whole population up front (sequentially, so the RNG
            // stream - and therefore the search path - is identical whatever
            // maxParallelism is), then evaluate. Fitness lands by offspring
            // index, never by completion order.
            var ys = new double[_lambda][];
            var xs = new double[_lambda][];
            for (var k = 0; k < _lambda; k++)
            {
                var z = new double[_n];
                for (var i = 0; i < _n; i++) z[i] = CmaEs.NextGaussian(_rng);
                var y = CmaEs.MatVec(_b, CmaEs.ElementwiseMul(_d, z));
                var x = new double[_n];
                for (var i = 0; i < _n; i++) x[i] = _mean[i] + _sigma * y[i];
                ys[k] = y;
                xs[k] = x;
            }

            var fitness = new double[_lambda];
            if (maxParallelism <= 1)
            {
                for (var k = 0; k < _lambda; k++)
                    fitness[k] = await evaluate(xs[k], ct);
            }
            else
            {
                // Offspring within a generation are independent - the only
                // shared state is whatever the caller's evaluate captures,
                // which is the caller's concurrency contract to honour.
                using var gate = new SemaphoreSlim(maxParallelism);
                var tasks = new Task[_lambda];
                for (var k = 0; k < _lambda; k++)
                {
                    var index = k;
                    tasks[k] = Task.Run(async () =>
                    {
                        await gate.WaitAsync(ct);
                        try { fitness[index] = await evaluate(xs[index], ct); }
                        finally { gate.Release(); }
                    }, ct);
                }
                await Task.WhenAll(tasks);
            }

            for (var k = 0; k < _lambda; k++)
            {
                History.Add(new CmaEs.Evaluation(xs[k], fitness[k]));
                if (fitness[k] < BestFitness) BestFitness = fitness[k];
            }

            var order = Enumerable.Range(0, _lambda).OrderBy(k => fitness[k]).ToArray();

            var yw = new double[_n];
            for (var i = 0; i < _mu; i++)
            {
                var k = order[i];
                for (var j = 0; j < _n; j++) yw[j] += _weights[i] * ys[k][j];
            }

            // ps (conjugate evolution path) needs C^{-1/2} * yw = B * D^-1 * B^T * yw.
            var btYw = CmaEs.MatTVec(_b, yw);
            var dInvBtYw = new double[_n];
            for (var i = 0; i < _n; i++) dInvBtYw[i] = btYw[i] / _d[i];
            var cInvSqrtYw = CmaEs.MatVec(_b, dInvBtYw);

            for (var i = 0; i < _n; i++)
                _ps[i] = (1 - _cs) * _ps[i] + Math.Sqrt(_cs * (2 - _cs) * _muEff) * cInvSqrtYw[i];

            var psNorm = Math.Sqrt(_ps.Sum(v => v * v));
            _generation++;
            var hsig = psNorm / Math.Sqrt(1 - Math.Pow(1 - _cs, 2 * _generation)) < (1.4 + 2.0 / (_n + 1)) * _chiN ? 1.0 : 0.0;

            for (var i = 0; i < _n; i++)
                _pc[i] = (1 - _cc) * _pc[i] + hsig * Math.Sqrt(_cc * (2 - _cc) * _muEff) * yw[i];

            var rankMu = CmaEs.ZeroMatrix(_n);
            for (var i = 0; i < _mu; i++)
            {
                var k = order[i];
                CmaEs.AddScaledOuter(rankMu, ys[k], ys[k], _weights[i]);
            }
            var deltaHsig = (1 - hsig) * _cc * (2 - _cc);
            var pcOuter = CmaEs.ZeroMatrix(_n);
            CmaEs.AddScaledOuter(pcOuter, _pc, _pc, 1.0);

            for (var p = 0; p < _n; p++)
            for (var q = 0; q < _n; q++)
                _c[p, q] = (1 - _c1 - _cmu) * _c[p, q] + _c1 * (pcOuter[p, q] + deltaHsig * _c[p, q]) + _cmu * rankMu[p, q];

            for (var i = 0; i < _n; i++) _mean[i] += _sigma * yw[i];
            _sigma *= Math.Exp((_cs / _damps) * (psNorm / _chiN - 1));

            (_b, _d) = CmaEs.EigenDecompose(_c);
        }
    }
}
