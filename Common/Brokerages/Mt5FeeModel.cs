using System;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages
{
    public class Mt5FeeModel : FeeModel
    {
        private const decimal FlatFeePerOrder = 7.0m;

        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            var security = parameters.Security;
            return new OrderFee(new CashAmount(FlatFeePerOrder, security.QuoteCurrency.Symbol));
        }
    }
}
