using System;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Fills;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages
{
    public class Mt5BrokerageModel : DefaultBrokerageModel
    {
        public Mt5BrokerageModel(AccountType accountType = AccountType.Margin)
            : base(accountType)
        {
        }

        public override IFeeModel GetFeeModel(Security security)
        {
            if (security.Type == SecurityType.Forex)
            {
                return new Mt5FeeModel();
            }
            return base.GetFeeModel(security);
        }

        public override decimal GetLeverage(Security security)
        {
            return security.Type switch
            {
                SecurityType.Forex => 50m,
                SecurityType.Cfd => 20m,
                _ => base.GetLeverage(security),
            };
        }

        public override IFillModel GetFillModel(Security security)
        {
            return new ImmediateFillModel();
        }

        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            message = null;

            if (security.Type != SecurityType.Forex && security.Type != SecurityType.Cfd)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedSecurity",
                    $"MT5 does not support {security.Type} securities");
                return false;
            }

            if (order.Price <= 0 && order.Type != OrderType.Market)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidPrice",
                    "Limit/stop orders require a positive price");
                return false;
            }

            return true;
        }

        public override bool CanUpdateOrder(Security security, Order order, UpdateOrderRequest request, out BrokerageMessageEvent message)
        {
            message = null;
            return true;
        }
    }
}
