// Spike A0.2: root build. :app is the DPC + DNS VpnService; :probe is a second app (own uid) that makes the test requests.
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

rootProject.name = "a0-2-dns-vpn"
include(":app")
include(":probe")
