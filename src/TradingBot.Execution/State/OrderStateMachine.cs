using TradingBot.Core.Domain.Enums;

namespace TradingBot.Execution.State;

/// <summary>
/// §6.4 — order state machine. Encodes every legal transition for the entry
/// + bracket lifecycle so that callers (the engine, the userData reactor,
/// reconciliation) cannot accidentally regress an order's state.
///
/// The transition matrix is the single source of truth. Tests enumerate
/// (from, to) ∈ AllStates × AllStates and assert that everything not in
/// <see cref="LegalTransitions"/> is rejected.
///
/// State codes match <see cref="OrderStatuses"/> verbatim — what the DB row
/// stores is what the machine reads.
/// </summary>
public sealed class OrderStateMachine
{
    /// All states the machine recognises.
    public static readonly IReadOnlyList<string> AllStates = new[]
    {
        OrderStatuses.Pending,
        OrderStatuses.Submitting,
        OrderStatuses.New,
        OrderStatuses.PartiallyFilled,
        OrderStatuses.Filled,
        OrderStatuses.Canceling,
        OrderStatuses.Cancelled,
        OrderStatuses.Rejected,
        OrderStatuses.Expired,
        OrderStatuses.Error,
    };

    /// Terminal states — orders here MUST never transition further. The state
    /// machine returns <see cref="TransitionResult.AlreadyTerminal"/> for any
    /// attempted move out of these.
    public static readonly IReadOnlySet<string> TerminalStates = new HashSet<string>(StringComparer.Ordinal)
    {
        OrderStatuses.Filled,
        OrderStatuses.Cancelled,
        OrderStatuses.Rejected,
        OrderStatuses.Expired,
        OrderStatuses.Error,
    };

    /// §6.4 transition table. Each entry is (FromState, ToState).
    /// Re-entrant transitions (X → X) are NOT included — duplicate WS events
    /// are handled by <see cref="TryTransition"/> returning <c>NoOp</c>.
    public static readonly IReadOnlySet<(string From, string To)> LegalTransitions =
        new HashSet<(string, string)>
        {
            // Local lifecycle before the exchange knows we exist.
            (OrderStatuses.Pending,    OrderStatuses.Submitting),
            (OrderStatuses.Pending,    OrderStatuses.Error),       // submit refused before REST call

            // REST call result.
            (OrderStatuses.Submitting, OrderStatuses.New),         // happy path: NEW from exchange
            (OrderStatuses.Submitting, OrderStatuses.PartiallyFilled), // immediately partially filled
            (OrderStatuses.Submitting, OrderStatuses.Filled),      // immediately filled (market)
            (OrderStatuses.Submitting, OrderStatuses.Rejected),    // exchange rejected
            (OrderStatuses.Submitting, OrderStatuses.Expired),     // IOC/FOK with no liquidity
            (OrderStatuses.Submitting, OrderStatuses.Error),       // unrecoverable HTTP/RPC error
            (OrderStatuses.Submitting, OrderStatuses.Pending),     // explicit retry after network drop (idempotent re-submit)

            // Acknowledged at the exchange — fills accumulate.
            (OrderStatuses.New,             OrderStatuses.PartiallyFilled),
            (OrderStatuses.New,             OrderStatuses.Filled),
            (OrderStatuses.New,             OrderStatuses.Canceling),
            (OrderStatuses.New,             OrderStatuses.Cancelled),  // exchange-initiated cancel (e.g. self-trade prevention)
            (OrderStatuses.New,             OrderStatuses.Expired),
            (OrderStatuses.PartiallyFilled, OrderStatuses.PartiallyFilled), // accumulating fills — same state, new fill row
            (OrderStatuses.PartiallyFilled, OrderStatuses.Filled),
            (OrderStatuses.PartiallyFilled, OrderStatuses.Canceling),
            (OrderStatuses.PartiallyFilled, OrderStatuses.Cancelled),
            (OrderStatuses.PartiallyFilled, OrderStatuses.Expired),

            // Cancel intent issued; exchange confirms.
            (OrderStatuses.Canceling, OrderStatuses.Cancelled),
            (OrderStatuses.Canceling, OrderStatuses.Filled),         // fill landed before cancel was processed
            (OrderStatuses.Canceling, OrderStatuses.PartiallyFilled),// same as above with leftover qty cancelled later

            // Reconciliation may discover an order we thought was Submitting/Pending
            // is actually NEW or beyond — those are covered above. The reverse
            // (e.g. NEW → Pending) is NEVER legal: once the exchange has it, we
            // do not regress.
        };

    public TransitionResult TryTransition(string fromState, string toState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromState);
        ArgumentException.ThrowIfNullOrWhiteSpace(toState);

        if (!AllStates.Contains(fromState, StringComparer.Ordinal))
            return TransitionResult.UnknownState($"unknown from-state '{fromState}'");
        if (!AllStates.Contains(toState, StringComparer.Ordinal))
            return TransitionResult.UnknownState($"unknown to-state '{toState}'");

        if (string.Equals(fromState, toState, StringComparison.Ordinal))
        {
            // Re-entrant updates (e.g. another partial fill arriving while
            // we're already PARTIALLY_FILLED) are explicitly allowed only for
            // PARTIALLY_FILLED so callers can layer multiple fill rows.
            return string.Equals(fromState, OrderStatuses.PartiallyFilled, StringComparison.Ordinal)
                ? TransitionResult.Ok()
                : TransitionResult.NoOp();
        }

        if (TerminalStates.Contains(fromState))
            return TransitionResult.AlreadyTerminal($"order is in terminal state '{fromState}'");

        return LegalTransitions.Contains((fromState, toState))
            ? TransitionResult.Ok()
            : TransitionResult.Illegal($"illegal transition '{fromState}' → '{toState}'");
    }

    public bool IsTerminal(string state) => TerminalStates.Contains(state);
}

/// Outcome of a transition attempt.
public readonly record struct TransitionResult(TransitionOutcome Outcome, string? Reason)
{
    public bool IsAccepted => Outcome is TransitionOutcome.Accepted or TransitionOutcome.NoOp;

    public static TransitionResult Ok()                     => new(TransitionOutcome.Accepted, null);
    public static TransitionResult NoOp()                   => new(TransitionOutcome.NoOp, "no state change");
    public static TransitionResult Illegal(string reason)         => new(TransitionOutcome.IllegalTransition, reason);
    public static TransitionResult AlreadyTerminal(string reason) => new(TransitionOutcome.AlreadyTerminal, reason);
    public static TransitionResult UnknownState(string reason)    => new(TransitionOutcome.UnknownState, reason);
}

public enum TransitionOutcome
{
    Accepted = 0,
    NoOp = 1,
    IllegalTransition = 2,
    AlreadyTerminal = 3,
    UnknownState = 4,
}
