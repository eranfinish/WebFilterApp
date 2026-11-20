// Spike A0.2: DPC + DNS-only VpnService. compileSdk 36 (AGP 8.9.1 cannot resolve android-37.0). Runs on the API 37 emulator.
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.webfilter.spike.a02"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.webfilter.spike.a02"
        minSdk = 31
        targetSdk = 36
        versionCode = 1
        versionName = "0.2-spike"
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
