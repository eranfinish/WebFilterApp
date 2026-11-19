// Spike A0.7: pure JVM port of WebFilter.Core's UrlNormalizer, checked against the C# corpus.
// Throwaway spike code. A1 writes the real :core module and reuses only the findings.
plugins {
    kotlin("jvm") version "2.0.21"
}

repositories {
    mavenCentral()
}

dependencies {
    // ICU4J stands in for android.icu.text.IDNA on the JVM. The device uses android.icu (API 24+).
    implementation("com.ibm.icu:icu4j:75.1")
    // Reads the corpus JSON in tests only.
    testImplementation("org.json:json:20240303")
    testImplementation(kotlin("test"))
}

tasks.test {
    useJUnitPlatform()
}
