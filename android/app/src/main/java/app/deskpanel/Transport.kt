package app.deskpanel

import java.net.Socket
import java.security.MessageDigest
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509KeyManager
import javax.net.ssl.X509TrustManager

/** How the app reaches the agent: the USB tunnel (adb reverse to 127.0.0.1) or Wi-Fi (docs/design/WIFI.md §3.2). */
sealed class Route {
    object Usb : Route() { override fun toString() = "usb" }
    data class Wifi(val host: String, val port: Int) : Route() { override fun toString() = "wifi" }

    companion object {
        /** USB first (faster, and it charges), then the saved Wi-Fi addresses with the last one that worked first. */
        fun order(wifi: WifiDetails?, lastGoodHost: String?): List<Route> {
            if (wifi == null) return listOf(Usb)
            val hosts = wifi.hosts.sortedByDescending { it == lastGoodHost }
            return listOf(Usb) + hosts.map { Wifi(it, wifi.port) }
        }
    }
}

/**
 * Accepts exactly one server key, the agent's, whose pin came over adb. No certificate authorities, no names, no dates:
 * the key is the identity (a fake "agent" on the network fails here, before anything is sent).
 */
class PinnedTrust(private val pin: String) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("no server certificate")
        if (!WifiRules.pinMatches(leaf.publicKey.encoded, pin)) throw CertificateException("not this phone's agent")
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException("client mode only")
    override fun getAcceptedIssuers() = emptyArray<X509Certificate>()
}

/** Offers the phone's Keystore key (never exportable) as the TLS client certificate. */
class KeystoreClientKey(private val key: PrivateKey, private val certificate: X509Certificate) : X509KeyManager {
    override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) = ALIAS
    override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(ALIAS)
    override fun getCertificateChain(alias: String?) = arrayOf(certificate)
    override fun getPrivateKey(alias: String?) = key
    override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String? = null
    override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = null

    private companion object { const val ALIAS = "deskpanel" }
}

object WifiTls {
    fun socketFactory(pin: String, key: PrivateKey, certificate: X509Certificate): SSLSocketFactory =
        SSLContext.getInstance("TLS").apply { init(arrayOf(KeystoreClientKey(key, certificate)), arrayOf(PinnedTrust(pin)), null) }.socketFactory
}

internal fun constantTimeEquals(a: String, b: String) = MessageDigest.isEqual(a.toByteArray(Charsets.US_ASCII), b.toByteArray(Charsets.US_ASCII))
