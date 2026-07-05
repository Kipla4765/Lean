using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Mt5
{
    [BrokerageFactory(typeof(Mt5BrokerageFactory))]
    public class Mt5Brokerage : Brokerage, IDataQueueHandler
    {
        private readonly string _host;
        private readonly int _port;
        private readonly Mt5SymbolMapper _symbolMapper;

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private StreamReader _reader;
        private Thread _fillPollThread;
        private volatile bool _isConnected;
        private volatile bool _stopping;

        private readonly object _lock = new();

        private readonly ConcurrentDictionary<int, Order> _pendingOrders = new();
        private readonly ConcurrentDictionary<Symbol, SubscriptionDataConfig> _subscriptions = new();
        private readonly ConcurrentDictionary<Symbol, ConcurrentQueue<Tick>> _ticks = new();
        private CancellationTokenSource _tickCts;
        private Thread _tickPollerThread;

        public Mt5Brokerage(string host, int port, Mt5SymbolMapper symbolMapper = null)
            : base("MT5 Brokerage")
        {
            _host = host;
            _port = port;
            _symbolMapper = symbolMapper ?? new Mt5SymbolMapper();
        }

        public override bool IsConnected => _isConnected;

        public override void Connect()
        {
            lock (_lock)
            {
                if (_isConnected) return;

                try
                {
                    _tcpClient = new TcpClient();
                    _tcpClient.Connect(_host, _port);
                    _stream = _tcpClient.GetStream();
                    _reader = new StreamReader(_stream, Encoding.UTF8);
                    _isConnected = true;
                    _stopping = false;

                    Log.Trace("Mt5Brokerage.Connect(): Connected to bridge at {0}:{1}", _host, _port);

                    var status = SendCommand(JObject.FromObject(new { cmd = "status" }));
                    if (status?["connected"]?.Value<bool>() == true)
                    {
                        Log.Trace("Mt5Brokerage.Connect(): Bridge verified: {0}", status["terminal"]?.Value<string>());
                    }

                    _fillPollThread = new Thread(FillPollLoop) { IsBackground = true, Name = "Mt5FillPoll" };
                    _fillPollThread.Start();
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    Log.Error("Mt5Brokerage.Connect(): {0}", ex.Message);
                    throw;
                }
            }
        }

        public override void Disconnect()
        {
            _stopping = true;
            _isConnected = false;
            _tickCts?.Cancel();

            lock (_lock)
            {
                _reader?.Close();
                _stream?.Close();
                _tcpClient?.Close();
                _reader = null;
                _stream = null;
                _tcpClient = null;
            }

            Log.Trace("Mt5Brokerage.Disconnect()");
        }

        public override bool PlaceOrder(Order order)
        {
            if (!_isConnected)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Not connected")
                    { Status = OrderStatus.Invalid });
                return false;
            }

            try
            {
                var brokerSymbol = _symbolMapper.GetBrokerageSymbol(order.Symbol);
                var side = order.Direction == OrderDirection.Buy ? "BUY" : "SELL";
                var volume = NormalizeVolume(Math.Abs(order.Quantity));

                var cmd = new JObject
                {
                    ["cmd"] = "order",
                    ["symbol"] = brokerSymbol,
                    ["side"] = side,
                    ["volume"] = volume,
                };

                if (order.Type == OrderType.Limit && order is LimitOrder limit)
                    cmd["price"] = limit.LimitPrice;
                else if (order.Type == OrderType.StopMarket && order is StopMarketOrder stop)
                    cmd["price"] = stop.StopPrice;

                var result = SendCommand(cmd);
                var status = result?["status"]?.Value<string>();
                if (status == "filled" || status == "placed")
                {
                    var brokerOrderId = result["order_id"]?.Value<long>() ?? 0;
                    order.BrokerId.Add(brokerOrderId.ToString(CultureInfo.InvariantCulture));

                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
                        { Status = status == "filled" ? OrderStatus.Filled : OrderStatus.Submitted });

                    if (status == "placed")
                    {
                        _pendingOrders[order.Id] = order;
                    }

                    Log.Trace("Mt5Brokerage.PlaceOrder(): {0} {1} {2} {3} at {4}",
                        side, volume, brokerSymbol, status, result["price"]);
                    return true;
                }

                var error = result?["error"]?.Value<string>() ?? "Unknown error";
                Log.Error("Mt5Brokerage.PlaceOrder(): {0}", error);
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, error)
                    { Status = OrderStatus.Invalid });
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Mt5Brokerage.PlaceOrder(): {0}", ex.Message);
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, ex.Message)
                    { Status = OrderStatus.Invalid });
                return false;
            }
        }

        public override bool UpdateOrder(Order order)
        {
            if (!_isConnected) return false;

            try
            {
                var ticket = long.Parse(order.BrokerId.First(), CultureInfo.InvariantCulture);
                var cmd = new JObject
                {
                    ["cmd"] = "modify_order",
                    ["ticket"] = ticket,
                };

                if (order is LimitOrder limit) cmd["price"] = limit.LimitPrice;
                else if (order is StopMarketOrder stop) cmd["price"] = stop.StopPrice;

                var result = SendCommand(cmd);
                if (result?["status"]?.Value<string>() == "modified")
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.UpdateSubmitted });
                    return true;
                }

                Log.Error("Mt5Brokerage.UpdateOrder(): {0}", result?["error"]?.Value<string>() ?? "Unknown error");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Mt5Brokerage.UpdateOrder(): {0}", ex.Message);
                return false;
            }
        }

        public override bool CancelOrder(Order order)
        {
            if (!_isConnected) return false;

            try
            {
                var ticket = long.Parse(order.BrokerId.First(), CultureInfo.InvariantCulture);
                var cmd = new JObject
                {
                    ["cmd"] = "cancel_order",
                    ["ticket"] = ticket,
                };

                var result = SendCommand(cmd);
                if (result?["status"]?.Value<string>() == "cancelled")
                {
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero) { Status = OrderStatus.Canceled });
                    return true;
                }

                Log.Error("Mt5Brokerage.CancelOrder(): {0}", result?["error"]?.Value<string>() ?? "Unknown error");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Mt5Brokerage.CancelOrder(): {0}", ex.Message);
                return false;
            }
        }

        public override List<Order> GetOpenOrders()
        {
            var orders = new List<Order>();
            try
            {
                var result = SendCommand(JObject.FromObject(new { cmd = "orders" }));
                if (result is JArray array)
                {
                    foreach (var item in array)
                    {
                        var brokerSymbol = item["symbol"]?.Value<string>();
                        var symbol = _symbolMapper.GetLeanSymbol(brokerSymbol, SecurityType.Forex, Market.FXCM);
                        var quantity = item["volume"]?.Value<decimal>() ?? 0;
                        var price = item["price"]?.Value<decimal>() ?? 0;
                        var ticket = item["ticket"]?.Value<long>() ?? 0;
                        var side = item["side"]?.Value<string>();

                        Order order = null;
                        // MT5 order types: 0=BUY, 1=SELL, 2=BUY_LIMIT, 3=SELL_LIMIT, 4=BUY_STOP, 5=SELL_STOP
                        if (side == "2" || side == "3")
                            order = new LimitOrder(symbol, side == "2" ? quantity : -quantity, price, DateTime.UtcNow);
                        else if (side == "4" || side == "5")
                            order = new StopMarketOrder(symbol, side == "4" ? quantity : -quantity, price, DateTime.UtcNow);

                        if (order != null)
                        {
                            order.BrokerId.Add(ticket.ToString(CultureInfo.InvariantCulture));
                            orders.Add(order);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mt5Brokerage.GetOpenOrders(): {0}", ex.Message);
            }
            return orders;
        }

        public override List<Holding> GetAccountHoldings()
        {
            var holdings = new List<Holding>();
            try
            {
                var result = SendCommand(JObject.FromObject(new { cmd = "positions" }));
                if (result is JArray array)
                {
                    foreach (var item in array)
                    {
                        var brokerSymbol = item["symbol"]?.Value<string>();
                        var symbol = _symbolMapper.GetLeanSymbol(brokerSymbol, SecurityType.Forex, Market.FXCM);
                        var quantity = item["volume"]?.Value<decimal>() ?? 0;
                        var price = item["price"]?.Value<decimal>() ?? 0;
                        var side = item["side"]?.Value<string>();

                        holdings.Add(new Holding
                        {
                            Symbol = symbol,
                            AveragePrice = price,
                            Quantity = side == "BUY" ? quantity : -quantity,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mt5Brokerage.GetAccountHoldings(): {0}", ex.Message);
            }
            return holdings;
        }

        public override List<CashAmount> GetCashBalance()
        {
            try
            {
                var result = SendCommand(JObject.FromObject(new { cmd = "account" }));
                if (result != null && result["balance"] != null)
                {
                    var balance = result["balance"].Value<decimal>();
                    var currency = result["currency"]?.Value<string>() ?? "USD";
                    return new List<CashAmount> { new(balance, currency) };
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mt5Brokerage.GetCashBalance(): {0}", ex.Message);
            }

            return new List<CashAmount>();
        }

        public override IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (!_isConnected) yield break;

            var brokerSymbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);
            var timeframe = "1h";
            if (request.Resolution == Resolution.Minute) timeframe = "1m";
            else if (request.Resolution == Resolution.Hour) timeframe = "1h";
            else if (request.Resolution == Resolution.Daily) timeframe = "1d";

            var cmd = new JObject
            {
                ["cmd"] = "history",
                ["symbol"] = brokerSymbol,
                ["timeframe"] = timeframe,
                ["count"] = 1000, // Reasonable default for history requests
            };

            var result = SendCommand(cmd);
            if (result is JArray array)
            {
                foreach (var item in array)
                {
                    var time = item["time"]?.Value<long>() ?? 0;
                    var open = item["open"]?.Value<decimal>() ?? 0;
                    var high = item["high"]?.Value<decimal>() ?? 0;
                    var low = item["low"]?.Value<decimal>() ?? 0;
                    var close = item["close"]?.Value<decimal>() ?? 0;
                    var volume = item["tick_volume"]?.Value<decimal>() ?? 0;

                    yield return new TradeBar(
                        DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime,
                        request.Symbol, open, high, low, close, volume, request.Resolution.ToTimeSpan());
                }
            }
        }

        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            var symbol = dataConfig.Symbol;
            _subscriptions[symbol] = dataConfig;
            _ticks.TryAdd(symbol, new ConcurrentQueue<Tick>());

            Log.Trace("Mt5Brokerage.Subscribe(): {0}", symbol);

            if (_tickCts == null)
            {
                _tickCts = new CancellationTokenSource();
                _tickPollerThread = new Thread(TickPollLoop) { IsBackground = true, Name = "Mt5TickPoll" };
                _tickPollerThread.Start();
            }

            return new TickEnumerator(this, symbol, _tickCts.Token);
        }

        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptions.TryRemove(dataConfig.Symbol, out _);
            _ticks.TryRemove(dataConfig.Symbol, out _);
            Log.Trace("Mt5Brokerage.Unsubscribe(): {0}", dataConfig.Symbol);

            if (_subscriptions.IsEmpty && _tickCts != null)
            {
                _tickCts.Cancel();
                _tickCts = null;
            }
        }

        public void SetJob(LiveNodePacket job) { }

        private void FillPollLoop()
        {
            while (!_stopping && _isConnected)
            {
                try
                {
                    CheckForFills();
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    if (!_stopping) Log.Error("Mt5Brokerage.FillPollLoop(): {0}", ex.Message);
                }
            }
        }

        private void CheckForFills()
        {
            if (_pendingOrders.IsEmpty) return;

            try
            {
                // Get all open orders to see what's still pending
                var openOrdersResult = SendCommand(JObject.FromObject(new { cmd = "orders" }));
                var openTicketIds = new HashSet<string>();
                if (openOrdersResult is JArray ordersArray)
                {
                    foreach (var item in ordersArray)
                    {
                        var ticket = item["ticket"]?.Value<long>().ToString(CultureInfo.InvariantCulture);
                        if (ticket != null) openTicketIds.Add(ticket);
                    }
                }

                foreach (var kvp in _pendingOrders)
                {
                    var order = kvp.Value;
                    var ticket = order.BrokerId.FirstOrDefault();
                    if (ticket == null) continue;

                    if (!openTicketIds.Contains(ticket))
                    {
                        // Order is no longer in open orders, check if it was filled (became a position)
                        var brokerSymbol = _symbolMapper.GetBrokerageSymbol(order.Symbol);
                        var posResult = SendCommand(JObject.FromObject(new { cmd = "positions", symbol = brokerSymbol }));
                        
                        bool filled = false;
                        if (posResult is JArray posArray)
                        {
                            foreach (var p in posArray)
                            {
                                // In MT5, multiple positions can exist for same symbol if hedging is enabled, 
                                // but usually we look for one that matches our trade.
                                // Simplification: if any position exists and our order is gone, we assume fill for now.
                                // A better way would be to check historical deals.
                                if (_pendingOrders.TryRemove(order.Id, out _))
                                {
                                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
                                    {
                                        Status = OrderStatus.Filled,
                                        FillPrice = p["price"]?.Value<decimal>() ?? 0,
                                        FillQuantity = order.Direction == OrderDirection.Buy 
                                            ? p["volume"]?.Value<decimal>() ?? 0 
                                            : -(p["volume"]?.Value<decimal>() ?? 0),
                                    });
                                    filled = true;
                                    break;
                                }
                            }
                        }
                        
                        if (!filled)
                        {
                            // Could have been cancelled or expired
                            // For now, if it's gone from orders and not in positions, we'll wait or mark as cancelled if we had a way to verify.
                            // To be safe, we'll keep it in pending until we are sure.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mt5Brokerage.CheckForFills(): {0}", ex.Message);
            }
        }

        private void TickPollLoop()
        {
            var token = _tickCts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var symbol in _subscriptions.Keys)
                    {
                        var tick = QueryTick(symbol);
                        if (tick != null)
                        {
                            var queue = _ticks.GetOrAdd(symbol, _ => new ConcurrentQueue<Tick>());
                            queue.Enqueue(tick);
                        }
                    }
                    Thread.Sleep(50);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        Log.Error("Mt5Brokerage.TickPollLoop(): {0}", ex.Message);
                }
            }
        }

        public Tick QueryTick(Symbol symbol)
        {
            if (!_isConnected) return null;

            try
            {
                var brokerSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                var cmd = new JObject { ["cmd"] = "tick", ["symbol"] = brokerSymbol };
                var result = SendCommand(cmd);
                if (result?["bid"] != null)
                {
                    var time = result["time"]?.Value<long>() ?? 0;
                    var dateTime = time > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime
                        : DateTime.UtcNow;
                    var bid = result["bid"].Value<decimal>();
                    var ask = result["ask"].Value<decimal>();
                    return new Tick(dateTime, symbol, bid, ask);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Mt5Brokerage.QueryTick(): {0}", ex.Message);
            }

            return null;
        }

        private JObject SendCommand(JObject command)
        {
            lock (_lock)
            {
                try
                {
                    if (_stream == null || !_tcpClient.Connected)
                    {
                        _isConnected = false;
                        return null;
                    }

                    var json = command.ToString(Formatting.None) + "\n";
                    var data = Encoding.UTF8.GetBytes(json);
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();

                    var response = _reader.ReadLine();
                    if (response != null)
                    {
                        return JObject.Parse(response);
                    }

                    _isConnected = false;
                    return null;
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    Log.Error("Mt5Brokerage.SendCommand(): {0}", ex.Message);
                    return null;
                }
            }
        }

        private static decimal NormalizeVolume(decimal volume)
        {
            return Math.Round(volume / 0.01m) * 0.01m;
        }

        private class TickEnumerator : IEnumerator<BaseData>
        {
            private readonly Mt5Brokerage _brokerage;
            private readonly Symbol _symbol;
            private readonly CancellationToken _token;
            private BaseData _current;

            public TickEnumerator(Mt5Brokerage brokerage, Symbol symbol, CancellationToken token)
            {
                _brokerage = brokerage;
                _symbol = symbol;
                _token = token;
            }

            public BaseData Current => _current;
            object System.Collections.IEnumerator.Current => _current;

            public bool MoveNext()
            {
                _current = null;
                while (!_token.IsCancellationRequested)
                {
                    if (_brokerage._ticks.TryGetValue(_symbol, out var queue) && queue.TryDequeue(out var tick))
                    {
                        _current = tick;
                        return true;
                    }
                    Thread.Sleep(10);
                }
                return false;
            }

            public void Reset() { }
            public void Dispose() { }
        }
    }
}
