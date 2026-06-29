# SEND A TEST UDP PACKET TO THE LOCAL MACHINE

from scapy.all import send, IP, UDP, Raw  # type: ignore

src_ip = "192.168.0.10"    # Must match one of your SourceIPs
dst_ip = "192.168.0.255"   # Must match DestinationIP (broadcast)

# Packet type as first field, then whatever fields your handlers expect
payloads = {
    "timing":       "1,30,3,300,5,6,7",
    "player_score": "3,4,5,5,4,2,5,7,4,5,3",
    "game_status":  "4,@01610@0120@0122",
    "shot":         "5,6,7",
}

# Pick which one to send
payload = payloads["game_status"]

packet = IP(src=src_ip, dst=dst_ip) / UDP(sport=12345, dport=5000) / Raw(load=payload.encode("ascii"))

send(packet)
print(f"Sent: {payload}")

payload = payloads["timing"]

packet = IP(src=src_ip, dst=dst_ip) / UDP(sport=12345, dport=5000) / Raw(load=payload.encode("ascii"))

send(packet)
print(f"Sent: {payload}")

payload = payloads["player_score"]

packet = IP(src=src_ip, dst=dst_ip) / UDP(sport=12345, dport=5000) / Raw(load=payload.encode("ascii"))

send(packet)
print(f"Sent: {payload}")