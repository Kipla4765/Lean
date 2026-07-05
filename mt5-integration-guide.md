# MT5 Brokerage Integration Guide for QuantConnect Lean

## The Big Picture

Lean has a modular architecture where brokerages plug in via **6 components** that work together. When you backtest, it uses simulation models. When you go live, the real brokerage API is called. Your MT5 integration replaces the "live" layer while keeping the backtesting/paper trading system unchanged.

---

## Component 1: The Brokerage (`IBrokerage`)

**This is the raw connection to MT5.** It handles order placement, account data, and connectivity.

You create `Mt5Brokerage : Brokerage` (extends `Brokerages/Brokerage.cs`) and implement these **abstract methods**:

```csharp
// ORDER OPERATIONS
public override bool PlaceOrder(Order order)   // send order to MT5 terminal
public override bool UpdateOrder(Order order)  // modify existing order
public override bool CancelOrder(Order order)  // cancel order

// CONNECTION
public override void Connect()                 // connect to MT5 via API (e.g. MetaTrader5 Python bridge or TCP)
public override void Disconnect()              // disconnect
public override bool IsConnected => ...

// ACCOUNT DATA
public override List<Order> GetOpenOrders()       // fetch open orders from MT5
public override List<Holding> GetAccountHoldings() // fetch positions from MT5
public override List<CashAmount> GetCashBalance()  // fetch cash balances from MT5
```

**How you fire order fills back to Lean:** When MT5 sends an order fill/update, you create `OrderEvent` objects and call:

```csharp
protected virtual void OnOrderEvents(List<OrderEvent> orderEvents);  // fire fills
protected virtual void OnMessage(BrokerageMessageEvent e);           // log errors/warnings
```

**Concrete example** — `BacktestingBrokerage.PlaceOrder()` (`Brokerages/Backtesting/BacktestingBrokerage.cs:119`):

```csharp
public override bool PlaceOrder(Order order)
{
    if (order.Status == OrderStatus.New)
    {
        lock (_needsScanLock) { _needsScan = true; SetPendingOrder(order); }
        AddBrokerageOrderId(order);
        var submitted = new OrderEvent(order, Algorithm.UtcTime, OrderFee.Zero)
            { Status = OrderStatus.Submitted };
        OnOrderEvent(submitted);
        return true;
    }
    return false;
}
```

For **live MT5**, yours would send via MT5 API instead of queueing:

```csharp
public override bool PlaceOrder(Order order)
{
    var mt5Symbol = _symbolMapper.GetBrokerageSymbol(order.Symbol);
    var result = _mt5Api.OrderSend(mt5Symbol, order.Direction, order.Quantity, ...);
    if (result != null)
    {
        order.BrokerId.Add(result.OrderId.ToString());
        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero)
            { Status = OrderStatus.Submitted });
        return true;
    }
    return false;
}
```

**How MT5 fills come back** — you poll or use WebSocket/trade events from MT5, then fire:

```csharp
var fill = new OrderEvent(order, DateTime.UtcNow, myFeeModel)
{
    Status = OrderStatus.Filled,
    FillPrice = fillPrice,
    FillQuantity = fillQuantity
};
OnOrderEvents(new List<OrderEvent> { fill });
```

---

## Component 2: The Brokerage Model (`IBrokerageModel`)

**This defines rules and pluggable models** — fees, fills, slippage, leverage, buying power, order validation. It is used both in **backtesting and live** so your backtests properly reflect MT5's rules.

You create `Mt5BrokerageModel : DefaultBrokerageModel` (extends `Common/Brokerages/DefaultBrokerageModel.cs`):

```csharp
public class Mt5BrokerageModel : DefaultBrokerageModel
{
    public override IFeeModel GetFeeModel(Security security)
    {
        return security.Type switch
        {
            SecurityType.Forex => new Mt5ForexFeeModel(),
            SecurityType.Equity => new Mt5EquityFeeModel(),
            _ => new ConstantFeeModel(0m)
        };
    }

    public override decimal GetLeverage(Security security) => 50m; // forex
    public override IBuyingPowerModel GetBuyingPowerModel(Security security) => ...;
    public override IFillModel GetFillModel(Security security) => new ImmediateFillModel();

    public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
    {
        // Validate MT5-specific rules: min/max lot sizes, allowed order types, etc.
        message = null;
        return true;
    }
}
```

**See `DefaultBrokerageModel`** at `Common/Brokerages/DefaultBrokerageModel.cs:39` — it has sensible defaults. You override what's different for MT5.

---

## Component 3: The Fee Model (`IFeeModel`)

**Defines commission per trade.** Used in backtesting AND live.

```csharp
// Common/Orders/Fees/FeeModel.cs (base — returns 0 fee)
public class FeeModel : IFeeModel
{
    public virtual OrderFee GetOrderFee(OrderFeeParameters parameters) { ... }
}
```

You create `Mt5FeeModel : FeeModel`:

```csharp
public class Mt5FeeModel : FeeModel
{
    public override OrderFee GetOrderFee(OrderFeeParameters parameters)
    {
        var order = parameters.Order;
        var security = parameters.Security;
        var value = Math.Abs(order.GetValue(security));
        var fee = value * 0.001m; // 0.1% commission
        return new OrderFee(new CashAmount(fee, security.QuoteCurrency.Symbol));
    }
}
```

**See `InteractiveBrokersFeeModel`** at `Common/Orders/Fees/InteractiveBrokersFeeModel.cs:26` for a full reference with per-asset-class fee logic.

---

## Component 4: The Symbol Mapper (`ISymbolMapper`)

**Maps between Lean symbols (e.g. `EURUSD`, `AAPL`) and MT5 symbols (e.g. `EURUSD`, `AAPL`).**

```csharp
// Brokerages/ISymbolMapper.cs
public interface ISymbolMapper
{
    string GetBrokerageSymbol(Symbol symbol);            // Lean -> MT5
    Symbol GetLeanSymbol(string brokerageSymbol, ...);   // MT5 -> Lean
}
```

For MT5, this might be a simple 1:1 mapping for forex (EURUSD ↔ EURUSD) but could differ for CFDs/stocks/indices.

---

## Component 5: The Data Queue Handler (`IDataQueueHandler`)

**Provides live market data from MT5.** Needed for live trading only.

```csharp
// Common/Interfaces/IDataQueueHandler.cs
public interface IDataQueueHandler : IDisposable
{
    IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler);
    void Unsubscribe(SubscriptionDataConfig dataConfig);
    void SetJob(LiveNodePacket job);
    bool IsConnected { get; }
}
```

Your `Mt5DataQueueHandler` subscribes to MT5's tick/bar data and returns `IEnumerator<BaseData>`:

```csharp
public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig config, EventHandler handler)
{
    var mt5Symbol = _symbolMapper.GetBrokerageSymbol(config.Symbol);
    _mt5Api.SubscribeToQuotes(mt5Symbol);
    return _quoteQueues.GetOrAdd(config.Symbol, _ => new LiveEnumerator(config, handler));
}
```

---

## Component 6: The Factory (`IBrokerageFactory`)

**Wires everything together for live mode.** Discovered via MEF (`[InheritedExport]`).

```csharp
[BrokerageFactory(typeof(Mt5BrokerageFactory))]
public class Mt5Brokerage : Brokerage { ... }

public class Mt5BrokerageFactory : BrokerageFactory
{
    public Mt5BrokerageFactory() : base(typeof(Mt5Brokerage)) { }

    public override Dictionary<string, string> BrokerageData => new()
    {
        { "mt5-server", "127.0.0.1" },
        { "mt5-port", "15555" },
        { "mt5-login", "12345" }
    };

    public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
    {
        return new Mt5Brokerage(_symbolMapper, job.BrokerageData["mt5-server"], ...);
    }

    public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider)
        => new Mt5BrokerageModel();
}
```

**See `BacktestingBrokerageFactory`** at `Brokerages/Backtesting/BacktestingBrokerageFactory.cs:26`.

---

## Registration (Two Places)

1. **`BrokerageName` enum** (`Common/Brokerages/BrokerageName.cs:207`) — add your `Mt5` entry
2. **`BrokerageModel.Create()`** switch statement (`Common/Brokerages/IBrokerageModel.cs:193`) — map `BrokerageName.Mt5` to `new Mt5BrokerageModel()`

The factory is auto-discovered via MEF at runtime (Composer.Instance), so no separate registration needed — just drop the DLL.

---

## How Backtesting, Paper, Live All Work With Your MT5

```
                    ┌──────────────────────────────────────┐
                    │         Your Algorithm               │
                    │  (QCAlgorithm — same code, all modes)│
                    └──────────┬───────────────────────────┘
                               │ orders
                               ▼
                    ┌──────────────────────┐
                    │ TransactionHandler   │
                    │ (BrokerageTransaction│
                    │  Handler)            │
                    └──────┬───────────────┘
                           │ calls brokerage.PlaceOrder()
                           ▼
              ┌────────────────────────────────────────────┐
              │             BACKTEST MODE                  │
              │  BacktestingBrokerage                      │
              │  • Queues order in _pending                │
              │  • Scan() applies FillModel                │
              │  • No MT5 API called                       │
              │  • Uses Mt5BrokerageModel                  │
              │    for fees, leverage, validation          │
              └────────────────────────────────────────────┘

              ┌────────────────────────────────────────────┐
              │             PAPER MODE                     │
              │  PaperBrokerage                            │
              │  • Extends BacktestingBrokerage            │
              │  • Same as backtest but for live-like env  │
              │  • No real money, simulates fills          │
              └────────────────────────────────────────────┘

              ┌────────────────────────────────────────────┐
              │             LIVE MODE                      │
              │  Your Mt5Brokerage                         │
              │  • Calls MT5 API directly                  │
              │    (MetaTrader5 Python bridge, TCP, etc.)  │
              │  • PlaceOrder() → MT5 terminal/server      │
              │  • OnOrderEvents() ← MT5 fills/updates     │
              │  • GetCashBalance() → MT5 account info     │
              │  • Uses Mt5DataQueueHandler                │
              │    for live price data (quotes/bars)       │
              └────────────────────────────────────────────┘
```

**Key insight:** When backtesting, `BacktestingBrokerage` is used (not your `Mt5Brokerage`). Your `Mt5BrokerageModel` *is* used in backtesting for fees, leverage, and order validation. When paper trading, `PaperBrokerage` extends `BacktestingBrokerage` and simulates fills. Only in live mode does your `Mt5Brokerage` get created by the factory and `Mt5DataQueueHandler` starts streaming data.

---

## What You Need to Write (Summary)

| File | What it does | Reference in codebase |
|------|-------------|----------------------|
| `Mt5Brokerage.cs` | Connect/order/account via MT5 API | `BacktestingBrokerage` → `Brokerages/Backtesting/BacktestingBrokerage.cs:38` |
| `Mt5BrokerageFactory.cs` | Creates Mt5Brokerage, returns Mt5BrokerageModel | `BacktestingBrokerageFactory` → `Brokerages/Backtesting/BacktestingBrokerageFactory.cs:26` |
| `Mt5BrokerageModel.cs` | Fee model, fill model, leverage, order validation | `DefaultBrokerageModel` → `Common/Brokerages/DefaultBrokerageModel.cs:39` |
| `Mt5FeeModel.cs` | Commission calculation per trade | `InteractiveBrokersFeeModel` → `Common/Orders/Fees/InteractiveBrokersFeeModel.cs:26` |
| `Mt5SymbolMapper.cs` | Maps Lean ↔ MT5 symbol names | `ISymbolMapper` → `Brokerages/ISymbolMapper.cs` |
| `Mt5DataQueueHandler.cs` | Live price feed from MT5 | `IDataQueueHandler` → `Common/Interfaces/IDataQueueHandler.cs` |
| `BrokerageName.cs` | Add `Mt5` enum value | `Common/Brokerages/BrokerageName.cs:207` |
| `IBrokerageModel.cs` | Add switch case for `Mt5` | `Common/Brokerages/IBrokerageModel.cs:193` |

The actual connection to MT5 via its API (Python package `MetaTrader5`, TCP protocol, terminal DLL bridge, or WebSocket bridge) goes inside `Mt5Brokerage.Connect()/PlaceOrder()` and `Mt5DataQueueHandler.Subscribe()`. The rest is Lean plumbing.
