# Summarise a libpcap capture from `adb emu network capture` (no third-party deps).
# Usage: python -I pcap_summary.py <file.pcap> [t0_epoch]
# Prints one line per IPv4 packet: relative time, src, dst, proto, sport, dport, length.
import struct, sys, socket

def ip(b):
    return socket.inet_ntoa(b)

def main():
    path = sys.argv[1]
    data = open(path, 'rb').read()
    magic = data[:4]
    if magic in (b'\xd4\xc3\xb2\xa1', b'\x4d\x3c\xb2\xa1'):
        e = '<'
    elif magic in (b'\xa1\xb2\xc3\xd4', b'\xa1\xb2\x3c\x4d'):
        e = '>'
    else:
        print('not a classic pcap file; first bytes', data[:8].hex()); return
    linktype = struct.unpack(e + 'I', data[20:24])[0]
    print('linktype', linktype)
    off = 24
    first = None
    while off + 16 <= len(data):
        ts_sec, ts_usec, incl, orig = struct.unpack(e + 'IIII', data[off:off + 16])
        off += 16
        pkt = data[off:off + incl]
        off += incl
        t = ts_sec + ts_usec / 1e6
        if first is None:
            first = t
        ip_off = 14 if linktype == 1 else (0 if linktype == 101 else None)
        if ip_off is None or len(pkt) < ip_off + 20:
            continue
        if linktype == 1 and pkt[12:14] != b'\x08\x00':
            continue
        h = pkt[ip_off:]
        if (h[0] >> 4) != 4:
            continue
        ihl = (h[0] & 0x0F) * 4
        proto = h[9]
        src, dst = ip(h[12:16]), ip(h[16:20])
        sport = dport = ''
        if proto in (6, 17) and len(h) >= ihl + 4:
            sport, dport = struct.unpack('>HH', h[ihl:ihl + 4])
        print('%.3f %s %s proto=%d %s %s len=%d' % (t - first, src, dst, proto, sport, dport, orig))

if __name__ == '__main__':
    main()
