using FluentAssertions;
using TradingBot.Core.Domain.Enums;
using TradingBot.Execution.State;
using Xunit;

namespace TradingBot.Tests.Execution;

public class OrderStateMachineTests
{
    private readonly OrderStateMachine _sut = new();

    [Theory]
    [InlineData(OrderStatuses.Pending,         OrderStatuses.Submitting)]
    [InlineData(OrderStatuses.Pending,         OrderStatuses.Error)]
    [InlineData(OrderStatuses.Submitting,      OrderStatuses.New)]
    [InlineData(OrderStatuses.Submitting,      OrderStatuses.PartiallyFilled)]
    [InlineData(OrderStatuses.Submitting,      OrderStatuses.Filled)]
    [InlineData(OrderStatuses.Submitting,      OrderStatuses.Rejected)]
    [InlineData(OrderStatuses.Submitting,      OrderStatuses.Expired)]
    [InlineData(OrderStatuses.Submitting,      OrderStatuses.Error)]
    [InlineData(OrderStatuses.Submitting,      OrderStatuses.Pending)]      // network-drop retry
    [InlineData(OrderStatuses.New,             OrderStatuses.PartiallyFilled)]
    [InlineData(OrderStatuses.New,             OrderStatuses.Filled)]
    [InlineData(OrderStatuses.New,             OrderStatuses.Canceling)]
    [InlineData(OrderStatuses.New,             OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.New,             OrderStatuses.Expired)]
    [InlineData(OrderStatuses.PartiallyFilled, OrderStatuses.Filled)]
    [InlineData(OrderStatuses.PartiallyFilled, OrderStatuses.Canceling)]
    [InlineData(OrderStatuses.PartiallyFilled, OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.PartiallyFilled, OrderStatuses.Expired)]
    [InlineData(OrderStatuses.Canceling,       OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Canceling,       OrderStatuses.Filled)]
    [InlineData(OrderStatuses.Canceling,       OrderStatuses.PartiallyFilled)]
    public void Legal_transitions_are_accepted(string from, string to)
    {
        var result = _sut.TryTransition(from, to);
        result.Outcome.Should().Be(TransitionOutcome.Accepted);
        result.IsAccepted.Should().BeTrue();
    }

    [Theory]
    // Cannot regress from acknowledged states back to local-only states.
    [InlineData(OrderStatuses.New,             OrderStatuses.Pending)]
    [InlineData(OrderStatuses.New,             OrderStatuses.Submitting)]
    [InlineData(OrderStatuses.PartiallyFilled, OrderStatuses.Pending)]
    [InlineData(OrderStatuses.PartiallyFilled, OrderStatuses.New)]
    // Cannot skip Submitting from Pending.
    [InlineData(OrderStatuses.Pending,         OrderStatuses.New)]
    [InlineData(OrderStatuses.Pending,         OrderStatuses.PartiallyFilled)]
    [InlineData(OrderStatuses.Pending,         OrderStatuses.Filled)]
    public void Illegal_transitions_are_rejected(string from, string to)
    {
        var result = _sut.TryTransition(from, to);
        result.IsAccepted.Should().BeFalse();
        result.Outcome.Should().Be(TransitionOutcome.IllegalTransition);
    }

    [Theory]
    [InlineData(OrderStatuses.Filled)]
    [InlineData(OrderStatuses.Cancelled)]
    [InlineData(OrderStatuses.Rejected)]
    [InlineData(OrderStatuses.Expired)]
    [InlineData(OrderStatuses.Error)]
    public void Terminal_states_refuse_any_outbound_transition(string terminal)
    {
        foreach (var to in OrderStateMachine.AllStates)
        {
            if (to == terminal) continue;
            var result = _sut.TryTransition(terminal, to);
            result.Outcome.Should().BeOneOf(TransitionOutcome.AlreadyTerminal, TransitionOutcome.NoOp);
        }
    }

    [Fact]
    public void PartiallyFilled_to_PartiallyFilled_is_accepted_for_layered_fills()
    {
        var result = _sut.TryTransition(OrderStatuses.PartiallyFilled, OrderStatuses.PartiallyFilled);
        result.Outcome.Should().Be(TransitionOutcome.Accepted);
    }

    [Fact]
    public void Same_state_for_non_PartiallyFilled_is_NoOp()
    {
        _sut.TryTransition(OrderStatuses.New, OrderStatuses.New).Outcome.Should().Be(TransitionOutcome.NoOp);
        _sut.TryTransition(OrderStatuses.Pending, OrderStatuses.Pending).Outcome.Should().Be(TransitionOutcome.NoOp);
    }

    [Fact]
    public void Unknown_states_return_UnknownState()
    {
        var r = _sut.TryTransition("MOON", OrderStatuses.Filled);
        r.Outcome.Should().Be(TransitionOutcome.UnknownState);
    }

    [Fact]
    public void Every_pair_in_AllStates_is_either_legal_illegal_or_noop_or_terminal()
    {
        // Defence-in-depth: enumerate the matrix and assert no exception is
        // thrown and every result has an Outcome value.
        foreach (var from in OrderStateMachine.AllStates)
        foreach (var to in OrderStateMachine.AllStates)
        {
            var r = _sut.TryTransition(from, to);
            Enum.IsDefined(typeof(TransitionOutcome), r.Outcome).Should().BeTrue();
        }
    }

    [Fact]
    public void IsTerminal_matches_TerminalStates_set()
    {
        foreach (var s in OrderStateMachine.AllStates)
            _sut.IsTerminal(s).Should().Be(OrderStateMachine.TerminalStates.Contains(s));
    }
}
