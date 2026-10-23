using ByeDpiPc.Core.Engine;

namespace ByeDpiPc.Core.Testing;

public sealed class StrategyResult
{
    public required string Arguments { get; init; }
    public int Success { get; set; }
    public int Total { get; set; }
    public TimeSpan AverageTime { get; set; }
    public string? Error { get; set; }
    public List<SiteResult> Sites { get; } = new();
    public double Percent => Total == 0 ? 0 : 100.0 * Success / Total;
}

public sealed record TestProgress(int Done, int Total, string? Current, StrategyResult? Finished);

/// <summary>
/// Подбор стратегии: для каждой стратегии поднимаем ciadpi на свободном порту и через него проверяем сайты.
/// Логика повторяет «Подбор» ByeByeDPI (TestService.kt), но стратегии проверяются параллельно.
/// </summary>
public sealed class StrategyTester
{
    public int Parallel { get; init; } = 4;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
    public string FakeSni { get; init; } = "www.iana.org";
    public int SiteConcurrency { get; init; } = 8;

    /// <summary>Сначала — без обхода, чтобы показать, какие сайты реально блокируются.</summary>
    public async Task<List<SiteResult>> CheckDirectAsync(IReadOnlyList<string> sites, CancellationToken ct)
    {
        using var checker = new SiteChecker(null, Timeout);
        return await checker.CheckAllAsync(sites, SiteConcurrency * 2, ct);
    }

    /// <summary>Остановить перебор, как только найдена стратегия, открывшая все сайты (для автоподбора).</summary>
    public bool StopOnFirstPerfect { get; init; }

    public async Task<List<StrategyResult>> RunAsync(IReadOnlyList<string> strategies, IReadOnlyList<string> sites,
        IProgress<TestProgress>? progress, CancellationToken ct)
    {
        var results = new List<StrategyResult>();
        int done = 0;
        using var inner = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var sem = new SemaphoreSlim(Parallel);
        var tasks = strategies.Select(async args =>
        {
            await sem.WaitAsync(inner.Token);
            try
            {
                progress?.Report(new TestProgress(Volatile.Read(ref done), strategies.Count, args, null));
                var r = await TestOneAsync(args, sites, inner.Token);
                lock (results) results.Add(r);
                progress?.Report(new TestProgress(Interlocked.Increment(ref done), strategies.Count, null, r));
                if (StopOnFirstPerfect && r.Total > 0 && r.Success == r.Total) inner.Cancel();
            }
            finally
            {
                sem.Release();
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Найдена идеальная стратегия — остальные можно не проверять.
        }
        lock (results)
            return results.OrderByDescending(r => r.Success).ThenBy(r => r.AverageTime).ToList();
    }

    public async Task<StrategyResult> TestOneAsync(string args, IReadOnlyList<string> sites, CancellationToken ct)
    {
        var result = new StrategyResult { Arguments = args, Total = sites.Count };
        ChildProcess? proc = null;
        try
        {
            var port = NetUtil.GetFreePort();
            proc = await CiadpiEngine.StartAsync(args, port, FakeSni, logOutput: false, ct: ct);
            using var checker = new SiteChecker(port, Timeout);
            var sitesResult = await checker.CheckAllAsync(sites, SiteConcurrency, ct);
            result.Sites.AddRange(sitesResult);
            var ok = sitesResult.Where(s => s.Ok).ToList();
            result.Success = ok.Count;
            result.AverageTime = ok.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(ok.Average(s => s.Elapsed.TotalMilliseconds));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UserFacingException ex)
        {
            result.Error = ex.Message;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            if (proc != null)
            {
                await proc.StopAsync();
                proc.Dispose();
            }
        }
        return result;
    }
}
