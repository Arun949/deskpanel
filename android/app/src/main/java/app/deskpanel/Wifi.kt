package app.deskpanel

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.cert.X509Certificate

/** What the phone needs to reach the agent over Wi-Fi, as handed over by adb (docs/design/WIFI.md §3.1). */
data class WifiDetails(val port: Int, val agentPin: String, val hosts: List<String>)

/** Checks on everything adb hands over (pure JVM code, unit-tested; the agent's C# shares the pin vector). */
object WifiRules {
    private val PIN = Regex("^[A-Za-z0-9_-]{43}$")
    private val IPV4 = Regex("^(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(\\.(25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3}$")

    fun isPin(s: String?) = s != null && PIN.matches(s)

    fun isPort(p: Int) = p in 1024..65535

    /** "a,b,c": at most 4 private IPv4 addresses (10/8, 172.16/12, 192.168/16), or null if none is valid. */
    fun parseHosts(s: String?): List<String>? {
        val hosts = s.orEmpty().split(',').map { it.trim() }.filter { IPV4.matches(it) && isPrivate(it) }.distinct().take(4)
        return hosts.ifEmpty { null }
    }

    private fun isPrivate(ip: String): Boolean {
        val (a, b) = ip.split('.').map { it.toInt() }
        return a == 10 || (a == 172 && b in 16..31) || (a == 192 && b == 168)
    }

    fun details(port: Int, pin: String?, hosts: String?): WifiDetails? {
        val parsed = parseHosts(hosts)
        return if (isPort(port) && isPin(pin) && parsed != null) WifiDetails(port, pin!!, parsed) else null
    }

    /** base64url(SHA-256(SubjectPublicKeyInfo)): the same pin the agent computes (WifiIdentity.SpkiPin). */
    fun pinOf(subjectPublicKeyInfo: ByteArray): String =
        Proof.base64Url(MessageDigest.getInstance("SHA-256").digest(subjectPublicKeyInfo))
}

/** The Wi-Fi details, in the app's private preferences (not secret: a port, addresses, and the agent's public-key pin). */
class WifiStore(context: Context) {
    private val prefs = context.applicationContext.getSharedPreferences("wifi", Context.MODE_PRIVATE)

    @Synchronized
    fun load(): WifiDetails? = WifiRules.details(prefs.getInt("port", 0), prefs.getString("pin", null), prefs.getString("hosts", null))

    @Synchronized
    fun save(d: WifiDetails) {
        prefs.edit().putInt("port", d.port).putString("pin", d.agentPin).putString("hosts", d.hosts.joinToString(",")).commit()
    }

    @Synchronized
    fun clear() { prefs.edit().clear().commit() }
}

/**
 * The phone's Wi-Fi client key: EC P-256, generated inside the Android Keystore and never exportable (W0 showed it works
 * as a TLS client certificate). The agent knows only its public-key hash, registered over adb.
 */
object WifiKey {
    private const val ALIAS = "deskpanel-wifi-client"

    @Synchronized
    fun certificate(): X509Certificate {
        val ks = keyStore()
        if (!ks.containsAlias(ALIAS)) {
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
                initialize(
                    KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                        .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
                        .build()
                )
            }.generateKeyPair()
        }
        return ks.getCertificate(ALIAS) as X509Certificate
    }

    fun privateKey(): PrivateKey? = keyStore().getKey(ALIAS, null) as? PrivateKey

    @Synchronized
    fun delete() { keyStore().apply { if (containsAlias(ALIAS)) deleteEntry(ALIAS) } }

    private fun keyStore() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
}
