// Spike A0.3: minimal DPC + WebView test app. compileSdk 36 (AGP 8.9.1 maximum, see A0.1 report).
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.webfilter.spike.a03"
    compileSdk = 36
    // Pinned from the first build: A0.1 saw AGP install build-tools 35 before the pin.
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.webfilter.spike.a03"
        // The emulator under test is Android 12 (API 31).
        minSdk = 31
        targetSdk = 31
        versionCode = 1
        versionName = "0.3-spike"
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    jvmToolchain(17)
}
