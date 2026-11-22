// Spike A0.25: throwaway Device Owner + Profile Owner test app. Not reused by the real agent (A8).
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

rootProject.name = "a0-25-child-regular-account"
include(":app")
