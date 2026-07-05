using System;
using System.Collections.Generic;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Mt5
{
    public class Mt5SymbolMapper : ISymbolMapper
    {
        private const string BrokerSuffix = ".p";

        private static readonly Dictionary<string, SecurityType> KnownForexSymbols = new(StringComparer.OrdinalIgnoreCase)
        {
            { "EURUSD", SecurityType.Forex },
            { "GBPUSD", SecurityType.Forex },
            { "USDJPY", SecurityType.Forex },
            { "AUDUSD", SecurityType.Forex },
            { "USDCAD", SecurityType.Forex },
            { "NZDUSD", SecurityType.Forex },
            { "EURGBP", SecurityType.Forex },
            { "EURJPY", SecurityType.Forex },
            { "GBPJPY", SecurityType.Forex },
        };

        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (symbol.SecurityType == SecurityType.Forex)
            {
                return symbol.Value.ToUpperInvariant() + BrokerSuffix;
            }
            return symbol.Value;
        }

        public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market,
            DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = 0)
        {
            var leanSymbol = brokerageSymbol;
            if (brokerageSymbol.EndsWith(BrokerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                leanSymbol = brokerageSymbol[..^BrokerSuffix.Length];
            }

            if (securityType == SecurityType.Forex)
            {
                market ??= Market.FXCM;
                return Symbol.Create(leanSymbol.ToUpperInvariant(), SecurityType.Forex, market);
            }

            market ??= Market.USA;
            return Symbol.Create(leanSymbol, securityType, market);
        }

        public static string ToBrokerSymbol(string leanSymbol)
        {
            return leanSymbol.ToUpperInvariant() + BrokerSuffix;
        }
    }
}
