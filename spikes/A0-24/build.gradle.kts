// Spike A0.24: root build. Same toolchain as A0.22 (AGP 8.9.1 needs Gradle 8.11.1), so the cache is reused.
plugins {
    id("com.android.application") version "8.9.1" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
}
