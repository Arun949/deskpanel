package app.deskpanel.w0spike

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.net.Socket
import java.net.URL
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.Principal
import java.security.PrivateKey
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLException
import javax.net.ssl.X509KeyManager
import javax.net.ssl.X509TrustManager
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import org.junit.runner.RunWith

/**
 * W0: over Wi-Fi, can the phone (1) present a client key that lives in the Android Keystore and can't be exported,
 * and (2) accept only the agent whose public key matches a pin? Arguments: host, port, pin.
 */
@RunWith(AndroidJUnit4::class)
class WifiSpikeTest {
    private val args = InstrumentationRegistry.getArguments()
    private val url = "https://${args.getString("host")}:${args.getString("port", "8766")}/spike"
    private val pin = args.getString("pin")!!

    private fun spki(encoded: ByteArray) =
        Base64.encodeToString(MessageDigest.getInstance("SHA-256").digest(encoded), Base64.NO_WRAP or Base64.NO_PADDING or Base64.URL_SAFE)

    /** An EC P-256 key generated inside the Keystore (never exportable); Android makes a self-signed cert for it. */
    private fun clientKey(): Pair<PrivateKey, X509Certificate> {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        if (!ks.containsAlias(ALIAS)) {
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
                initialize(
                    KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                        .setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_NONE)
                        .build()
                )
            }.generateKeyPair()
        }
        return ks.getKey(ALIAS, null) as PrivateKey to ks.getCertificate(ALIAS) as X509Certificate
    }

    private fun keyManager(key: PrivateKey, cert: X509Certificate) = object : X509KeyManager {
        override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) = ALIAS
        override fun getCertificateChain(alias: String?) = arrayOf(cert)
        override fun getPrivateKey(alias: String?) = key
        override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(ALIAS)
        override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?) = null
        override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?) = null
    }

    /** Accepts exactly one server key: no certificate authorities, no names. */
    private fun pinned(expected: String) = object : X509TrustManager {
        override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String?) {
            val got = spki(chain[0].publicKey.encoded)
            if (got != expected) throw CertificateException("server key $got is not the pinned $expected")
        }
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException()
        override fun getAcceptedIssuers() = emptyArray<X509Certificate>()
    }

    private fun get(withClientKey: Boolean, expectedPin: String): JSONObject {
        val ctx = SSLContext.getInstance("TLS")
        val (key, cert) = clientKey()
        ctx.init(if (withClientKey) arrayOf(keyManager(key, cert)) else null, arrayOf(pinned(expectedPin)), null)
        val c = URL(url).openConnection() as HttpsURLConnection
        c.sslSocketFactory = ctx.socketFactory
        c.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true } // the pin is the identity
        c.connectTimeout = 5000
        c.readTimeout = 5000
        try {
            assertEquals(200, c.responseCode)
            return JSONObject(c.inputStream.bufferedReader().readText())
        } finally { c.disconnect() }
    }

    @Test
    fun keystoreClientKeyAndPinnedServerWork() {
        val (_, cert) = clientKey()
        val mine = spki(cert.publicKey.encoded)
        val r = get(withClientKey = true, expectedPin = pin)
        Log.i(TAG, "OK $r (my key $mine)")
        assertEquals("the agent identified this phone by its Keystore key", mine, r.getString("client"))
        // the private key really is hardware/Keystore-held and not exportable
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        assertEquals(null, (ks.getKey(ALIAS, null) as PrivateKey).encoded)
    }

    @Test
    fun aWrongPinIsRefusedBeforeAnythingIsSent() {
        try {
            get(withClientKey = true, expectedPin = "A".repeat(43))
            fail("connected to a server whose key is not the pin")
        } catch (e: SSLException) {
            Log.i(TAG, "wrong pin refused: $e")
        }
    }

    @Test
    fun withoutAClientKeyTheAgentRefuses() {
        try {
            get(withClientKey = false, expectedPin = pin)
            fail("the agent accepted a phone without a client key")
        } catch (e: Exception) {
            assertTrue("$e", e is SSLException || e is java.io.IOException)
            Log.i(TAG, "no client key refused: $e")
        }
    }

    private companion object {
        const val ALIAS = "w0-spike-client"
        const val TAG = "W0SPIKE"
    }
}
