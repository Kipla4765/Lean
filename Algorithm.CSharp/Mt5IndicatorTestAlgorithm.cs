using QuantConnect.Data;
using QuantConnect.Indicators;
using QuantConnect.Brokerages;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp
{
    public class Mt5IndicatorTestAlgorithm : QCAlgorithm
    {
        private Symbol _eurusd;
        private RelativeStrengthIndex _rsi;
        private ExponentialMovingAverage _ema;

        public override void Initialize()
        {
            SetStartDate(2023, 1, 1);
            SetCash(10000);

            // MT5 Brokerage Setup
            SetBrokerageModel(BrokerageName.Mt5);
            
            _eurusd = AddForex("EURUSD", Resolution.Minute, Market.FXCM).Symbol;

            // Indicators
            _rsi = RSI(_eurusd, 14, MovingAverageType.Exponential, Resolution.Minute);
            _ema = EMA(_eurusd, 20, Resolution.Minute);

            // Warm up
            SetWarmUp(20);
        }

        public override void OnData(Slice data)
        {
            if (IsWarmingUp) return;
            if (!_rsi.IsReady || !_ema.IsReady) return;

            var holdings = Portfolio[_eurusd].Quantity;

            // Simple RSI + EMA strategy
            // Buy if RSI < 30 (oversold) and Price > EMA
            if (holdings <= 0 && _rsi < 30 && data[_eurusd].Price > _ema)
            {
                MarketOrder(_eurusd, 10000);
            }
            // Sell if RSI > 70 (overbought) or Price < EMA
            else if (holdings > 0 && (_rsi > 70 || data[_eurusd].Price < _ema))
            {
                Liquidate(_eurusd);
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled)
            {
                Debug($"Order Filled: {orderEvent.Symbol} at {orderEvent.FillPrice}. Commission: {orderEvent.OrderFee}");
            }
        }
    }
}
