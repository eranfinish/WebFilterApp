// Spike A0.2: the second app (own uid, not an owner). It makes DNS, HTTP, TCP and WebView requests for the tests.
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.webfilter.spike.a02.probe"
    compileSdk = 36
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "com.webfilter.spike.a02.probe"
        minSdk = 31
        targetSdk = 36
        versionCode = 1
        versionName = "0.2-probe"
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
