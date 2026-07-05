"""
MT5 Bridge Server - Fast persistent TCP server for MT5 communication.
Handles multiple commands per connection for maximum speed.
Runs in Wine Python to communicate with MT5 terminal.

Usage in Wine Python:
    wine python.exe mt5_bridge_fast.py --port 5555

Features:
- Persistent connections (multiple commands per connection)
- Real order execution via MT5 API
- Fast tick data and order execution
"""
import sys
import json
import os
import socket
import threading
import time
from typing import Dict, Any

# Load .env from ~/.mt5/fxpesa.env
env_file = os.path.expanduser("~/.mt5/fxpesa.env")
if os.path.exists(env_file):
    with open(env_file) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                os.environ[k.strip()] = v.strip()
else:
    try:
        from dotenv import load_dotenv
        load_dotenv()
    except ImportError:
        pass


def get_mt5():
    """Initialize and return MT5 module."""
    try:
        import MetaTrader5 as mt5
    except ImportError:
        return None, "MetaTrader5 not available"

    # Try simple init first (best for Wine if terminal is already open)
    if mt5.initialize():
        return mt5, None

    # If that fails, try with credentials
    login = int(os.getenv("MT5_LOGIN", "0")) or None
    password = os.getenv("MT5_PASSWORD")
    server = os.getenv("MT5_SERVER", "EGMSecurities-Demo")

    init_args = {}
    if login and password:
        init_args = {"login": login, "password": password, "server": server}

    if mt5.initialize(**init_args):
        return mt5, None

    err = mt5.last_error()
    mt5.shutdown()
    return None, f"MT5 init failed: {err}"



def handle_command(mt5, command: Dict[str, Any]) -> Dict[str, Any]:
    """Process a command and return the result."""
    try:
        cmd = command.get("cmd")
        symbol = command.get("symbol", "")
        timeframe = command.get("timeframe", "1h")
        count = int(command.get("count", 100))

        if cmd == "status":
            if mt5 is None:
                return {"connected": False, "error": "MT5 not available"}
            info = mt5.terminal_info()
            return {"connected": True, "terminal": info.name if info else "Unknown"}

        elif cmd == "account":
            if mt5 is None:
                return {"error": "MT5 not available"}
            acc = mt5.account_info()
            if acc:
                return {
                    "balance": acc.balance,
                    "equity": acc.equity,
                    "currency": acc.currency,
                    "leverage": acc.leverage,
                    "profit": acc.profit,
                }
            return {"error": "Failed to get account info"}

        elif cmd == "tick":
            if mt5 is None:
                return {"error": "MT5 not available"}
            tick = mt5.symbol_info_tick(symbol)
            if tick:
                return {
                    "symbol": symbol,
                    "bid": tick.bid,
                    "ask": tick.ask,
                    "last": tick.last,
                    "volume": tick.volume,
                    "time": tick.time,
                }
            return {"error": "No tick for %s" % symbol}

        elif cmd == "history":
            if mt5 is None:
                return {"error": "MT5 not available"}
            tf_map = {
                "1m": mt5.TIMEFRAME_M1,
                "5m": mt5.TIMEFRAME_M5,
                "15m": mt5.TIMEFRAME_M15,
                "1h": mt5.TIMEFRAME_H1,
                "4h": mt5.TIMEFRAME_H4,
                "1d": mt5.TIMEFRAME_D1,
            }
            tf = tf_map.get(timeframe, mt5.TIMEFRAME_H1)
            rates = mt5.copy_rates_from_pos(symbol, tf, 0, count)
            if rates is not None:
                return [dict(r) for r in rates]
            return {"error": "No history for %s" % symbol}

        elif cmd == "order":
            if mt5 is None:
                return {"error": "MT5 not available"}
            
            symbol = command.get("symbol")
            side = command.get("side", "").upper()
            volume = float(command.get("volume", 0.01))
            price = command.get("price")
            sl = command.get("sl")
            tp = command.get("tp")
            comment = command.get("comment", "")
            type_filling = command.get("type_filling", mt5.ORDER_FILLING_IOC)
            
            # Map side to MT5 order type
            if side == "BUY":
                order_type = mt5.ORDER_TYPE_BUY
                price = mt5.symbol_info_tick(symbol).ask if not price else price
            elif side == "SELL":
                order_type = mt5.ORDER_TYPE_SELL
                price = mt5.symbol_info_tick(symbol).bid if not price else price
            elif side == "BUY_LIMIT":
                order_type = mt5.ORDER_TYPE_BUY_LIMIT
            elif side == "SELL_LIMIT":
                order_type = mt5.ORDER_TYPE_SELL_LIMIT
            elif side == "BUY_STOP":
                order_type = mt5.ORDER_TYPE_BUY_STOP
            elif side == "SELL_STOP":
                order_type = mt5.ORDER_TYPE_SELL_STOP
            else:
                return {"error": "Invalid side: %s" % side}
            
            request = {
                "action": mt5.TRADE_ACTION_DEAL if order_type in [mt5.ORDER_TYPE_BUY, mt5.ORDER_TYPE_SELL] else mt5.TRADE_ACTION_PENDING,
                "symbol": symbol,
                "volume": volume,
                "type": order_type,
                "price": price,
                "deviation": 10,
                "magic": 123456,
                "comment": comment,
                "type_time": mt5.ORDER_TIME_GTC,
                "type_filling": type_filling,
            }
            
            if sl: request["sl"] = sl
            if tp: request["tp"] = tp
            
            result = mt5.order_send(request)
            if result.retcode != mt5.TRADE_RETCODE_DONE:
                return {"error": "Order failed: %s (retcode: %d)" % (result.comment, result.retcode)}
            
            return {
                "status": "filled" if order_type in [mt5.ORDER_TYPE_BUY, mt5.ORDER_TYPE_SELL] else "placed",
                "order_id": result.order,
                "symbol": symbol,
                "price": result.price,
            }

        elif cmd == "cancel_order":
            if mt5 is None:
                return {"error": "MT5 not available"}
            ticket = int(command.get("ticket"))
            request = {
                "action": mt5.TRADE_ACTION_REMOVE,
                "order": ticket,
            }
            result = mt5.order_send(request)
            if result.retcode != mt5.TRADE_RETCODE_DONE:
                return {"error": "Cancel failed: %s" % result.comment}
            return {"status": "cancelled", "ticket": ticket}

        elif cmd == "modify_order":
            if mt5 is None:
                return {"error": "MT5 not available"}
            ticket = int(command.get("ticket"))
            price = command.get("price")
            sl = command.get("sl")
            tp = command.get("tp")
            request = {
                "action": mt5.TRADE_ACTION_MODIFY,
                "order": ticket,
            }
            if price: request["price"] = float(price)
            if sl: request["sl"] = float(sl)
            if tp: request["tp"] = float(tp)
            
            result = mt5.order_send(request)
            if result.retcode != mt5.TRADE_RETCODE_DONE:
                return {"error": "Modify failed: %s" % result.comment}
            return {"status": "modified", "ticket": ticket}

        elif cmd == "positions":
            if mt5 is None:
                return {"error": "MT5 not available"}
            positions = mt5.positions_get(symbol=symbol) if symbol else mt5.positions_get()
            if positions is None:
                return []
            return [
                {
                    "symbol": p.symbol,
                    "ticket": p.ticket,
                    "side": "BUY" if p.type == mt5.ORDER_TYPE_BUY else "SELL",
                    "volume": p.volume,
                    "price": p.price_open,
                    "profit": p.profit,
                } for p in positions
            ]

        elif cmd == "orders":
            if mt5 is None:
                return {"error": "MT5 not available"}
            orders = mt5.orders_get(symbol=symbol) if symbol else mt5.orders_get()
            if orders is None:
                return []
            return [
                {
                    "symbol": o.symbol,
                    "ticket": o.ticket,
                    "side": str(o.type),
                    "volume": o.volume_initial,
                    "price": o.price_open,
                } for o in orders
            ]

        elif cmd == "position":
            if mt5 is None:
                return {"error": "MT5 not available"}
            
            positions = mt5.positions_get(symbol=symbol)
            if positions is None or len(positions) == 0:
                return {"status": "no_position"}
            
            pos = positions[0]
            return {
                "symbol": pos.symbol,
                "ticket": pos.ticket,
                "side": "BUY" if pos.type == mt5.ORDER_TYPE_BUY else "SELL",
                "volume": pos.volume,
                "price": pos.price_open,
                "sl": pos.sl,
                "tp": pos.tp,
                "profit": pos.profit,
            }

        return {"error": "Unknown command: %s" % cmd}

    except Exception as e:
        return {"error": str(e)}


def handle_client_persistent(client_socket, mt5):
    """Handle a persistent client connection - multiple commands per connection."""
    try:
        buffer = b""
        while True:
            # Receive data
            data = client_socket.recv(4096)
            if not data:
                break
            
            buffer += data
            
            # Process all complete messages (newline-delimited)
            while b"\n" in buffer:
                line, buffer = buffer.split(b"\n", 1)
                if line:
                    try:
                        command = json.loads(line.decode())
                        result = handle_command(mt5, command)
                        response = json.dumps(result).encode() + b"\n"
                        client_socket.sendall(response)
                    except json.JSONDecodeError:
                        error = json.dumps({"error": "Invalid JSON"}).encode() + b"\n"
                        client_socket.sendall(error)
                        
    except Exception as e:
        print("Client error: %s" % e)
    finally:
        client_socket.close()


def run_server(port=5555):
    """Run the TCP server with persistent connections."""
    mt5, error = get_mt5()
    if error:
        print("WARNING: %s" % error)
        mt5 = None
    else:
        print("MT5 initialized: %s" % mt5.terminal_info().name)

    print("MT5 Fast Bridge Server running on port %d (persistent connections)" % port)
    if mt5:
        print("Terminal: %s" % mt5.terminal_info().name)

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    except:
        pass
    server.bind(("127.0.0.1", port))
    server.listen(5)
    server.settimeout(1.0)

    try:
        while True:
            try:
                client, addr = server.accept()
                # Handle each client in a new thread for persistence
                client_thread = threading.Thread(
                    target=handle_client_persistent,
                    args=(client, mt5),
                    daemon=True
                )
                client_thread.start()
            except socket.timeout:
                continue
            except Exception as e:
                print("Server error: %s" % e)
    except KeyboardInterrupt:
        print("Shutting down...")
    finally:
        if mt5:
            mt5.shutdown()
        server.close()


if __name__ == "__main__":
    port = 5555
    if "--port" in sys.argv:
        idx = sys.argv.index("--port")
        if idx + 1 < len(sys.argv):
            port = int(sys.argv[idx + 1])
    elif len(sys.argv) > 1 and sys.argv[1] != "--port":
        port = int(sys.argv[1])

    run_server(port)
