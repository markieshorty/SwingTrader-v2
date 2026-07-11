using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using Xunit;

namespace SwingTrader.Tests;

// The generic CMA-ES core has no domain knowledge - it's pure numerics, so it
// can be verified against a textbook test function (a shifted quadratic bowl)
// rather than needing a real backtest. If the eigen-decomposition or the
// evolution-path math were wrong, it wouldn't reliably converge on even the
// simplest possible objective.
public class CmaEsTests
{
    [Fact]
    public async Task MinimizeAsync_QuadraticBowl_ConvergesTowardTheMinimum()
    {
        // f(x) = sum((x_i - target_i)^2), minimised at x = target. Starting
        // the search at the origin (far from target) and a generous budget
        // should land close to it.
        var target = new[] { 3.0, -2.0, 1.5, 0.5 };
        Task<double> Evaluate(double[] x, CancellationToken ct) =>
            Task.FromResult(x.Select((v, i) => (v - target[i]) * (v - target[i])).Sum());

        var history = await CmaEs.MinimizeAsync(
            dimensions: target.Length, initialMean: new double[target.Length], initialSigma: 2.0,
            targetBudget: 400, Evaluate, CancellationToken.None);

        var best = history.MinBy(e => e.Fitness)!;
        best.Fitness.Should().BeLessThan(0.5); // started at distance^2 = 9+4+2.25+0.25 = 15.5
        for (var i = 0; i < target.Length; i++)
            best.X[i].Should().BeApproximately(target[i], 1.0);
    }

    [Fact]
    public async Task MinimizeAsync_IsDeterministic_SameInputsSameSearchPath()
    {
        Task<double> Evaluate(double[] x, CancellationToken ct) => Task.FromResult(x.Sum(v => v * v));

        var a = await CmaEs.MinimizeAsync(3, new double[3], 1.0, 100, Evaluate, CancellationToken.None);
        var b = await CmaEs.MinimizeAsync(3, new double[3], 1.0, 100, Evaluate, CancellationToken.None);

        a.Select(e => e.Fitness).Should().Equal(b.Select(e => e.Fitness));
    }

    [Fact]
    public async Task MinimizeAsync_RunsExactlyGenerationsTimesLambdaEvaluations()
    {
        var evalCount = 0;
        Task<double> Evaluate(double[] x, CancellationToken ct)
        {
            evalCount++;
            return Task.FromResult(x.Sum(v => v * v));
        }

        var (_, _, actualBudget) = CmaEs.PlanBudget(dimensions: 7, targetBudget: 400);
        var history = await CmaEs.MinimizeAsync(7, new double[7], 1.0, 400, Evaluate, CancellationToken.None);

        history.Count.Should().Be(actualBudget);
        evalCount.Should().Be(actualBudget);
    }

    [Fact]
    public void PlanBudget_NeverExceedsTargetBudget()
    {
        var (lambda, generations, actualBudget) = CmaEs.PlanBudget(dimensions: 7, targetBudget: 400);

        lambda.Should().BeGreaterThan(0);
        generations.Should().BeGreaterThan(0);
        actualBudget.Should().Be(lambda * generations);
        actualBudget.Should().BeLessThanOrEqualTo(400);
    }
}
