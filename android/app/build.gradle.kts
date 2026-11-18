plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "app.deskpanel"
    compileSdk = 35

    defaultConfig {
        applicationId = "app.deskpanel"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "0.0.1"
    }

    buildTypes {
        release { isMinifyEnabled = false }
    }

    buildFeatures { buildConfig = true }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }

    // The panel (panel/dist, built by `npm run build`) is packaged as assets/panel/.
    sourceSets["main"].assets.srcDir(layout.buildDirectory.dir("generated/panelAssets"))
}

val panelDist = rootProject.file("../panel/dist")

val copyPanel by tasks.registering(Copy::class) {
    description = "Copies the built panel into the APK's assets."
    doFirst {
        check(panelDist.resolve("index.html").exists()) {
            "panel/dist is missing: run `npm ci && npm run build` in panel/ first"
        }
    }
    from(panelDist)
    into(layout.buildDirectory.dir("generated/panelAssets/panel"))
}

tasks.named("preBuild") { dependsOn(copyPanel) }

dependencies {
    implementation("androidx.webkit:webkit:1.12.1")
    testImplementation("junit:junit:4.13.2")
}
