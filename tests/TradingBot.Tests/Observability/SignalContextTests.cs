using FluentAssertions;
using TradingBot.Core.Observability;
using Xunit;

namespace TradingBot.Tests.Observability;

public class SignalContextTests
{
    [Fact]
    public void Current_is_null_outside_scope()
    {
        SignalContext.Current.Should().BeNull();
    }

    [Fact]
    public void Begin_pushes_id_and_dispose_pops()
    {
        var id = Guid.NewGuid();
        using (SignalContext.BeginSignal(id))
        {
            SignalContext.Current.Should().Be(id);
        }
        SignalContext.Current.Should().BeNull();
    }

    [Fact]
    public void Nested_begin_restores_outer_on_inner_dispose()
    {
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();
        using (SignalContext.BeginSignal(outer))
        {
            using (SignalContext.BeginSignal(inner))
            {
                SignalContext.Current.Should().Be(inner);
            }
            SignalContext.Current.Should().Be(outer);
        }
    }

    [Fact]
    public async Task AsyncLocal_flows_across_await()
    {
        var id = Guid.NewGuid();
        using var _ = SignalContext.BeginSignal(id);
        await Task.Yield();
        SignalContext.Current.Should().Be(id);
    }
}
