// Spike A0.22: root build. Plugin versions are pinned here, modules apply them.
// Same toolchain as A0.1 (AGP 8.9.1 needs Gradle 8.11.1).
plugins {
    id("com.android.application") version "8.9.1" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
}
