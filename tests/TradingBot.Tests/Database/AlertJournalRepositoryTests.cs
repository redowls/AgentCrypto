using FluentAssertions;
using TradingBot.Data.Abstractions;
using TradingBot.Data.Connection;
using TradingBot.Data.Repositories;
using Xunit;

namespace TradingBot.Tests.Database;

[Collection(SqlServerCollection.Name)]
public sealed class AlertJournalRepositoryTests(SqlServerFixture fixture)
{
    private IDbConnectionFactory Cf => new SqlConnectionFactory(fixture.ConnectionString);

    [Fact]
    public async Task Insert_then_GetWindow_returns_inserted_row()
    {
        var repo = new AlertJournalRepository(Cf);
        var now  = DateTime.UtcNow;
        var row  = new AlertJournalRow(
            SentAtUtc: now, Severity: 3, Title: "kill switch tripped",
            Body: "daily loss limit", Fingerprint: new string('a', 64),
            Transports: "Log,Telegram", InstanceId: "bot-test", CorrelationId: null);

        await repo.InsertAsync(row, default);
        var rows = await repo.GetWindowAsync(severity: 3, now.AddMinutes(-1), now.AddMinutes(1), default);

        rows.Should().ContainSingle(r => r.Title == "kill switch tripped" && r.Severity == 3);
    }

    [Fact]
    public async Task GetWindow_with_null_severity_returns_all_severities()
    {
        var repo = new AlertJournalRepository(Cf);
        var now  = DateTime.UtcNow;
        await repo.InsertAsync(new AlertJournalRow(now, 1, "warn-a", "x", new string('b', 64), "Log", "bot", null), default);
        await repo.InsertAsync(new AlertJournalRow(now, 2, "err-a",  "x", new string('c', 64), "Log", "bot", null), default);

        var rows = await repo.GetWindowAsync(severity: null, now.AddSeconds(-30), now.AddSeconds(30), default);

        rows.Should().HaveCountGreaterOrEqualTo(2);
        rows.Should().Contain(r => r.Severity == 1);
        rows.Should().Contain(r => r.Severity == 2);
    }
}
