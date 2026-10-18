# URL Filter System

A comprehensive cross-platform URL filtering and parental control system for Windows and Android devices. This system provides enterprise-grade network filtering, user management, and time-based access controls with SSO authentication support.

## 🚀 Features

### Core Functionality
- **Real-time Network Filtering**: System-level traffic interception and filtering
- **Cross-Platform Support**: Windows (WFP) and Android (VPN Service) implementations  
- **YouTube Channel Filtering**: Granular control over YouTube content access
- **Time-Based Restrictions**: Daily time limits and scheduled access hours
- **User Management**: Multi-user support with role-based permissions
- **SSO Authentication**: Windows AD, Azure AD, and Google account integration

### Advanced Capabilities
- **Deep Packet Inspection**: HTTP/HTTPS traffic analysis
- **Certificate Management**: Automatic HTTPS certificate handling
- **Audit Logging**: Comprehensive activity tracking and reporting
- **Performance Monitoring**: Real-time system health and usage statistics
- **Emergency Controls**: Override codes and emergency access procedures

### User-Friendly Design
- **Intuitive Admin Interface**: Family-friendly dashboard with visual controls
- **Mobile Admin App**: Remote management capabilities
- **Quick Setup Wizard**: 5-minute configuration process
- **Smart Suggestions**: AI-powered content categorization
- **Visual Reports**: Charts and graphs instead of raw data

## 📋 Prerequisites

### Windows Requirements
- Windows 7 or later (Windows 10+ recommended)
- .NET Framework 4.7.2 or .NET 6.0+
- Administrator privileges for installation
- Npcap (optional, for enhanced packet capture)

### Android Requirements
- Android 7.0 (API level 24) or later
- VPN permission (granted during setup)
- Device admin privileges (for enterprise features)
- 50MB+ storage space

### Development Requirements
- Visual Studio 2022 or VS Code
- .NET 6.0 SDK
- Android Studio (for Android development)
- Git for version control

## 🔧 Quick Start

### Windows Installation

1. **Download and Run Installer**
   ```batch
   # Run as Administrator
   scripts\install.bat install
   ```

2. **Verify Installation**
   ```batch
   scripts\install.bat status
   ```

3. **Configure Admin Users**
   - Launch Admin Control Panel
   - Set up authentication (Windows AD/Azure AD)
   - Create user profiles and filtering rules

### Android Installation

1. **Install APK**
   ```bash
   adb install build/Android/app-release.apk
   ```

2. **Grant Permissions**
   - Enable VPN permission
   - Grant Device Admin privileges
   - Allow system overlay

3. **Initial Configuration**
   - Run setup wizard
   - Configure authentication
   - Set up user profiles

## 🛠️ Development Setup

### Build from Source

1. **Clone Repository**
   ```bash
   git clone https://github.com/your-org/WebFilterApp.git
   cd WebFilterApp
   ```

2. **Build All Components**
   ```batch
   scripts\build.bat
   ```

3. **Run Tests**
   ```batch
   scripts\dev-tools.bat test all
   ```

### Development Workflow

1. **Start Debug Mode**
   ```batch
   scripts\dev-tools.bat debug
   ```

2. **Monitor System**
   ```batch
   scripts\dev-tools.bat monitor
   ```

3. **Run Specific Tests**
   ```batch
   scripts\dev-tools.bat test filtering
   scripts\dev-tools.bat simulate youtube 120
   ```

## 📁 Project Structure

```
WebFilterApp/
├── android/                    # Android VPN application
│   ├── app/src/main/java/     # Java/Kotlin source code
│   │   ├── vpn/               # VPN service implementation
│   │   ├── admin/             # Admin interface
│   │   ├── auth/              # Authentication services
│   │   └── utils/             # Utilities and helpers
│   └── app/src/main/res/      # Android resources
├── windows/                   # Windows service and applications
│   ├── UrlFilterService/      # Core Windows service
│   ├── WFPIntegration/        # Windows Filtering Platform
│   ├── AdminControlPanel/     # WPF admin interface
│   ├── Installer/             # System installer
│   └── DevTools/              # Development utilities
├── shared/                    # Cross-platform components
│   ├── database-schema/       # SQLite database schema
│   ├── models/                # Data models (C# and Java)
│   ├── logging/               # Universal logging system
│   └── sso-integration/       # Authentication providers
├── scripts/                   # Build and management scripts
│   ├── install.bat           # Installation script
│   ├── dev-tools.bat         # Development tools
│   └── build.bat             # Build automation
└── docs/                     # Documentation
    ├── installation/         # Setup guides
    ├── configuration/        # Configuration reference
    └── troubleshooting/      # Problem resolution
```

## 🔍 Development Tools

The system includes comprehensive development and testing tools:

### Command Line Tools
```batch
# Run comprehensive tests
scripts\dev-tools.bat test all

# Start debug mode with verbose logging  
scripts\dev-tools.bat debug

# Monitor system in real-time
scripts\dev-tools.bat monitor

# Simulate different types of traffic
scripts\dev-tools.bat simulate youtube 120

# Reset system to defaults
scripts\dev-tools.bat reset all

# Export debug data
scripts\dev-tools.bat export all

# Complete system removal (for testing)
scripts\dev-tools.bat reverse
```

### Interactive Menu
```batch
# Launch interactive development menu
scripts\dev-tools.bat menu
```

### Logging System

Universal logging with multiple output targets:

**Windows (C#)**
```csharp
var logger = UniversalLogger.Instance;
logger.LogInformation("System started", "Service");
logger.LogError("Connection failed", exception, "Network");
logger.LogAudit("User login", "details", "user123");
```

**Android (Java)**
```java
AndroidLogger logger = AndroidLogger.getInstance(context);
logger.logInfo("VPN service started", "VPN");
logger.logError("Filter failed", "Filter", exception);
logger.logVpnEvent("Connection established", "details");
```

## 🧪 Testing

### Automated Tests
```batch
# Run all test suites
scripts\dev-tools.bat test all

# Test specific components
scripts\dev-tools.bat test service
scripts\dev-tools.bat test filtering  
scripts\dev-tools.bat test database
scripts\dev-tools.bat test auth
```

### Traffic Simulation
```batch
# Simulate different traffic patterns
scripts\dev-tools.bat simulate basic 60
scripts\dev-tools.bat simulate burst 30
scripts\dev-tools.bat simulate youtube 120
scripts\dev-tools.bat simulate blocked 60
```

### Performance Benchmarking
```batch
# Run performance benchmarks
scripts\dev-tools.bat benchmark
```

## 📊 Monitoring and Logging

### Real-time Monitoring
```batch
# Live system monitoring
scripts\dev-tools.bat monitor

# Live log streaming
scripts\dev-tools.bat logs live
```

### Log Analysis
```batch
# View recent logs
scripts\dev-tools.bat logs recent 100

# View error logs only  
scripts\dev-tools.bat logs errors 50

# View audit logs
scripts\dev-tools.bat logs audit 50
```

### System Status
```batch
# Check system health
scripts\install.bat status

# Detailed system information
scripts\dev-tools.bat database analyze
```

## 🔄 System Management

### Installation Management
```batch
# Install system
scripts\install.bat install

# Repair installation  
scripts\install.bat repair

# Uninstall system
scripts\install.bat uninstall

# Reset to defaults
scripts\install.bat reset
```

### Service Control
```batch
# Start/stop/restart service
net start UrlFilterService
net stop UrlFilterService
scripts\dev-tools.bat service restart
```

### Development Reset
```batch
# Reset specific components
scripts\dev-tools.bat reset database
scripts\dev-tools.bat reset config
scripts\dev-tools.bat reset logs

# Complete system reversal (destructive)
scripts\dev-tools.bat reverse
```

## 🔒 Security Features

- **Tamper Protection**: Service and configuration integrity monitoring
- **Certificate Pinning**: Secure admin communication channels
- **Encrypted Storage**: Sensitive data protection
- **Audit Trail**: Comprehensive logging of all system changes
- **Role-Based Access**: Granular permission controls
- **Emergency Procedures**: Safe system recovery options

## 📚 Database Schema

The system uses SQLite with a comprehensive schema supporting:

- **User Management**: Device users, permissions, and profiles
- **URL Filtering**: Whitelist/blacklist with pattern matching
- **YouTube Integration**: Channel-specific filtering rules  
- **Time Controls**: Daily limits and scheduled access
- **Session Tracking**: Real-time usage monitoring
- **Audit Logging**: Complete activity history
- **Authentication**: SSO sessions and admin users

Key tables include:
- `device_users` - User profiles and settings
- `url_lists` - Filtering rules and patterns
- `youtube_channels` - YouTube-specific controls
- `user_time_restrictions` - Time-based access rules
- `blocked_attempts` - Security and compliance logging
- `auth_sessions` - SSO session management

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

### Development Guidelines
- Follow existing code style and conventions
- Add comprehensive tests for new features
- Update documentation for API changes
- Use the provided development tools for testing
- Ensure cross-platform compatibility

## 📝 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🆘 Support

- **Documentation**: Check the `docs/` directory for detailed guides
- **Issues**: Report bugs and request features via GitHub Issues  
- **Development**: Use `scripts\dev-tools.bat help` for development assistance
- **Testing**: Run `scripts\dev-tools.bat test all` to validate your setup

## 🔄 Version History

### v1.0.0 (Current)
- Initial release with full Windows and Android support
- Comprehensive SSO authentication system
- Advanced YouTube filtering capabilities
- Real-time monitoring and logging
- Development tools and automation scripts

---

**Built with ❤️ for safer internet experiences**