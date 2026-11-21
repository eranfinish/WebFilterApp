// Spike A0.3b: DPC + WebView test app on Android 17 (API 37). compileSdk 36 (AGP 8.9.1 maximum, see A0.1 report).
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.webfilter.spike.a03b"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.webfilter.spike.a03b"
        minSdk = 31
        // Target the device under test (API 37). A0.3 used 31, which changes WebView/Chrome behaviour on new platforms.
        targetSdk = 36
        versionCode = 1
        versionName = "0.3b-spike"
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
