// Spike A0.21: root build. :app is the DPC + full-tunnel VpnService (own process); :probe is a second app (own uid) for the client-side checks.
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

rootProject.name = "a0-2b-full-tunnel"
include(":app")
include(":probe")
