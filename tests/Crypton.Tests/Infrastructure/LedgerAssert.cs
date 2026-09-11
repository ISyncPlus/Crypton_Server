using Crypton.Core.Admin;
using Microsoft.Extensions.DependencyInjection;

namespace Crypton.Tests.Infrastructure;

public static class LedgerAssert
{
    /// <summary>Runs every ledger invariant against the database and fails with details if any is violated.</summary>
    public static async Task InvariantsHoldAsync(CryptonFactory factory)
    {
        await using var scope = factory.Scope();
        var report = await scope.ServiceProvider.GetRequiredService<LedgerCheckService>().RunAsync();
        var failures = report.Checks.Where(c => !c.Ok).Select(c => $"{c.Name}: {string.Join(" | ", c.Problems)}").ToList();
        Assert.True(report.Ok, "Ledger invariants violated:\n" + string.Join("\n", failures));
    }
}
