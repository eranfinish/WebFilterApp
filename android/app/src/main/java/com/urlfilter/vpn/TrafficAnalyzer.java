package com.urlfilter.vpn;

import android.content.Context;
import android.util.Log;

import com.urlfilter.database.DatabaseHelper;
import com.urlfilter.models.DataModels;
import com.urlfilter.utils.YouTubeUrlParser;
import com.urlfilter.utils.DomainClassifier;

import java.net.MalformedURLException;
import java.net.URL;
import java.util.Date;
import java.util.regex.Pattern;

/**
 * Analyzes network traffic to determine content type, destination, and filtering requirements
 */
public class TrafficAnalyzer {
    
    private static final String TAG = "TrafficAnalyzer";
    
    private final Context context;
    private final DatabaseHelper databaseHelper;
    private final YouTubeUrlParser youtubeParser;
    private final DomainClassifier domainClassifier;
    
    public TrafficAnalyzer(Context context, DatabaseHelper databaseHelper) {
        this.context = context;
        this.databaseHelper = databaseHelper;
        this.youtubeParser = new YouTubeUrlParser();
        this.domainClassifier = new DomainClassifier(context);
    }
    
    public TrafficAnalysis analyzePacket(IpPacket packet, DataModels.DeviceUser user) {
        TrafficAnalysis analysis = new TrafficAnalysis();
        
        // Basic packet information
        analysis.setSourceIp(packet.getSourceAddress());
        analysis.setDestinationIp(packet.getDestinationAddress());
        analysis.setSourcePort(packet.getSourcePort());
        analysis.setDestinationPort(packet.getDestinationPort());
        analysis.setProtocol(packet.getProtocolName());
        analysis.setServiceType(packet.getServiceType());
        analysis.setTimestamp(new Date());
        analysis.setUserId(user != null ? user.id : null);
        
        try {
            // Analyze based on packet type
            if (packet.isWebTraffic()) {
                analyzeWebTraffic(packet, analysis);
            } else if (packet.isDnsRequest()) {
                analyzeDnsRequest(packet, analysis);
            } else {
                analyzeGeneralTraffic(packet, analysis);
            }
            
            // Classify content category
            classifyContent(analysis);
            
            // Determine significance for logging
            determineSignificance(analysis);
            
        } catch (Exception e) {
            Log.e(TAG, "Error analyzing packet", e);
            analysis.setCategory("unknown");
            analysis.setContentType("unknown");
        }
        
        return analysis;
    }
    
    private void analyzeWebTraffic(IpPacket packet, TrafficAnalysis analysis) {
        analysis.setContentType("web");
        
        if (packet.containsHttpData()) {
            // Extract HTTP information
            String host = packet.extractHttpHost();
            String path = packet.extractHttpPath();
            String fullUrl = packet.getFullUrl();
            
            if (fullUrl != null) {
                analysis.setUrl(fullUrl);
                analysis.setDomain(host);
                analysis.setPath(path);
                
                // Check if it's YouTube
                if (youtubeParser.isYouTubeUrl(fullUrl)) {
                    analyzeYouTubeTraffic(fullUrl, analysis);
                }
            } else if (host != null) {
                analysis.setDomain(host);
                analysis.setUrl("http" + (packet.isHttpsRequest() ? "s" : "") + "://" + host + "/");
            }
        } else {
            // HTTPS or other web traffic without readable content
            analysis.setDomain(packet.getDestinationAddress());
            analysis.setUrl("https://" + packet.getDestinationAddress() + "/");
        }
    }
    
    private void analyzeDnsRequest(IpPacket packet, TrafficAnalysis analysis) {
        analysis.setContentType("dns");
        analysis.setServiceType("DNS");
        
        // Try to extract domain from DNS query
        String domain = extractDnsQuery(packet);
        if (domain != null) {
            analysis.setDomain(domain);
            analysis.setUrl("dns://" + domain);
            
            // Check if DNS query is for YouTube
            if (youtubeParser.isYouTubeDomain(domain)) {
                analysis.setContentType("youtube_dns");
            }
        }
    }
    
    private void analyzeGeneralTraffic(IpPacket packet, TrafficAnalysis analysis) {
        analysis.setContentType("other");
        analysis.setDomain(packet.getDestinationAddress());
        
        // Try to identify service based on port
        String service = identifyServiceByPort(packet.getDestinationPort());
        if (service != null) {
            analysis.setServiceType(service);
            analysis.setContentType(service.toLowerCase());
        }
    }
    
    private void analyzeYouTubeTraffic(String url, TrafficAnalysis analysis) {
        analysis.setContentType("youtube");
        
        try {
            YouTubeUrlParser.YouTubeInfo info = youtubeParser.parseYouTubeUrl(url);
            
            if (info != null) {
                analysis.setYoutubeChannelId(info.getChannelId());
                analysis.setYoutubeVideoId(info.getVideoId());
                analysis.setYoutubePlaylistId(info.getPlaylistId());
                
                // Get channel information from database
                if (info.getChannelId() != null) {
                    DataModels.YouTubeChannel channel = databaseHelper.getYouTubeChannelById(info.getChannelId());
                    if (channel != null) {
                        analysis.setYoutubeChannelName(channel.channelName);
                        analysis.setYoutubeChannelCategory(channel.category);
                        analysis.setYoutubeAgeRating(channel.ageRating);
                        analysis.setYoutubeEducational(channel.educationalContent);
                    }
                }
                
                // Set more specific content type
                if (info.getVideoId() != null) {
                    analysis.setContentType("youtube_video");
                } else if (info.getChannelId() != null) {
                    analysis.setContentType("youtube_channel");
                } else if (info.getPlaylistId() != null) {
                    analysis.setContentType("youtube_playlist");
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Error parsing YouTube URL: " + url, e);
        }
    }
    
    private String extractDnsQuery(IpPacket packet) {
        // This is a simplified DNS query extraction
        // In practice, you would need to parse the DNS packet format
        byte[] data = packet.getData();
        
        if (data.length < 12) { // Minimum DNS header size
            return null;
        }
        
        try {
            // Skip DNS header (12 bytes)
            int offset = 12;
            StringBuilder domain = new StringBuilder();
            
            while (offset < data.length) {
                int length = data[offset] & 0xFF;
                
                if (length == 0) {
                    break; // End of domain name
                }
                
                if (length > 63) {
                    break; // Invalid length or compression pointer
                }
                
                offset++;
                
                if (offset + length > data.length) {
                    break; // Not enough data
                }
                
                if (domain.length() > 0) {
                    domain.append(".");
                }
                
                for (int i = 0; i < length; i++) {
                    domain.append((char) (data[offset + i] & 0xFF));
                }
                
                offset += length;
            }
            
            return domain.length() > 0 ? domain.toString() : null;
            
        } catch (Exception e) {
            Log.e(TAG, "Error extracting DNS query", e);
            return null;
        }
    }
    
    private String identifyServiceByPort(int port) {
        switch (port) {
            case 21: return "FTP";
            case 22: return "SSH";
            case 23: return "Telnet";
            case 25: return "SMTP";
            case 53: return "DNS";
            case 80: return "HTTP";
            case 110: return "POP3";
            case 143: return "IMAP";
            case 443: return "HTTPS";
            case 993: return "IMAPS";
            case 995: return "POP3S";
            case 1194: return "OpenVPN";
            case 3389: return "RDP";
            case 5060: return "SIP";
            default: 
                // Check for common gaming ports
                if (port >= 27000 && port <= 27050) return "Steam";
                if (port >= 1024 && port <= 5000) return "Game";
                return null;
        }
    }
    
    private void classifyContent(TrafficAnalysis analysis) {
        String category = "other";
        
        try {
            if (analysis.getDomain() != null) {
                // Use domain classifier to determine category
                category = domainClassifier.classifyDomain(analysis.getDomain());
            }
            
            // Override with specific YouTube category if applicable
            if ("youtube".equals(analysis.getContentType()) && analysis.getYoutubeChannelCategory() != null) {
                category = analysis.getYoutubeChannelCategory();
            }
            
            // Special handling for educational content
            if (analysis.isYoutubeEducational()) {
                category = "educational";
            }
            
            analysis.setCategory(category);
            
        } catch (Exception e) {
            Log.e(TAG, "Error classifying content", e);
            analysis.setCategory("unknown");
        }
    }
    
    private void determineSignificance(TrafficAnalysis analysis) {
        boolean significant = false;
        
        // Web traffic is generally significant
        if ("web".equals(analysis.getContentType()) || 
            "youtube".equals(analysis.getContentType()) ||
            "youtube_video".equals(analysis.getContentType())) {
            significant = true;
        }
        
        // DNS queries for new domains are significant
        if ("dns".equals(analysis.getContentType()) && 
            !isKnownDomain(analysis.getDomain())) {
            significant = true;
        }
        
        // Traffic to blocked categories is always significant
        if (isBlockedCategory(analysis.getCategory())) {
            significant = true;
        }
        
        // Gaming and social media traffic is significant
        if ("gaming".equals(analysis.getCategory()) || 
            "social_media".equals(analysis.getCategory())) {
            significant = true;
        }
        
        analysis.setSignificant(significant);
    }
    
    private boolean isKnownDomain(String domain) {
        if (domain == null) {
            return false;
        }
        
        // Check if domain has been seen recently
        return databaseHelper.isDomainRecent(domain, 24); // Within last 24 hours
    }
    
    private boolean isBlockedCategory(String category) {
        if (category == null) {
            return false;
        }
        
        // Check if category is typically blocked
        return "adult".equals(category) || 
               "malware".equals(category) || 
               "phishing".equals(category) ||
               "gambling".equals(category);
    }
    
    public static class TrafficAnalysis {
        private String sourceIp;
        private String destinationIp;
        private int sourcePort;
        private int destinationPort;
        private String protocol;
        private String serviceType;
        private String contentType;
        private String category;
        private String domain;
        private String url;
        private String path;
        private Date timestamp;
        private Integer userId;
        private boolean significant;
        
        // YouTube-specific fields
        private String youtubeChannelId;
        private String youtubeVideoId;
        private String youtubePlaylistId;
        private String youtubeChannelName;
        private String youtubeChannelCategory;
        private String youtubeAgeRating;
        private boolean youtubeEducational;
        
        // Getters and setters
        public String getSourceIp() { return sourceIp; }
        public void setSourceIp(String sourceIp) { this.sourceIp = sourceIp; }
        
        public String getDestinationIp() { return destinationIp; }
        public void setDestinationIp(String destinationIp) { this.destinationIp = destinationIp; }
        
        public int getSourcePort() { return sourcePort; }
        public void setSourcePort(int sourcePort) { this.sourcePort = sourcePort; }
        
        public int getDestinationPort() { return destinationPort; }
        public void setDestinationPort(int destinationPort) { this.destinationPort = destinationPort; }
        
        public String getProtocol() { return protocol; }
        public void setProtocol(String protocol) { this.protocol = protocol; }
        
        public String getServiceType() { return serviceType; }
        public void setServiceType(String serviceType) { this.serviceType = serviceType; }
        
        public String getContentType() { return contentType; }
        public void setContentType(String contentType) { this.contentType = contentType; }
        
        public String getCategory() { return category; }
        public void setCategory(String category) { this.category = category; }
        
        public String getDomain() { return domain; }
        public void setDomain(String domain) { this.domain = domain; }
        
        public String getUrl() { return url; }
        public void setUrl(String url) { this.url = url; }
        
        public String getPath() { return path; }
        public void setPath(String path) { this.path = path; }
        
        public Date getTimestamp() { return timestamp; }
        public void setTimestamp(Date timestamp) { this.timestamp = timestamp; }
        
        public Integer getUserId() { return userId; }
        public void setUserId(Integer userId) { this.userId = userId; }
        
        public boolean isSignificant() { return significant; }
        public void setSignificant(boolean significant) { this.significant = significant; }
        
        // YouTube getters and setters
        public String getYoutubeChannelId() { return youtubeChannelId; }
        public void setYoutubeChannelId(String youtubeChannelId) { this.youtubeChannelId = youtubeChannelId; }
        
        public String getYoutubeVideoId() { return youtubeVideoId; }
        public void setYoutubeVideoId(String youtubeVideoId) { this.youtubeVideoId = youtubeVideoId; }
        
        public String getYoutubePlaylistId() { return youtubePlaylistId; }
        public void setYoutubePlaylistId(String youtubePlaylistId) { this.youtubePlaylistId = youtubePlaylistId; }
        
        public String getYoutubeChannelName() { return youtubeChannelName; }
        public void setYoutubeChannelName(String youtubeChannelName) { this.youtubeChannelName = youtubeChannelName; }
        
        public String getYoutubeChannelCategory() { return youtubeChannelCategory; }
        public void setYoutubeChannelCategory(String youtubeChannelCategory) { this.youtubeChannelCategory = youtubeChannelCategory; }
        
        public String getYoutubeAgeRating() { return youtubeAgeRating; }
        public void setYoutubeAgeRating(String youtubeAgeRating) { this.youtubeAgeRating = youtubeAgeRating; }
        
        public boolean isYoutubeEducational() { return youtubeEducational; }
        public void setYoutubeEducational(boolean youtubeEducational) { this.youtubeEducational = youtubeEducational; }
        
        @Override
        public String toString() {
            return String.format("TrafficAnalysis{%s -> %s, %s, %s, category=%s, significant=%s}",
                    sourceIp, destinationIp, contentType, url, category, significant);
        }
    }
}