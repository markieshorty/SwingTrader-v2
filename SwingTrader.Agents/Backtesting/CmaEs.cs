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
    // recombination edge cases). Deterministic for a given rngSeed so repeated
    // sweeps on the same data retrace the same search path; callers running
    // multiple independent restarts (multi-start search) should pass a
    // distinct seed per restart so they don't all sample the same relative
    // noise sequence around their different starting means.
    public static async Task<List<Evaluation>> MinimizeAsync(
        int dimensions, double[] initialMean, double initialSigma, int targetBudget,
        Func<double[], CancellationToken, Task<double>> evaluate, CancellationToken ct,
        int rngSeed = 20260711)
    {
        var n = dimensions;
        var (lambda, generations, _) = PlanBudget(n, targetBudget);
        var mu = Math.Max(1, lambda / 2);

        var rawWeights = Enumerable.Range(1, mu).Select(i => Math.Log(mu + 0.5) - Math.Log(i)).ToArray();
        var weightSum = rawWeights.Sum();
        var weights = rawWeights.Select(w => w / weightSum).ToArray();
        var muEff = 1.0 / weights.Sum(w => w * w);

        var cc = (4 + muEff / n) / (n + 4 + 2 * muEff / n);
        var cs = (muEff + 2) / (n + muEff + 5);
        var c1 = 2.0 / (Math.Pow(n + 1.3, 2) + muEff);
        var cmu = Math.Min(1 - c1, 2 * (muEff - 2 + 1 / muEff) / (Math.Pow(n + 2, 2) + muEff));
        var damps = 1 + 2 * Math.Max(0, Math.Sqrt((muEff - 1) / (n + 1.0)) - 1) + cs;
        var chiN = Math.Sqrt(n) * (1 - 1.0 / (4 * n) + 1.0 / (21.0 * n * n));

        var mean = (double[])initialMean.Clone();
        var sigma = initialSigma;
        var pc = new double[n];
        var ps = new double[n];
        var c = Identity(n);
        var (b, d) = EigenDecompose(c);

        var rng = new Random(rngSeed);
        var history = new List<Evaluation>();

        for (var gen = 0; gen < generations; gen++)
        {
            var ys = new double[lambda][];
            var fitness = new double[lambda];

            for (var k = 0; k < lambda; k++)
            {
                ct.ThrowIfCancellationRequested();
                var z = new double[n];
                for (var i = 0; i < n; i++) z[i] = NextGaussian(rng);
                var y = MatVec(b, ElementwiseMul(d, z));
                var x = new double[n];
                for (var i = 0; i < n; i++) x[i] = mean[i] + sigma * y[i];

                ys[k] = y;
                fitness[k] = await evaluate(x, ct);
                history.Add(new Evaluation(x, fitness[k]));
            }

            var order = Enumerable.Range(0, lambda).OrderBy(k => fitness[k]).ToArray();

            var yw = new double[n];
            for (var i = 0; i < mu; i++)
            {
                var k = order[i];
                for (var j = 0; j < n; j++) yw[j] += weights[i] * ys[k][j];
            }

            // ps (conjugate evolution path) needs C^{-1/2} * yw = B * D^-1 * B^T * yw.
            var btYw = MatTVec(b, yw);
            var dInvBtYw = new double[n];
            for (var i = 0; i < n; i++) dInvBtYw[i] = btYw[i] / d[i];
            var cInvSqrtYw = MatVec(b, dInvBtYw);

            for (var i = 0; i < n; i++)
                ps[i] = (1 - cs) * ps[i] + Math.Sqrt(cs * (2 - cs) * muEff) * cInvSqrtYw[i];

            var psNorm = Math.Sqrt(ps.Sum(v => v * v));
            var hsig = psNorm / Math.Sqrt(1 - Math.Pow(1 - cs, 2 * (gen + 1))) < (1.4 + 2.0 / (n + 1)) * chiN ? 1.0 : 0.0;

            for (var i = 0; i < n; i++)
                pc[i] = (1 - cc) * pc[i] + hsig * Math.Sqrt(cc * (2 - cc) * muEff) * yw[i];

            var rankMu = ZeroMatrix(n);
            for (var i = 0; i < mu; i++)
            {
                var k = order[i];
                AddScaledOuter(rankMu, ys[k], ys[k], weights[i]);
            }
            var deltaHsig = (1 - hsig) * cc * (2 - cc);
            var pcOuter = ZeroMatrix(n);
            AddScaledOuter(pcOuter, pc, pc, 1.0);

            for (var p = 0; p < n; p++)
            for (var q = 0; q < n; q++)
                c[p, q] = (1 - c1 - cmu) * c[p, q] + c1 * (pcOuter[p, q] + deltaHsig * c[p, q]) + cmu * rankMu[p, q];

            for (var i = 0; i < n; i++) mean[i] += sigma * yw[i];
            sigma *= Math.Exp((cs / damps) * (psNorm / chiN - 1));

            (b, d) = EigenDecompose(c);
        }

        return history;
    }

    private static double NextGaussian(Random rng)
    {
        // Box-Muller.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private static double[,] Identity(int n)
    {
        var m = new double[n, n];
        for (var i = 0; i < n; i++) m[i, i] = 1.0;
        return m;
    }

    private static double[,] ZeroMatrix(int n) => new double[n, n];

    private static double[] MatVec(double[,] m, double[] v)
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
    private static double[] MatTVec(double[,] m, double[] v)
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

    private static double[] ElementwiseMul(double[] a, double[] b)
    {
        var r = new double[a.Length];
        for (var i = 0; i < a.Length; i++) r[i] = a[i] * b[i];
        return r;
    }

    // target += scale * (a outer b)
    private static void AddScaledOuter(double[,] target, double[] a, double[] b, double scale)
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
    private static (double[,] B, double[] D) EigenDecompose(double[,] c)
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
