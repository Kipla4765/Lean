# MT5 Live Trading Guide (QuantConnect Lean)

This guide explains how to run Lean algorithms live or in paper-trading mode using the MetaTrader 5 (MT5) integration.

## Prerequisites

1.  **Wine & MT5**: Ensure MT5 is installed in a Wine prefix (default: `~/.mt5`).
2.  **Wine Python**: Install Python 3.11 inside the same Wine prefix.
3.  **Python Packages**: Install the `MetaTrader5` package in Wine Python:
    ```bash
    WINEPREFIX=~/.mt5 wine python -m pip install MetaTrader5
    ```

## 1. Configuration

### Credentials
The bridge loads credentials from `~/.mt5/fxpesa.env`. Format:
```env
MT5_LOGIN=your_login_id
MT5_PASSWORD=your_password
MT5_SERVER=your_broker_server
```

### Commissions
Brokerage costs are configured in `Common/Brokerages/Mt5FeeModel.cs`. The current setup is:
- **Flat Fee**: $7.00 per side (per order filled).

## 2. Start the MT5 Bridge

The bridge acts as the translator between Lean (C#) and the MT5 Terminal (Python/Wine).

```bash
cd Brokerages/Mt5/Bridge
bash start_bridge.sh
```
*Note: This script will automatically launch the MT5 Terminal in Wine if it's not already running.*

## 3. Running Your Strategy

### Mode A: Live Trading (Real/Demo Execution)
In this mode, trades are sent directly to your MT5 account.

1.  Open `config.json`.
2.  Set the environment to `live-mt5`:
    ```json
    "environment": "live-mt5"
    ```
3.  Ensure the `algorithm-type-name` points to your class (e.g., `QuantConnect.Algorithm.CSharp.Mt5IndicatorTestAlgorithm`).
4.  Run Lean:
    ```bash
    dotnet run --project Launcher/QuantConnect.Lean.Launcher.csproj
    ```

### Mode B: Paper Trading (Lean Simulation + Live MT5 Data)
Use this to test strategy logic with live price action without risking capital. Lean will simulate fills locally but use the real-time tick stream from MT5.

1.  Open `config.json`.
2.  Set the environment to `live-paper`.
3.  In the `live-paper` section, update `data-queue-handler` to use MT5:
    ```json
    "live-paper": {
      "live-mode": true,
      "live-mode-brokerage": "PaperBrokerage",
      "data-queue-handler": [ "Mt5Brokerage" ],
      "brokerage-data": {
        "mt5-host": "127.0.0.1",
        "mt5-port": "5555"
      },
      ...
    }
    ```
4.  Run Lean.

### Mode C: Backtesting
Traditional historical simulation.

1.  Set `environment` to `backtesting` in `config.json`.
2.  (Optional) The MT5 integration also supports historical data downloads if the bridge is running.

## 4. Troubleshooting

- **Bridge not responding**: Check `/tmp/mt5_bridge.log` for Python errors.
- **Symbol mismatch**: MT5 symbols often require a suffix (e.g., `.p` for FX Pesa). The `Mt5SymbolMapper.cs` handles this conversion.
- **Connection Refused**: Ensure the bridge is running on the port specified in `config.json` (default 5555).
- **Wine Prefix**: If your MT5 is installed elsewhere, update the `WINEPREFIX` in `Brokerages/Mt5/Bridge/start_bridge.sh`.
