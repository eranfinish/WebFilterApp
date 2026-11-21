// Spike A0.22: minimal Device Owner / Profile Owner test app.
// compileSdk 36 and buildToolsVersion 36.0.0 are the versions A0.1 verified with AGP 8.9.1.
// minSdk and targetSdk stay at 31: the emulator under test is Android 12 (API 31), the same as the real tablet.
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.webfilter.spike.a19"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.webfilter.spike.a19"
        minSdk = 31
        targetSdk = 31
        versionCode = 1
        versionName = "0.19-spike"
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
