// Spike A0.2: root build. Plugin versions are pinned here, modules apply them (same versions as A0.1, so the Gradle cache is reused).
plugins {
    id("com.android.application") version "8.9.1" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
}
