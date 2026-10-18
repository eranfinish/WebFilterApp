package com.urlfilter.vpn;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.net.VpnService;
import android.os.Build;
import android.os.ParcelFileDescriptor;
import android.util.Log;
import android.os.Handler;
import android.os.HandlerThread;

import androidx.core.app.NotificationCompat;

import com.urlfilter.R;
import com.urlfilter.admin.MainActivity;
import com.urlfilter.database.DatabaseHelper;
import com.urlfilter.models.DataModels;
import com.urlfilter.utils.UserDetector;
import com.urlfilter.utils.FilterEngine;
import com.urlfilter.utils.NetworkUtils;

import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.nio.channels.SelectionKey;
import java.nio.channels.Selector;
import java.nio.channels.SocketChannel;
import java.util.Iterator;
import java.util.Set;
import java.nio.channels.FileChannel;
import java.nio.channels.SocketChannel;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * VPN Service for URL filtering using Android's VpnService API
 * Intercepts all network traffic and filters based on user-defined rules
 */
public class UrlFilterVpnService extends VpnService {
    
    private static final String TAG = "UrlFilterVpnService";
    private static final String NOTIFICATION_CHANNEL_ID = "url_filter_channel";
    private static final int NOTIFICATION_ID = 1001;
    
    // VPN Configuration
    private static final String VPN_ADDRESS = "10.0.0.1";
    private static final String VPN_ROUTE = "0.0.0.0";
    private static final int VPN_DNS_PORT = 53;
    private static final int BUFFER_SIZE = 32767;
    private static final int MAX_PACKET_SIZE = 1500;
    
    // Service state
    private final AtomicBoolean isRunning = new AtomicBoolean(false);
    private ParcelFileDescriptor vpnInterface;
    private HandlerThread handlerThread;
    private Handler backgroundHandler;
    private ExecutorService executorService;
    
    // Core components
    private DatabaseHelper databaseHelper;
    private UserDetector userDetector;
    private FilterEngine filterEngine;
    private LocalProxyServer proxyServer;
    private TrafficAnalyzer trafficAnalyzer;
    private UserDetectionService userDetectionService;
    
    // Traffic handling
    private FileInputStream vpnInputStream;
    private FileOutputStream vpnOutputStream;
    
    @Override
    public void onCreate() {
        super.onCreate();
        Log.i(TAG, "VPN Service created");
        
        // Initialize core components
        databaseHelper = new DatabaseHelper(this);
        userDetector = new UserDetector(this);
        userDetectionService = new UserDetectionService(this, databaseHelper);
        filterEngine = new FilterEngine(this, databaseHelper);
        proxyServer = new LocalProxyServer(this, filterEngine);
        trafficAnalyzer = new TrafficAnalyzer(this, databaseHelper);
        
        // Setup background thread
        handlerThread = new HandlerThread("VpnServiceThread");
        handlerThread.start();
        backgroundHandler = new Handler(handlerThread.getLooper());
        
        // Create thread pool for packet processing
        executorService = Executors.newFixedThreadPool(4);
        
        // Create notification channel
        createNotificationChannel();
    }
    
    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        Log.i(TAG, "VPN Service start command received");
        
        if (intent != null) {
            String action = intent.getAction();
            if ("STOP_VPN".equals(action)) {
                stopVpn();
                return START_NOT_STICKY;
            }
        }
        
        if (!isRunning.get()) {
            startVpn();
        }
        
        return START_STICKY; // Service should be restarted if killed
    }
    
    private void startVpn() {
        Log.i(TAG, "Starting VPN service");
        
        if (prepare(this) != null) {
            Log.e(TAG, "VPN permission not granted");
            return;
        }
        
        try {
            // Configure VPN interface
            Builder builder = new Builder()
                    .setSession(getString(R.string.app_name))
                    .addAddress(VPN_ADDRESS, 24)
                    .addRoute(VPN_ROUTE, 0)
                    .addDnsServer("8.8.8.8")
                    .addDnsServer("8.8.4.4")
                    .setMtu(MAX_PACKET_SIZE)
                    .setBlocking(false);
            
            // Add applications that should be bypassed (e.g., this app itself)
            try {
                builder.addDisallowedApplication(getPackageName());
            } catch (Exception e) {
                Log.w(TAG, "Cannot bypass own application: " + e.getMessage());
            }
            
            // Establish VPN connection
            vpnInterface = builder.establish();
            
            if (vpnInterface == null) {
                Log.e(TAG, "Failed to establish VPN interface");
                return;
            }
            
            // Setup I/O streams
            vpnInputStream = new FileInputStream(vpnInterface.getFileDescriptor());
            vpnOutputStream = new FileOutputStream(vpnInterface.getFileDescriptor());
            
            // Start background services
            startForeground(NOTIFICATION_ID, createNotification());
            startProxyServer();
            startPacketProcessing();
            
            isRunning.set(true);
            Log.i(TAG, "VPN service started successfully");
            
            // Log service start
            logServiceEvent("VPN_STARTED");
            
        } catch (Exception e) {
            Log.e(TAG, "Error starting VPN service", e);
            stopVpn();
        }
    }
    
    private void stopVpn() {
        Log.i(TAG, "Stopping VPN service");
        
        isRunning.set(false);
        
        try {
            // Stop proxy server
            if (proxyServer != null) {
                proxyServer.stop();
            }
            
            // Close VPN interface
            if (vpnInterface != null) {
                vpnInterface.close();
                vpnInterface = null;
            }
            
            // Close streams
            if (vpnInputStream != null) {
                vpnInputStream.close();
                vpnInputStream = null;
            }
            
            if (vpnOutputStream != null) {
                vpnOutputStream.close();
                vpnOutputStream = null;
            }
            
            // Stop foreground service
            stopForeground(true);
            
            // Log service stop
            logServiceEvent("VPN_STOPPED");
            
        } catch (IOException e) {
            Log.e(TAG, "Error stopping VPN service", e);
        }
    }
    
    private void startProxyServer() {
        backgroundHandler.post(() -> {
            try {
                proxyServer.start(8080);
                Log.i(TAG, "Proxy server started on port 8080");
            } catch (IOException e) {
                Log.e(TAG, "Failed to start proxy server", e);
            }
        });
    }
    
    private void startPacketProcessing() {
        backgroundHandler.post(this::processPackets);
    }
    
    private void processPackets() {
        Log.i(TAG, "Starting packet processing");
        ByteBuffer packet = ByteBuffer.allocate(BUFFER_SIZE);
        
        while (isRunning.get() && vpnInputStream != null) {
            try {
                packet.clear();
                
                // Read packet from VPN interface
                int length = vpnInputStream.read(packet.array());
                
                if (length > 0) {
                    packet.limit(length);
                    
                    // Process packet asynchronously
                    executorService.submit(() -> handlePacket(packet.array(), length));
                    
                    // Small delay to prevent CPU overuse
                    Thread.sleep(1);
                }
                
            } catch (IOException e) {
                if (isRunning.get()) {
                    Log.e(TAG, "Error reading packet", e);
                }
                break;
            } catch (InterruptedException e) {
                Log.i(TAG, "Packet processing interrupted");
                break;
            }
        }
        
        Log.i(TAG, "Packet processing stopped");
    }
    
    private void handlePacket(byte[] packetData, int length) {
        try {
            // Parse IP packet
            IpPacket ipPacket = IpPacket.parsePacket(packetData, length);
            
            if (ipPacket == null) {
                // Forward unrecognized packets
                writePacketToVpn(packetData, length);
                return;
            }
            
            // Get current user
            DataModels.DeviceUser currentUser = userDetector.getCurrentUser();
            
            // Analyze and filter traffic
            TrafficAnalysis analysis = trafficAnalyzer.analyzePacket(ipPacket, currentUser);
            DataModels.FilterResult filterResult = filterEngine.checkPacketAccess(analysis, currentUser);
            
            if (filterResult.isAllowed) {
                // Forward allowed packet
                forwardPacket(ipPacket);
                
                // Log allowed traffic if needed
                if (analysis.isSignificant()) {
                    logTrafficEvent(currentUser, analysis, "ALLOWED");
                }
            } else {
                // Block packet and log
                blockPacket(ipPacket, filterResult, currentUser);
                logBlockedAttempt(currentUser, analysis, filterResult.reason);
            }
            
        } catch (Exception e) {
            Log.e(TAG, "Error handling packet", e);
            // Forward packet on error to maintain connectivity
            writePacketToVpn(packetData, length);
        }
    }
    
    private void forwardPacket(IpPacket packet) {
        try {
            // Create socket connection to destination
            SocketChannel socketChannel = SocketChannel.open();
            socketChannel.configureBlocking(false);
            
            // Connect to destination
            InetSocketAddress destination = new InetSocketAddress(
                    packet.getDestinationAddress(),
                    packet.getDestinationPort()
            );
            
            boolean connected = socketChannel.connect(destination);
            if (!connected) {
                // Wait for connection to complete (non-blocking)
                while (!socketChannel.finishConnect()) {
                    Thread.sleep(10);
                }
            }
            
            // Forward original packet data
            ByteBuffer outgoingBuffer = ByteBuffer.wrap(packet.getData());
            socketChannel.write(outgoingBuffer);
            
            // Handle response and write back to VPN interface
            handleSocketResponse(socketChannel, packet);
            
        } catch (IOException | InterruptedException e) {
            Log.e(TAG, "Error forwarding packet", e);
            Thread.currentThread().interrupt();
        }
    }
    
    private void handleSocketResponse(SocketChannel socketChannel, IpPacket originalPacket) {
        try {
            // Create buffer for response data
            ByteBuffer responseBuffer = ByteBuffer.allocate(8192); // 8KB buffer
            int totalBytesRead = 0;
            long startTime = System.currentTimeMillis();
            long timeout = 5000; // 5 second timeout
            
            // Set up selector for non-blocking read
            Selector selector = Selector.open();
            socketChannel.register(selector, SelectionKey.OP_READ);
            
            while (System.currentTimeMillis() - startTime < timeout) {
                if (selector.select(100) > 0) { // 100ms timeout on select
                    Set<SelectionKey> selectedKeys = selector.selectedKeys();
                    Iterator<SelectionKey> keyIterator = selectedKeys.iterator();
                    
                    while (keyIterator.hasNext()) {
                        SelectionKey key = keyIterator.next();
                        
                        if (key.isReadable()) {
                            int bytesRead = socketChannel.read(responseBuffer);
                            
                            if (bytesRead > 0) {
                                totalBytesRead += bytesRead;
                                
                                // Process response data and create response packet
                                byte[] responseData = new byte[totalBytesRead];
                                responseBuffer.flip();
                                responseBuffer.get(responseData, 0, totalBytesRead);
                                
                                // Create response packet with swapped source/destination
                                IpPacket responsePacket = createResponsePacket(originalPacket, responseData);
                                
                                // Write response back to VPN interface
                                writePacketToVpn(responsePacket.toByteArray(), responsePacket.getTotalLength());
                                
                                // Update session statistics
                                updateSessionStats(userDetector.getCurrentUser(), originalPacket, true);
                                
                                // Reset buffer for potential additional data
                                responseBuffer.clear();
                            } else if (bytesRead == -1) {
                                // End of stream - connection closed by remote
                                break;
                            }
                        }
                        
                        keyIterator.remove();
                    }
                }
            }
            
            selector.close();
            socketChannel.close();
            
        } catch (IOException e) {
            Log.e(TAG, "Error handling socket response", e);
            try {
                socketChannel.close();
            } catch (IOException closeEx) {
                Log.e(TAG, "Error closing socket", closeEx);
            }
        }
    }
    
    private IpPacket createResponsePacket(IpPacket originalPacket, byte[] responseData) {
        try {
            // Create a new IP packet with swapped addresses for the response
            IpPacket responsePacket = new IpPacket();
            
            // Swap source and destination addresses
            responsePacket.setSourceAddress(originalPacket.getDestinationAddress());
            responsePacket.setDestinationAddress(originalPacket.getSourceAddress());
            responsePacket.setSourcePort(originalPacket.getDestinationPort());
            responsePacket.setDestinationPort(originalPacket.getSourcePort());
            
            // Set protocol and other headers
            responsePacket.setProtocol(originalPacket.getProtocol());
            responsePacket.setData(responseData);
            
            // Calculate checksums and finalize packet
            responsePacket.finalizePacket();
            
            return responsePacket;
            
        } catch (Exception e) {
            Log.e(TAG, "Error creating response packet", e);
            // Return a simple response packet as fallback
            return createSimpleResponsePacket(originalPacket, responseData);
        }
    }
    
    private IpPacket createSimpleResponsePacket(IpPacket originalPacket, byte[] responseData) {
        // Simplified response packet creation for error cases
        IpPacket responsePacket = new IpPacket();
        responsePacket.setSourceAddress(originalPacket.getDestinationAddress());
        responsePacket.setDestinationAddress(originalPacket.getSourceAddress());
        responsePacket.setData(responseData);
        return responsePacket;
    }
    
    private void blockPacket(IpPacket packet, DataModels.FilterResult filterResult, DataModels.DeviceUser user) {
        // Generate block response if needed (e.g., for HTTP requests)
        if (packet.isHttpRequest()) {
            byte[] blockResponse = generateHttpBlockResponse(filterResult.reason);
            writePacketToVpn(blockResponse, blockResponse.length);
        }
        
        // Update user session statistics
        updateSessionStats(user, packet, false);
    }
    
    private byte[] generateHttpBlockResponse(String reason) {
        String response = "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html\r\n" +
                "Content-Length: " + getBlockPageContent(reason).length() + "\r\n" +
                "\r\n" +
                getBlockPageContent(reason);
        
        return response.getBytes();
    }
    
    private String getBlockPageContent(String reason) {
        return "<!DOCTYPE html>" +
                "<html><head><title>Access Blocked</title>" +
                "<meta charset='utf-8'>" +
                "<style>body{font-family:Arial;text-align:center;margin-top:50px;}" +
                ".block-message{color:#d32f2f;font-size:18px;}</style>" +
                "</head><body>" +
                "<h1>Access Blocked</h1>" +
                "<p class='block-message'>This website has been blocked.</p>" +
                "<p>Reason: " + reason + "</p>" +
                "<p>If you believe this is an error, please contact your administrator.</p>" +
                "</body></html>";
    }
    
    private void writePacketToVpn(byte[] data, int length) {
        try {
            if (vpnOutputStream != null && isRunning.get()) {
                vpnOutputStream.write(data, 0, length);
                vpnOutputStream.flush();
            }
        } catch (IOException e) {
            Log.e(TAG, "Error writing packet to VPN", e);
        }
    }
    
    private void updateSessionStats(DataModels.DeviceUser user, IpPacket packet, boolean allowed) {
        backgroundHandler.post(() -> {
            try {
                if (user != null) {
                    // Update current session
                    DataModels.InternetSession session = getCurrentSession(user);
                    if (session != null) {
                        session.totalRequests++;
                        if (allowed) {
                            session.allowedRequests++;
                        } else {
                            session.blockedRequests++;
                        }
                        
                        // Update in database
                        databaseHelper.updateInternetSession(session);
                    }
                }
            } catch (Exception e) {
                Log.e(TAG, "Error updating session stats", e);
            }
        });
    }
    
    private DataModels.InternetSession getCurrentSession(DataModels.DeviceUser user) {
        // Get or create current session for user
        return databaseHelper.getCurrentSession(user.id);
    }
    
    private void logServiceEvent(String event) {
        backgroundHandler.post(() -> {
            try {
                // Log to audit trail
                databaseHelper.logAuditEvent("vpn_service", event, 
                        "VPN service event: " + event);
            } catch (Exception e) {
                Log.e(TAG, "Error logging service event", e);
            }
        });
    }
    
    private void logTrafficEvent(DataModels.DeviceUser user, TrafficAnalysis analysis, String action) {
        backgroundHandler.post(() -> {
            try {
                // Log significant traffic events for analysis
                databaseHelper.logTrafficEvent(user.id, analysis.getUrl(), 
                        analysis.getCategory(), action);
            } catch (Exception e) {
                Log.e(TAG, "Error logging traffic event", e);
            }
        });
    }
    
    private void logBlockedAttempt(DataModels.DeviceUser user, TrafficAnalysis analysis, String reason) {
        backgroundHandler.post(() -> {
            try {
                DataModels.BlockedAttempt attempt = new DataModels.BlockedAttempt();
                attempt.userId = user != null ? user.id : null;
                attempt.url = analysis.getUrl();
                attempt.contentType = analysis.getContentType();
                attempt.blockReason = reason;
                attempt.deviceInfo = NetworkUtils.getDeviceInfo();
                attempt.ipAddress = analysis.getDestinationIp();
                
                databaseHelper.insertBlockedAttempt(attempt);
            } catch (Exception e) {
                Log.e(TAG, "Error logging blocked attempt", e);
            }
        });
    }
    
    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    NOTIFICATION_CHANNEL_ID,
                    "URL Filter Service",
                    NotificationManager.IMPORTANCE_LOW
            );
            channel.setDescription("URL filtering and monitoring service");
            channel.setShowBadge(false);
            
            NotificationManager notificationManager = getSystemService(NotificationManager.class);
            notificationManager.createNotificationChannel(channel);
        }
    }
    
    private Notification createNotification() {
        Intent mainIntent = new Intent(this, MainActivity.class);
        PendingIntent pendingIntent = PendingIntent.getActivity(
                this, 0, mainIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
        );
        
        Intent stopIntent = new Intent(this, UrlFilterVpnService.class);
        stopIntent.setAction("STOP_VPN");
        PendingIntent stopPendingIntent = PendingIntent.getService(
                this, 0, stopIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE
        );
        
        return new NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
                .setContentTitle("URL Filter Active")
                .setContentText("Monitoring and filtering network traffic")
                .setSmallIcon(R.drawable.ic_shield)
                .setContentIntent(pendingIntent)
                .setOngoing(true)
                .setPriority(NotificationCompat.PRIORITY_LOW)
                .addAction(R.drawable.ic_stop, "Stop", stopPendingIntent)
                .build();
    }
    
    @Override
    public void onDestroy() {
        Log.i(TAG, "VPN Service destroyed");
        
        stopVpn();
        
        // Cleanup resources
        if (executorService != null) {
            executorService.shutdown();
        }
        
        if (handlerThread != null) {
            handlerThread.quitSafely();
        }
        
        if (databaseHelper != null) {
            databaseHelper.close();
        }
        
        super.onDestroy();
    }
    
    @Override
    public void onRevoke() {
        Log.i(TAG, "VPN permission revoked");
        stopVpn();
        super.onRevoke();
    }
    
    // Static method to check if VPN is running
    public static boolean isVpnRunning(Context context) {
        // Check if service is running
        // This would need to be implemented based on specific requirements
        return false;
    }
    
    // Static method to start VPN service
    public static void startVpnService(Context context) {
        Intent intent = new Intent(context, UrlFilterVpnService.class);
        context.startForegroundService(intent);
    }
    
    // Static method to stop VPN service  
    public static void stopVpnService(Context context) {
        Intent intent = new Intent(context, UrlFilterVpnService.class);
        intent.setAction("STOP_VPN");
        context.startService(intent);
    }
}