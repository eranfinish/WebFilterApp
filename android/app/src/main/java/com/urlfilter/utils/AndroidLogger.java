package com.urlfilter.utils;

import android.content.Context;
import android.os.Build;
import android.util.Log;

import java.io.File;
import java.io.FileWriter;
import java.io.IOException;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingQueue;

/**
 * Universal Android Logger for URL Filter System
 * Provides file logging, console output, and structured logging capabilities
 * Thread-safe with async operations and log rotation
 */
public class AndroidLogger {
    
    private static final String TAG = "AndroidLogger";
    
    // Singleton instance
    private static AndroidLogger instance;
    private static final Object instanceLock = new Object();
    
    // Configuration
    private LogLevel minimumLevel = LogLevel.INFO;
    private boolean enableFileLogging = true;
    private boolean enableConsoleOutput = true;
    private int maxLogFileSizeMB = 50;
    private int maxLogFileCount = 5;
    
    // File handling
    private final Context context;
    private final File logDirectory;
    private File currentLogFile;
    private final SimpleDateFormat fileNameFormat = new SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US);
    private final SimpleDateFormat timestampFormat = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US);
    
    // Async logging
    private final BlockingQueue<LogEntry> logQueue = new LinkedBlockingQueue<>();
    private final ExecutorService executorService = Executors.newSingleThreadExecutor();
    private volatile boolean isRunning = true;
    
    // Log levels
    public enum LogLevel {
        TRACE(0), DEBUG(1), INFO(2), WARN(3), ERROR(4), CRITICAL(5);
        
        private final int value;
        LogLevel(int value) { this.value = value; }
        public int getValue() { return value; }
    }
    
    // Private constructor for singleton
    private AndroidLogger(Context context) {
        this.context = context.getApplicationContext();
        this.logDirectory = new File(context.getFilesDir(), "logs");
        
        // Create log directory
        if (!logDirectory.exists()) {
            logDirectory.mkdirs();
        }
        
        // Create current log file
        createNewLogFile();
        
        // Start background log processor
        executorService.submit(this::processLogQueue);
        
        // Log initialization
        logInfo("AndroidLogger initialized", "Logger");
        logInfo("Log directory: " + logDirectory.getAbsolutePath(), "Logger");
        logInfo("Device: " + Build.MANUFACTURER + " " + Build.MODEL + " (API " + Build.VERSION.SDK_INT + ")", "Logger");
    }
    
    // Singleton access
    public static AndroidLogger getInstance(Context context) {
        if (instance == null) {
            synchronized (instanceLock) {
                if (instance == null) {
                    instance = new AndroidLogger(context);
                }
            }
        }
        return instance;
    }
    
    // Configuration methods
    public AndroidLogger setMinimumLevel(LogLevel level) {
        this.minimumLevel = level;
        return this;
    }
    
    public AndroidLogger setEnableFileLogging(boolean enable) {
        this.enableFileLogging = enable;
        return this;
    }
    
    public AndroidLogger setEnableConsoleOutput(boolean enable) {
        this.enableConsoleOutput = enable;
        return this;
    }
    
    public AndroidLogger setMaxLogFileSizeMB(int sizeMB) {
        this.maxLogFileSizeMB = sizeMB;
        return this;
    }
    
    public AndroidLogger setMaxLogFileCount(int count) {
        this.maxLogFileCount = count;
        return this;
    }
    
    // Public logging methods
    public void logTrace(String message) {
        logTrace(message, "General");
    }
    
    public void logTrace(String message, String category) {
        log(LogLevel.TRACE, message, category, null, getCallerInfo());
    }
    
    public void logDebug(String message) {
        logDebug(message, "General");
    }
    
    public void logDebug(String message, String category) {
        log(LogLevel.DEBUG, message, category, null, getCallerInfo());
    }
    
    public void logInfo(String message) {
        logInfo(message, "General");
    }
    
    public void logInfo(String message, String category) {
        log(LogLevel.INFO, message, category, null, getCallerInfo());
    }
    
    public void logWarning(String message) {
        logWarning(message, "General");
    }
    
    public void logWarning(String message, String category) {
        log(LogLevel.WARN, message, category, null, getCallerInfo());
    }
    
    public void logError(String message) {
        logError(message, "General", null);
    }
    
    public void logError(String message, String category) {
        logError(message, category, null);
    }
    
    public void logError(String message, Throwable throwable) {
        logError(message, "General", throwable);
    }
    
    public void logError(String message, String category, Throwable throwable) {
        log(LogLevel.ERROR, message, category, throwable, getCallerInfo());
    }
    
    public void logCritical(String message) {
        logCritical(message, "General", null);
    }
    
    public void logCritical(String message, String category) {
        logCritical(message, category, null);
    }
    
    public void logCritical(String message, String category, Throwable throwable) {
        log(LogLevel.CRITICAL, message, category, throwable, getCallerInfo());
    }
    
    // Specialized logging methods
    public void logAudit(String action, String details, String userId) {
        String auditMessage = String.format("AUDIT: %s | User: %s | Details: %s", 
                action, userId != null ? userId : "SYSTEM", details);
        log(LogLevel.INFO, auditMessage, "AUDIT", null, getCallerInfo());
    }
    
    public void logSecurity(String event, String details, String userId, String ipAddress) {
        String securityMessage = String.format("SECURITY: %s | User: %s | IP: %s | Details: %s",
                event, userId != null ? userId : "UNKNOWN", 
                ipAddress != null ? ipAddress : "UNKNOWN", details);
        log(LogLevel.WARN, securityMessage, "SECURITY", null, getCallerInfo());
    }
    
    public void logPerformance(String operation, long durationMs, String details) {
        String perfMessage = String.format("PERFORMANCE: %s took %dms", operation, durationMs);
        if (details != null && !details.isEmpty()) {
            perfMessage += " | " + details;
        }
        log(LogLevel.INFO, perfMessage, "PERFORMANCE", null, getCallerInfo());
    }
    
    public void logVpnEvent(String event, String details) {
        log(LogLevel.INFO, String.format("VPN: %s | %s", event, details), "VPN", null, getCallerInfo());
    }
    
    public void logFilterEvent(String action, String url, String reason, String userId) {
        String filterMessage = String.format("FILTER: %s | URL: %s | User: %s | Reason: %s",
                action, url, userId != null ? userId : "UNKNOWN", reason);
        log(LogLevel.INFO, filterMessage, "FILTER", null, getCallerInfo());
    }
    
    // Core logging method
    private void log(LogLevel level, String message, String category, Throwable throwable, CallerInfo callerInfo) {
        if (level.getValue() < minimumLevel.getValue()) {
            return;
        }
        
        LogEntry entry = new LogEntry();
        entry.timestamp = new Date();
        entry.level = level;
        entry.message = message;
        entry.category = category != null ? category : "General";
        entry.throwable = throwable;
        entry.callerInfo = callerInfo;
        entry.threadId = Thread.currentThread().getId();
        entry.threadName = Thread.currentThread().getName();
        
        // Add to queue for async processing
        if (!logQueue.offer(entry)) {
            // Queue is full, log directly to console as fallback
            if (enableConsoleOutput) {
                logToConsole(entry);
            }
        }
    }
    
    // Background log processing
    private void processLogQueue() {
        while (isRunning) {
            try {
                LogEntry entry = logQueue.take(); // Blocking call
                processLogEntry(entry);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                break;
            } catch (Exception e) {
                // Emergency logging - directly to console
                Log.e(TAG, "Error processing log entry", e);
            }
        }
    }
    
    private void processLogEntry(LogEntry entry) {
        // Console output
        if (enableConsoleOutput) {
            logToConsole(entry);
        }
        
        // File output
        if (enableFileLogging) {
            logToFile(entry);
        }
    }
    
    // Console output
    private void logToConsole(LogEntry entry) {
        String formattedMessage = formatLogEntry(entry);
        
        switch (entry.level) {
            case TRACE:
                Log.v(entry.category, formattedMessage, entry.throwable);
                break;
            case DEBUG:
                Log.d(entry.category, formattedMessage, entry.throwable);
                break;
            case INFO:
                Log.i(entry.category, formattedMessage, entry.throwable);
                break;
            case WARN:
                Log.w(entry.category, formattedMessage, entry.throwable);
                break;
            case ERROR:
            case CRITICAL:
                Log.e(entry.category, formattedMessage, entry.throwable);
                break;
        }
    }
    
    // File output
    private void logToFile(LogEntry entry) {
        try {
            // Check if log rotation is needed
            checkAndRotateLogFile();
            
            String formattedEntry = formatLogEntry(entry);
            
            synchronized (this) {
                try (FileWriter fileWriter = new FileWriter(currentLogFile, true);
                     PrintWriter writer = new PrintWriter(fileWriter)) {
                    
                    writer.println(formattedEntry);
                    
                    if (entry.throwable != null) {
                        StringWriter sw = new StringWriter();
                        PrintWriter pw = new PrintWriter(sw);
                        entry.throwable.printStackTrace(pw);
                        writer.println("    Stack Trace: " + sw.toString());
                    }
                }
            }
        } catch (IOException e) {
            Log.e(TAG, "Failed to write to log file", e);
        }
    }
    
    // Log entry formatting
    private String formatLogEntry(LogEntry entry) {
        StringBuilder sb = new StringBuilder();
        
        sb.append(timestampFormat.format(entry.timestamp));
        sb.append(" [").append(entry.level.name()).append("]");
        sb.append(" [").append(entry.category).append("]");
        sb.append(" [T").append(entry.threadId).append("]");
        
        if (entry.callerInfo != null) {
            sb.append(" [").append(entry.callerInfo.className).append(".")
              .append(entry.callerInfo.methodName).append(":").append(entry.callerInfo.lineNumber).append("]");
        }
        
        sb.append(" ").append(entry.message);
        
        if (entry.throwable != null) {
            sb.append(" | Exception: ").append(entry.throwable.getClass().getSimpleName())
              .append(": ").append(entry.throwable.getMessage());
        }
        
        return sb.toString();
    }
    
    // Log file management
    private void createNewLogFile() {
        String fileName = "urlfilter_" + fileNameFormat.format(new Date()) + ".log";
        currentLogFile = new File(logDirectory, fileName);
        
        try {
            if (!currentLogFile.exists()) {
                currentLogFile.createNewFile();
            }
        } catch (IOException e) {
            Log.e(TAG, "Failed to create log file", e);
        }
    }
    
    private void checkAndRotateLogFile() {
        if (currentLogFile.length() > maxLogFileSizeMB * 1024 * 1024) {
            rotateLogFiles();
            createNewLogFile();
        }
    }
    
    private void rotateLogFiles() {
        try {
            File[] logFiles = logDirectory.listFiles((dir, name) -> name.startsWith("urlfilter_") && name.endsWith(".log"));
            
            if (logFiles != null && logFiles.length >= maxLogFileCount) {
                // Sort by last modified date (oldest first)
                Arrays.sort(logFiles, Comparator.comparing(File::lastModified));
                
                // Delete oldest files
                for (int i = 0; i <= logFiles.length - maxLogFileCount; i++) {
                    if (logFiles[i].delete()) {
                        Log.d(TAG, "Deleted old log file: " + logFiles[i].getName());
                    }
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "Error rotating log files", e);
        }
    }
    
    // Utility methods
    public List<File> getLogFiles() {
        List<File> logFiles = new ArrayList<>();
        File[] files = logDirectory.listFiles((dir, name) -> name.startsWith("urlfilter_") && name.endsWith(".log"));
        
        if (files != null) {
            Arrays.sort(files, (f1, f2) -> Long.compare(f2.lastModified(), f1.lastModified()));
            logFiles.addAll(Arrays.asList(files));
        }
        
        return logFiles;
    }
    
    public File getCurrentLogFile() {
        return currentLogFile;
    }
    
    public File getLogDirectory() {
        return logDirectory;
    }
    
    public LogStatistics getStatistics() {
        LogStatistics stats = new LogStatistics();
        stats.logDirectory = logDirectory.getAbsolutePath();
        stats.currentLogFile = currentLogFile.getAbsolutePath();
        stats.queuedEntries = logQueue.size();
        stats.minimumLevel = minimumLevel;
        stats.enableFileLogging = enableFileLogging;
        stats.enableConsoleOutput = enableConsoleOutput;
        
        // Calculate total log size
        File[] logFiles = logDirectory.listFiles((dir, name) -> name.startsWith("urlfilter_") && name.endsWith(".log"));
        if (logFiles != null) {
            stats.logFileCount = logFiles.length;
            stats.totalLogSizeBytes = Arrays.stream(logFiles).mapToLong(File::length).sum();
        }
        
        return stats;
    }
    
    public void flushLogs() {
        // Wait for queue to be processed
        long timeout = System.currentTimeMillis() + 5000; // 5 second timeout
        while (!logQueue.isEmpty() && System.currentTimeMillis() < timeout) {
            try {
                Thread.sleep(100);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                break;
            }
        }
    }
    
    public void shutdown() {
        logInfo("AndroidLogger shutting down", "Logger");
        isRunning = false;
        flushLogs();
        executorService.shutdown();
    }
    
    // Helper methods
    private CallerInfo getCallerInfo() {
        try {
            StackTraceElement[] stackTrace = Thread.currentThread().getStackTrace();
            // Skip getStackTrace, getCallerInfo, log, and public method calls
            for (int i = 4; i < stackTrace.length; i++) {
                StackTraceElement element = stackTrace[i];
                if (!element.getClassName().equals(AndroidLogger.class.getName())) {
                    CallerInfo info = new CallerInfo();
                    info.className = element.getClassName().substring(element.getClassName().lastIndexOf('.') + 1);
                    info.methodName = element.getMethodName();
                    info.lineNumber = element.getLineNumber();
                    return info;
                }
            }
        } catch (Exception e) {
            // Ignore
        }
        return null;
    }
    
    // Inner classes
    private static class LogEntry {
        Date timestamp;
        LogLevel level;
        String message;
        String category;
        Throwable throwable;
        CallerInfo callerInfo;
        long threadId;
        String threadName;
    }
    
    private static class CallerInfo {
        String className;
        String methodName;
        int lineNumber;
    }
    
    public static class LogStatistics {
        public String logDirectory;
        public String currentLogFile;
        public int logFileCount;
        public long totalLogSizeBytes;
        public int queuedEntries;
        public LogLevel minimumLevel;
        public boolean enableFileLogging;
        public boolean enableConsoleOutput;
        
        public String getTotalLogSizeMB() {
            return String.format(Locale.US, "%.2f MB", totalLogSizeBytes / (1024.0 * 1024.0));
        }
        
        @Override
        public String toString() {
            return String.format(Locale.US,
                    "LogStatistics{files=%d, totalSize=%s, queued=%d, level=%s, fileLogging=%s, consoleOutput=%s}",
                    logFileCount, getTotalLogSizeMB(), queuedEntries, minimumLevel,
                    enableFileLogging, enableConsoleOutput);
        }
    }
}