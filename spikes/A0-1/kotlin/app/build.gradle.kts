// Spike A0.1: minimal Device Owner test app (AGP 8.9.1 needs Gradle 8.11.1).
// compileSdk 36 = AGP 8.9.1's tested maximum. The installed platform is "android-37.0", which AGP 8.9.1 does not resolve.
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.webfilter.spike.a01"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.webfilter.spike.a01"
        // The emulator under test is Android 12 (API 31). Keep both bounds at 31 so the spike measures that API level.
        minSdk = 31
        targetSdk = 31
        versionCode = 1
        versionName = "0.1-spike"
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

dependencies {
    testImplementation(kotlin("test"))
    testImplementation(kotlin("test-junit"))
    testImplementation("junit:junit:4.13.2")
}
