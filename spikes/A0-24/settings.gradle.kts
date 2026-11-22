// Spike A0.24: throwaway Device Owner test app for the hybrid model on Android 17. Not reused by the real agent.
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

rootProject.name = "a0-24-hybrid-model"
include(":app")
