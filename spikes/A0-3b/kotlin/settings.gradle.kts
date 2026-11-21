// Spike A0.3b: throwaway DPC + WebView test app for Android 17 / Chrome 145. Not reused by the real agent (A8).
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "a0-3b-chrome145-webview"
include(":app")
