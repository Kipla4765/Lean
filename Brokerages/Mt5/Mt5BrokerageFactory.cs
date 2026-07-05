using System.Collections.Generic;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Mt5
{
    public class Mt5BrokerageFactory : BrokerageFactory
    {
        public Mt5BrokerageFactory()
            : base(typeof(Mt5Brokerage))
        {
        }

        public override Dictionary<string, string> BrokerageData => new()
        {
            { "mt5-host", "127.0.0.1" },
            { "mt5-port", "5555" },
        };

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var host = Read<string>(job.BrokerageData, "mt5-host", new List<string>());
            var port = Read<int>(job.BrokerageData, "mt5-port", new List<string>());

            if (string.IsNullOrEmpty(host)) host = "127.0.0.1";
            if (port == 0) port = 5555;

            var mapper = new Mt5SymbolMapper();
            var brokerage = new Mt5Brokerage(host, port, mapper);
            return brokerage;
        }

        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider)
        {
            return new Mt5BrokerageModel();
        }

        public override void Dispose()
        {
        }
    }
}
