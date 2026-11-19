// Throwaway W0 spike (docs/design/WIFI.md §7): a separate app id, so the real app and its enrolment are untouched.
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "app.deskpanel.w0spike"
    compileSdk = 35
    defaultConfig {
        applicationId = "app.deskpanel.w0spike"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("junit:junit:4.13.2")
}
