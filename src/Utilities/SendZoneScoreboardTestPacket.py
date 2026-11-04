# SEND TEST LASER GAME PACKETS FOR A KIOSK'S PACKET SNIFFER TO PICK UP
#
# Payloads use the real Begeara 2 control box format (comma-separated ASCII):
#   1 Timing         1,<game no>,"@<mode>",<seconds remaining>,?
#   3 Player score   3,<gun>,<team>,<score>,<rank>,?,<shots fired>,<accuracy>,?
#   4 Game status    4,@015,0 (game started) / 4,@014,0 (game ended)
#   5 Shot confirmed 5,<gun hit>,<shooter gun>,0,0,0,0,0
#
# Examples (needs scapy and, on Windows, Npcap; run as admin):
#   python SendZoneScoreboardTestPacket.py                      # one start, timing, score, end
#   python SendZoneScoreboardTestPacket.py --game --seconds 60  # a whole busy game
#   python SendZoneScoreboardTestPacket.py --game --noise 500   # plus other LAN traffic per second,
#                                                               # to compare CPU with the capture filter on/off

import argparse
import random
import time

from scapy.all import send, IP, UDP, TCP, Raw  # type: ignore

parser = argparse.ArgumentParser(description="Send laser game test packets.")
parser.add_argument("--src", default="192.168.0.10", help="Control box IP; must match the kiosk's source IP setting")
parser.add_argument("--dst", default="192.168.0.255", help="Must match the kiosk's destination IP setting (broadcast)")
parser.add_argument("--port", type=int, default=5000)
parser.add_argument("--game", action="store_true", help="Simulate a whole game instead of one of each packet")
parser.add_argument("--seconds", type=int, default=60, help="Game length when --game is set")
parser.add_argument("--guns", type=int, default=12, help="Number of guns when --game is set")
parser.add_argument("--rate", type=int, default=40, help="Score and shot packets per second when --game is set")
parser.add_argument("--noise", type=int, default=0, help="Non-game packets per second to send alongside the game")
args = parser.parse_args()


def game_packet(payload):
    return IP(src=args.src, dst=args.dst) / UDP(sport=12345, dport=args.port) / Raw(load=payload.encode("ascii"))


def noise_packets(count):
    # Traffic a capture filter should drop: other hosts, other protocols.
    packets = []
    for _ in range(count):
        host = f"192.168.0.{random.randint(50, 200)}"
        if random.random() < 0.5:
            packets.append(IP(src=host, dst=args.dst) / UDP(sport=random.randint(1024, 65535), dport=random.randint(1024, 65535)) / Raw(load=b"x" * 64))
        else:
            packets.append(IP(src=host, dst="192.168.0.1") / TCP(sport=random.randint(1024, 65535), dport=443) / Raw(load=b"x" * 512))
    return packets


def send_one(payload):
    send(game_packet(payload), verbose=False)
    print(f"Sent: {payload}")


game_number = random.randint(1000, 9999)

if not args.game:
    send_one("4,@015,0")
    send_one(f'1,{game_number},"@004",300,1')
    send_one("3,1,0,30,1,30,3,10,0")
    send_one("5,11,1,2,0,0,0,0")
    send_one("4,@014,0")
else:
    scores = {gun: 0 for gun in range(1, args.guns + 1)}
    shots = {gun: 0 for gun in range(1, args.guns + 1)}
    sent = 0

    send_one("4,@015,0")

    for remaining in range(args.seconds, -1, -1):
        second_started = time.time()
        packets = [game_packet(f'1,{game_number},"@004",{remaining},1')]

        for _ in range(args.rate // 2):
            shooter = random.randint(1, args.guns)
            target = random.choice([g for g in scores if g != shooter])
            shots[shooter] += 1
            scores[shooter] += 10
            accuracy = min(100, scores[shooter] // max(1, shots[shooter]))
            team = 0 if shooter % 2 else 2
            packets.append(game_packet(f"5,{target},{shooter},2,0,0,0,0"))
            packets.append(game_packet(f"3,{shooter},{team},{scores[shooter]},1,0,{shots[shooter]},{accuracy},0"))

        packets.extend(noise_packets(args.noise))

        send(packets, verbose=False)
        sent += len(packets)
        print(f"{remaining:>4}s left, {sent} packets sent")

        time.sleep(max(0.0, 1.0 - (time.time() - second_started)))

    send_one("4,@014,0")
