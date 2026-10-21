"""Synthetic inventory sender for ShockWave Shader Lab. No toy/Bluetooth access."""
import argparse
import json
import socket
import struct
import time


def osc_string(value):
    raw = value.encode("utf-8") + b"\0"
    return raw + b"\0" * (-len(raw) % 4)


def inventory_packet(toys):
    return osc_string("/szebs/shader/inventory") + osc_string(",s") + osc_string(json.dumps(toys, ensure_ascii=False, separators=(",", ":")))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=9002)
    parser.add_argument("--count", type=int, default=8, help="1–128 demo devices")
    parser.add_argument("--name", default="My custom toy ✨", help="Custom label for the fifth demo toy")
    parser.add_argument("--watch", action="store_true", help="Refresh battery/name metadata every two seconds; held levels remain untouched")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535 or not 1 <= args.count <= 128:
        parser.error("Port must be 1–65535 and count 1–128")
    names = ["Lush family", "Nora", "Domi", "Edge", args.name, "Future model", "Lush family #2", "Remote toy (demo)"]
    toys = [{"id": f"demo-{i}", "name": names[i] if i < len(names) else f"Demo toy {i + 1}", "connected": True, "battery": 100 - i % 70} for i in range(args.count)]
    for i, toy in enumerate(toys):
        labels = ["Vibration", "Rotation"] if i == 1 else ["Vibration 1", "Vibration 2"] if i == 3 else ["Vibration"]
        toy["functions"] = [{"id": f"function-{j}", "name": label} for j, label in enumerate(labels)]
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
        while True:
            sender.sendto(inventory_packet(toys), ("127.0.0.1", args.port))
            print(f"Sent {len(toys)} synthetic toy names to 127.0.0.1:{args.port}")
            if not args.watch:
                break
            time.sleep(2)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
