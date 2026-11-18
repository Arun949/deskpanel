package app.deskpanel

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * The device key, encrypted with an AES-GCM key that lives in the Android Keystore and can't be exported.
 * (PhoneBox kept its Wi-Fi token in plain SharedPreferences, Module 4.) Backups are disabled in the manifest.
 */
class KeyVault(context: Context) {
    data class Enrolment(val deviceId: String, val key: String)

    private val prefs = context.applicationContext.getSharedPreferences("vault", Context.MODE_PRIVATE)

    @Synchronized
    fun load(): Enrolment? {
        val id = prefs.getString("id", null) ?: return null
        val blob = prefs.getString("key", null) ?: return null
        return try {
            val raw = Base64.decode(blob, Base64.NO_WRAP)
            val cipher = Cipher.getInstance(TRANSFORM)
            cipher.init(Cipher.DECRYPT_MODE, secret(), GCMParameterSpec(128, raw, 0, IV_BYTES))
            val key = String(cipher.doFinal(raw, IV_BYTES, raw.size - IV_BYTES), Charsets.US_ASCII)
            if (Proof.isDeviceId(id) && Proof.isKey(key)) Enrolment(id, key) else null
        } catch (e: Exception) {
            null // Keystore key lost (e.g. lock screen reset): the PC will notice and enrol again
        }
    }

    @Synchronized
    fun store(deviceId: String, key: String) {
        require(Proof.isDeviceId(deviceId) && Proof.isKey(key)) { "bad enrolment" }
        val cipher = Cipher.getInstance(TRANSFORM)
        cipher.init(Cipher.ENCRYPT_MODE, secret())
        val blob = cipher.iv + cipher.doFinal(key.toByteArray(Charsets.US_ASCII))
        check(cipher.iv.size == IV_BYTES)
        prefs.edit().putString("id", deviceId).putString("key", Base64.encodeToString(blob, Base64.NO_WRAP)).commit()
    }

    private fun secret(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getEntry(ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    private companion object {
        const val ALIAS = "deskpanel-vault"
        const val TRANSFORM = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
    }
}
