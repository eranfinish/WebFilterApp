// Spike A0.8: Android-shaped HTTPS client (OkHttp, mutual TLS, CA pinning, SSE, pairing proof) against the real C# hub.
// Throwaway spike code. A2 writes :hub-client for real and reuses only the findings.
plugins {
    kotlin("jvm") version "2.0.21"
}

repositories {
    mavenCentral()
}

dependencies {
    // Client stack the plan names (MVP-PLAN / ANDROID-PLAN §4): OkHttp for HTTPS and SSE.
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    // CSR and test certificates. The plan uses BouncyCastle on the device too.
    implementation("org.bouncycastle:bcpkix-jdk18on:1.78.1")
    // Reads the hub's JSON responses and the proof vectors.
    implementation("org.json:json:20240303")
    testImplementation(kotlin("test"))
}

tasks.test {
    useJUnitPlatform()
    // The hub harness is started per test. Its path and the vector file are resolved from the project folder.
    testLogging { events("passed", "failed", "skipped"); exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL }
}
