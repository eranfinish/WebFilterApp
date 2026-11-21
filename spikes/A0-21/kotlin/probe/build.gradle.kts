// Spike A0.21: second app (its own uid). Makes the client-side requests: HTTP, raw TCP, UDP DNS and a WebView.
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.webfilter.spike.a021.probe"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.webfilter.spike.a021.probe"
        minSdk = 31
        targetSdk = 36
        versionCode = 1
        versionName = "0.2b-probe"
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
