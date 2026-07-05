# MT5 Brokerage for QuantConnect Lean

## What Was Done

This directory implements a **MetaTrader 5 brokerage integration** for QuantConnect Lean. It connects to a Python TCP bridge (`mt5_bridge_fast.py`) that runs inside Wine and communicates with the MT5 terminal.

### Improvements (Completed June 2026):
- [x] **Real account balance** — Implemented `GetCashBalance()` via `cmd: account`.
- [x] **Real holdings** — Implemented `GetAccountHoldings()` via `cmd: positions`.
- [x] **Open orders** — Implemented `GetOpenOrders()` via `cmd: orders`.
- [x] **Order updates/cancels** — Implemented `UpdateOrder` and `CancelOrder` via `cmd: modify_order` and `cmd: cancel_order`.
- [x] **History** — Implemented `GetHistory()` via `cmd: history`.
- [x] **Live Data** — Implemented functional tick streaming via `TickPollLoop` and buffered `TickEnumerator`.
- [x] **Pending Orders** — Added support for Limit and Stop orders.

## Architecture

```
┌──────────────────────┐     TCP/JSON     ┌──────────────────────┐     MT5 API    ┌──────────┐
│  Lean (C#)           │ ◄──────────────► │  mt5_bridge_fast.py  │ ◄─────────────►│  MT5     │
│  Mt5Brokerage        │   cmd: order     │  (Wine Python)       │                │ Terminal │
│  (IDataQueueHandler)  │   cmd: tick      │  port 5555           │                │          │
│  Mt5BrokerageFactory  │   cmd: position  │                      │                │          │
│  Mt5BrokerageModel    │   cmd: status    │                      │                │          │
│  Mt5FeeModel          │                  │                      │                │          │
│  Mt5SymbolMapper      │                  │                      │                │          │
└──────────────────────┘                  └──────────────────────┘                └──────────┘
```

## Files

### `Brokerages/Mt5/` (QuantConnect.Brokerages project)
| File | Purpose |
|------|---------|
| `Mt5Brokerage.cs` | Core brokerage + live data handler. TCP client to the bridge. Implements `IBrokerage` and `IDataQueueHandler`. |
| `Mt5BrokerageFactory.cs` | MEF-discovered factory. |
| `Mt5SymbolMapper.cs` | Maps Lean `EURUSD` → bridge `EURUSD.p`. |
| `Bridge/mt5_bridge_fast.py` | Python TCP bridge server. Runs in Wine Python. |
| `Bridge/start_bridge.sh` | Starts the bridge in Wine Python. |

### `Common/Brokerages/` (QuantConnect.Common project)
| File | Purpose |
|------|---------|
| `Mt5BrokerageModel.cs` | Brokerage rules: max leverage 50x forex, fee model, etc. |
| `Mt5FeeModel.cs` | Commission calc: $10 per million USD traded. |

## How to Configure

### 1. Start the Bridge
```bash
cd Lean/Brokerages/Mt5/Bridge
bash start_bridge.sh
```

**Note on Python environment:**
The `start_bridge.sh` script looks for `Python311` in the Wine prefix. If you are using a virtual environment (venv) inside Wine, update the `PYTHON_EXE` variable in `start_bridge.sh` to point to your venv's `python.exe`.

### 2. Configure Live Trading
In your Lean configuration (`config.json`):
```json
{
    "brokerage": "Mt5Brokerage",
    "data-queue-handler": "Mt5Brokerage",
    "brokerage-data": {
        "mt5-host": "127.0.0.1",
        "mt5-port": "5555"
    }
}
```

## Protocol (JSON-over-TCP)

The bridge speaks newline-delimited JSON. Commands:

| Command | Purpose | Response |
|---------|---------|----------|
| `{"cmd":"status"}` | Connection check | `{"connected":true, "terminal":"..."}` |
| `{"cmd":"account"}` | Get account info | `{"balance":10000, ...}` |
| `{"cmd":"tick","symbol":"EURUSD.p"}` | Get latest tick | `{"symbol":"EURUSD.p","bid":1.05,"ask":1.0501,...}` |
| `{"cmd":"order",...}` | Place order | `{"status":"filled/placed","order_id":123,...}` |
| `{"cmd":"cancel_order","ticket":123}` | Cancel order | `{"status":"cancelled","ticket":123}` |
| `{"cmd":"positions"}` | Get all positions | `[...]` |
| `{"cmd":"orders"}` | Get all open orders | `[...]` |
| `{"cmd":"history",...}` | Get OHLC history | `[...]` |

## Future Improvements
- [ ] **Batch ticks** — Current poll queries symbols one-by-one. Batch would be faster.
- [ ] **Error handling** — Robust retry logic on TCP disconnect.
- [ ] **Wine watchdog** — Automatic restart of bridge if it crashes.
