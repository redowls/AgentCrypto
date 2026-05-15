using Microsoft.Extensions.Options;
using TradingBot.Core.Domain.Enums;
using TradingBot.Execution.Configuration;

namespace TradingBot.Execution.Brackets;

public sealed class BracketPlacerResolver : IBracketPlacerResolver
{
    private readonly SpotOcoBracketPlacer _spotOco;
    private readonly FuturesEmulatedBracketPlacer _futuresEmu;
    private readonly ExecutionOptions _options;

    public BracketPlacerResolver(
        SpotOcoBracketPlacer spotOco,
        FuturesEmulatedBracketPlacer futuresEmu,
        IOptions<ExecutionOptions> options)
    {
        _spotOco    = spotOco;
        _futuresEmu = futuresEmu;
        _options    = options.Value;
    }

    public IBracketPlacer Resolve(string accountType)
    {
        if (string.Equals(accountType, AccountTypes.UmFut, StringComparison.OrdinalIgnoreCase))
            return _futuresEmu;

        // SPOT: native OCO when enabled, else fall back to the futures-style
        // emulated bracket (used in tests / spot-without-OCO accounts).
        return _options.EnableSpotNativeOco ? _spotOco : _futuresEmu;
    }
}
