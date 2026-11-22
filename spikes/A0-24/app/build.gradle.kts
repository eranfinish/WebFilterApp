// Spike A0.24: minimal Device Owner test app for API 37.
// compileSdk 36 and buildToolsVersion 36.0.0 are the versions A0.1 verified with AGP 8.9.1 (API 37 platform is not supported by AGP 8.9.1).
// targetSdk 36 and minSdk 31: the app must install on API 37; no API 37-only calls are used.
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.webfilter.spike.a24"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.webfilter.spike.a24"
        minSdk = 31
        targetSdk = 36
        versionCode = 1
        versionName = "0.24-spike"
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
