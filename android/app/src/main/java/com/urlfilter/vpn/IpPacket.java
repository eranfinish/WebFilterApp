package com.urlfilter.vpn;

import java.net.InetAddress;
import java.net.UnknownHostException;
import java.nio.ByteBuffer;

/**
 * IP Packet parser and analyzer for VPN traffic
 * Handles IPv4 packet parsing and protocol identification
 */
public class IpPacket {
    
    private static final int IPV4_HEADER_MIN_LENGTH = 20;
    private static final int TCP_HEADER_MIN_LENGTH = 20;
    private static final int UDP_HEADER_LENGTH = 8;
    
    // IP Protocol numbers
    public static final int PROTOCOL_TCP = 6;
    public static final int PROTOCOL_UDP = 17;
    public static final int PROTOCOL_ICMP = 1;
    
    // Well-known ports
    public static final int PORT_HTTP = 80;
    public static final int PORT_HTTPS = 443;
    public static final int PORT_DNS = 53;
    public static final int PORT_FTP = 21;
    public static final int PORT_SSH = 22;
    
    private byte[] rawData;
    private int totalLength;
    private int protocol;
    private String sourceAddress;
    private String destinationAddress;
    private int sourcePort;
    private int destinationPort;
    private byte[] payload;
    private boolean isValidPacket;
    
    private IpPacket(byte[] data) {
        this.rawData = data;
        this.isValidPacket = false;
    }
    
    public static IpPacket parsePacket(byte[] data, int length) {
        if (data == null || length < IPV4_HEADER_MIN_LENGTH) {
            return null;
        }
        
        IpPacket packet = new IpPacket(data);
        
        try {
            // Parse IP header
            packet.parseIpHeader(data);
            
            // Parse transport layer (TCP/UDP)
            packet.parseTransportHeader(data);
            
            packet.isValidPacket = true;
            return packet;
            
        } catch (Exception e) {
            // Return null for invalid packets
            return null;
        }
    }
    
    private void parseIpHeader(byte[] data) throws Exception {
        ByteBuffer buffer = ByteBuffer.wrap(data);
        
        // Check IP version (should be 4)
        int versionAndHeaderLength = buffer.get() & 0xFF;
        int version = (versionAndHeaderLength >> 4) & 0x0F;
        if (version != 4) {
            throw new Exception("Not IPv4 packet");
        }
        
        int headerLength = (versionAndHeaderLength & 0x0F) * 4;
        if (headerLength < IPV4_HEADER_MIN_LENGTH) {
            throw new Exception("Invalid IP header length");
        }
        
        // Skip Type of Service
        buffer.get();
        
        // Total length
        this.totalLength = buffer.getShort() & 0xFFFF;
        
        // Skip ID, Flags, Fragment Offset
        buffer.getShort(); // ID
        buffer.getShort(); // Flags and Fragment Offset
        
        // Skip TTL
        buffer.get();
        
        // Protocol
        this.protocol = buffer.get() & 0xFF;
        
        // Skip checksum
        buffer.getShort();
        
        // Source address
        byte[] srcAddr = new byte[4];
        buffer.get(srcAddr);
        this.sourceAddress = InetAddress.getByAddress(srcAddr).getHostAddress();
        
        // Destination address
        byte[] destAddr = new byte[4];
        buffer.get(destAddr);
        this.destinationAddress = InetAddress.getByAddress(destAddr).getHostAddress();
        
        // Skip options if present
        buffer.position(headerLength);
        
        // Extract payload
        int payloadLength = Math.max(0, Math.min(totalLength - headerLength, data.length - headerLength));
        this.payload = new byte[payloadLength];
        buffer.get(this.payload, 0, payloadLength);
    }
    
    private void parseTransportHeader(byte[] data) throws Exception {
        if (payload.length == 0) {
            return;
        }
        
        ByteBuffer buffer = ByteBuffer.wrap(payload);
        
        switch (protocol) {
            case PROTOCOL_TCP:
                parseTcpHeader(buffer);
                break;
            case PROTOCOL_UDP:
                parseUdpHeader(buffer);
                break;
            default:
                // For other protocols, ports are not applicable
                this.sourcePort = 0;
                this.destinationPort = 0;
                break;
        }
    }
    
    private void parseTcpHeader(ByteBuffer buffer) throws Exception {
        if (buffer.remaining() < TCP_HEADER_MIN_LENGTH) {
            throw new Exception("Invalid TCP header length");
        }
        
        // Source port
        this.sourcePort = buffer.getShort() & 0xFFFF;
        
        // Destination port
        this.destinationPort = buffer.getShort() & 0xFFFF;
        
        // Skip sequence number, ack number
        buffer.getInt(); // Sequence number
        buffer.getInt(); // Acknowledgment number
        
        // Header length and flags
        int headerLengthAndFlags = buffer.getShort() & 0xFFFF;
        int tcpHeaderLength = ((headerLengthAndFlags >> 12) & 0x0F) * 4;
        
        // Skip window size, checksum, urgent pointer
        buffer.getShort(); // Window size
        buffer.getShort(); // Checksum
        buffer.getShort(); // Urgent pointer
        
        // Skip TCP options
        if (tcpHeaderLength > TCP_HEADER_MIN_LENGTH) {
            int optionsLength = tcpHeaderLength - TCP_HEADER_MIN_LENGTH;
            buffer.position(buffer.position() + optionsLength);
        }
    }
    
    private void parseUdpHeader(ByteBuffer buffer) throws Exception {
        if (buffer.remaining() < UDP_HEADER_LENGTH) {
            throw new Exception("Invalid UDP header length");
        }
        
        // Source port
        this.sourcePort = buffer.getShort() & 0xFFFF;
        
        // Destination port
        this.destinationPort = buffer.getShort() & 0xFFFF;
        
        // Skip length and checksum
        buffer.getShort(); // Length
        buffer.getShort(); // Checksum
    }
    
    // Getter methods
    public byte[] getRawData() {
        return rawData;
    }
    
    public int getTotalLength() {
        return totalLength;
    }
    
    public int getProtocol() {
        return protocol;
    }
    
    public String getSourceAddress() {
        return sourceAddress;
    }
    
    public String getDestinationAddress() {
        return destinationAddress;
    }
    
    public int getSourcePort() {
        return sourcePort;
    }
    
    public int getDestinationPort() {
        return destinationPort;
    }
    
    public byte[] getData() {
        return payload;
    }
    
    public boolean isValidPacket() {
        return isValidPacket;
    }
    
    // Protocol identification methods
    public boolean isTcp() {
        return protocol == PROTOCOL_TCP;
    }
    
    public boolean isUdp() {
        return protocol == PROTOCOL_UDP;
    }
    
    public boolean isIcmp() {
        return protocol == PROTOCOL_ICMP;
    }
    
    // Port-based service identification
    public boolean isHttpRequest() {
        return (isTcp() || isUdp()) && (destinationPort == PORT_HTTP);
    }
    
    public boolean isHttpsRequest() {
        return (isTcp() || isUdp()) && (destinationPort == PORT_HTTPS);
    }
    
    public boolean isDnsRequest() {
        return isUdp() && (destinationPort == PORT_DNS || sourcePort == PORT_DNS);
    }
    
    public boolean isWebTraffic() {
        return isHttpRequest() || isHttpsRequest();
    }
    
    public boolean isFtpTraffic() {
        return isTcp() && (destinationPort == PORT_FTP || sourcePort == PORT_FTP);
    }
    
    public boolean isSshTraffic() {
        return isTcp() && (destinationPort == PORT_SSH || sourcePort == PORT_SSH);
    }
    
    // Analysis methods
    public String getProtocolName() {
        switch (protocol) {
            case PROTOCOL_TCP:
                return "TCP";
            case PROTOCOL_UDP:
                return "UDP";
            case PROTOCOL_ICMP:
                return "ICMP";
            default:
                return "Unknown(" + protocol + ")";
        }
    }
    
    public String getServiceType() {
        if (isHttpRequest()) {
            return "HTTP";
        } else if (isHttpsRequest()) {
            return "HTTPS";
        } else if (isDnsRequest()) {
            return "DNS";
        } else if (isFtpTraffic()) {
            return "FTP";
        } else if (isSshTraffic()) {
            return "SSH";
        } else {
            return "Other";
        }
    }
    
    public boolean isOutgoingPacket() {
        // Determine if this is an outgoing packet based on source address
        // This is a simplified check - in practice, you'd compare against
        // the VPN interface address range
        return sourceAddress != null && sourceAddress.startsWith("10.0.0.");
    }
    
    public boolean isIncomingPacket() {
        return !isOutgoingPacket();
    }
    
    // HTTP content analysis (basic)
    public boolean containsHttpData() {
        if (!isHttpRequest() || payload == null) {
            return false;
        }
        
        String payloadStr = new String(payload);
        return payloadStr.contains("HTTP/") || 
               payloadStr.contains("GET ") || 
               payloadStr.contains("POST ") ||
               payloadStr.contains("Host:");
    }
    
    public String extractHttpHost() {
        if (!containsHttpData()) {
            return null;
        }
        
        String payloadStr = new String(payload);
        String[] lines = payloadStr.split("\r?\n");
        
        for (String line : lines) {
            if (line.toLowerCase().startsWith("host:")) {
                String host = line.substring(5).trim();
                // Remove port number if present
                int colonIndex = host.indexOf(':');
                if (colonIndex > 0) {
                    host = host.substring(0, colonIndex);
                }
                return host;
            }
        }
        
        return null;
    }
    
    public String extractHttpPath() {
        if (!containsHttpData()) {
            return null;
        }
        
        String payloadStr = new String(payload);
        String[] lines = payloadStr.split("\r?\n");
        
        if (lines.length > 0) {
            String requestLine = lines[0];
            String[] parts = requestLine.split(" ");
            if (parts.length >= 2) {
                return parts[1]; // GET /path HTTP/1.1
            }
        }
        
        return null;
    }
    
    public String getFullUrl() {
        String host = extractHttpHost();
        String path = extractHttpPath();
        
        if (host != null && path != null) {
            String protocol = isHttpsRequest() ? "https" : "http";
            return protocol + "://" + host + path;
        }
        
        return null;
    }
    
    @Override
    public String toString() {
        return String.format("IpPacket{%s %s:%d -> %s:%d, %s, length=%d}",
                getProtocolName(),
                sourceAddress, sourcePort,
                destinationAddress, destinationPort,
                getServiceType(),
                totalLength);
    }
}