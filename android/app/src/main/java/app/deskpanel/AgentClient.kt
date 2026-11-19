package app.deskpanel

import java.io.ByteArrayInputStream
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.SecureRandom
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLSocketFactory
import org.json.JSONObject

/**
 * The only code that talks to the PC. It picks a route (USB first, then Wi-Fi, docs/design/WIFI.md §3.2), and before the
 * device key is sent anywhere it checks that the other end proves it is this phone's agent (Module 3). Over Wi-Fi two
 * more locks come first, in the TLS handshake: the agent's key must match the pin received over adb, and the phone
 * presents its Keystore key, which the agent registered over adb.
 */
class AgentClient(private val vault: KeyVault, private val wifi: WifiStore) {
    class Reply(val code: Int, val contentType: String?, val body: InputStream)

    @Volatile private var verifiedRunId: String? = null
    @Volatile private var route: Route? = null
    @Volatile private var lastGoodHost: String? = null
    @Volatile private var lastUsbCheck = 0L
    private var tls: Pair<String, SSLSocketFactory>? = null // (pin + client key) → factory, rebuilt when either changes
    private val random = SecureRandom()

    sealed class Check {
        data class Ok(val runId: String) : Check()
        object NotEnrolled : Check()
        object Unreachable : Check()
        object NotYourPc : Check()
    }

    /** "usb" or "wifi" while verified, else null (shown on the panel). */
    fun via(): String? = route?.toString()

    /**
     * Proof round trip on each route in order until one proves itself. Sends only the device id and a fresh nonce,
     * never the key. Something on 127.0.0.1 that can't prove itself is skipped, never trusted.
     */
    @Synchronized
    fun verify(): Check {
        val e = vault.load() ?: return Check.NotEnrolled
        val details = wifi.load()?.takeIf { WifiKey.existing() != null } // Wi-Fi only with both the details and the key
        var notYours = false
        for (r in Route.order(details, lastGoodHost)) {
            when (val c = prove(r, e)) {
                is Check.Ok -> {
                    route = r
                    verifiedRunId = c.runId
                    if (r is Route.Wifi) { lastGoodHost = r.host; lastUsbCheck = System.currentTimeMillis() }
                    return c
                }
                Check.NotYourPc -> notYours = true
                else -> {}
            }
        }
        forgetVerification()
        return if (notYours) Check.NotYourPc else Check.Unreachable
    }

    private fun prove(r: Route, e: KeyVault.Enrolment): Check {
        val nonce = Proof.base64Url(ByteArray(18).also(random::nextBytes))
        val json = try {
            val c = open(r, "/api/v1/hello?d=${e.deviceId}&n=$nonce", timeoutMs = 3000)
            try {
                if (c.responseCode != 200) return Check.NotYourPc
                JSONObject(c.inputStream.bufferedReader().use { it.readText() })
            } finally { c.disconnect() }
        } catch (ex: Exception) {
            return Check.Unreachable // includes a Wi-Fi handshake that failed (wrong agent key, or our key not accepted)
        }
        val runId = json.optString("runId")
        val proof = if (json.isNull("proof")) null else json.optString("proof")
        return if (runId.isNotEmpty() && Proof.matches(Proof.expected(e.key, e.deviceId, nonce, runId), proof)) Check.Ok(runId)
        else Check.NotYourPc
    }

    /** The run id last proven; the stream re-verifies when the agent's run id changes (it restarted). */
    fun verifiedRunId(): String? = verifiedRunId

    fun forgetVerification() {
        verifiedRunId = null
        route = null
    }

    /**
     * While on Wi-Fi, every 30 s: is the cable back? Then forget the Wi-Fi route, so the next request proves itself
     * over USB again (faster, and it charges). Returns true when the caller should reconnect.
     */
    fun usbIsBack(): Boolean {
        if (route !is Route.Wifi) return false
        val now = System.currentTimeMillis()
        if (now - lastUsbCheck < 30_000) return false
        lastUsbCheck = now
        val e = vault.load() ?: return false
        if (prove(Route.Usb, e) !is Check.Ok) return false
        forgetVerification()
        return true
    }

    /** A GET to the agent with the device key, only after a successful proof. */
    fun get(path: String, timeoutMs: Int = 4000): Reply {
        val e = vault.load() ?: return error(503, "not-enrolled")
        val r = route.takeIf { verifiedRunId != null } ?: run { if (verify() !is Check.Ok) return error(502, "agent-not-verified"); route!! }
        return try {
            val c = open(r, path, timeoutMs)
            c.setRequestProperty("Authorization", "Device ${e.deviceId}.${e.key}")
            val code = c.responseCode
            if (code == 401) forgetVerification() // key revoked, Wi-Fi turned off, or agent changed: prove again
            val body = (if (code >= 400) c.errorStream else c.inputStream) ?: ByteArrayInputStream(ByteArray(0))
            Reply(code, c.contentType, body)
        } catch (ex: Exception) {
            forgetVerification()
            error(503, "offline")
        }
    }

    /**
     * A control request. Sent once: if the connection breaks after the request went out, the result is reported as
     * unknown instead of retrying, because the computer may already have acted (PhoneBox's rule, Module 3).
     */
    fun post(pathAndQuery: String, idempotencyKey: String?): Reply {
        val e = vault.load() ?: return error(503, "not-enrolled")
        val r = route.takeIf { verifiedRunId != null } ?: run { if (verify() !is Check.Ok) return error(502, "agent-not-verified"); route!! }
        val c = try {
            open(r, pathAndQuery, 8000).apply {
                requestMethod = "POST"
                setRequestProperty("Authorization", "Device ${e.deviceId}.${e.key}")
                if (idempotencyKey != null) setRequestProperty("Idempotency-Key", idempotencyKey)
                doOutput = true
                setFixedLengthStreamingMode(0)
            }
        } catch (ex: Exception) {
            return error(503, "offline")
        }
        return try {
            c.outputStream.close()
            val code = c.responseCode
            if (code == 401) forgetVerification()
            val body = (if (code >= 400) c.errorStream else c.inputStream) ?: ByteArrayInputStream(ByteArray(0))
            Reply(code, c.contentType, body)
        } catch (ex: java.net.ConnectException) {
            forgetVerification()
            error(503, "offline") // never connected: nothing was sent
        } catch (ex: javax.net.ssl.SSLHandshakeException) {
            forgetVerification()
            error(503, "offline") // the handshake failed: no request was sent either
        } catch (ex: Exception) {
            forgetVerification()
            error(504, "result-unknown")
        }
    }

    private fun open(r: Route, path: String, timeoutMs: Int): HttpURLConnection {
        val (url, connectMs) = when (r) {
            Route.Usb -> URL(BASE + path) to 800
            is Route.Wifi -> URL("https://${r.host}:${r.port}$path") to 2500 // host: a checked private IPv4 address
        }
        return (url.openConnection() as HttpURLConnection).apply {
            if (this is HttpsURLConnection) {
                sslSocketFactory = wifiSocketFactory() ?: throw IllegalStateException("Wi-Fi not set up")
                hostnameVerifier = HostnameVerifier { _, _ -> true } // the pinned key is the identity, not a name
            }
            instanceFollowRedirects = false
            connectTimeout = connectMs
            readTimeout = timeoutMs
            useCaches = false
            requestMethod = "GET"
        }
    }

    @Synchronized
    private fun wifiSocketFactory(): SSLSocketFactory? {
        val details = wifi.load() ?: return null
        val (key, certificate) = WifiKey.existing() ?: return null
        val id = details.agentPin + "|" + WifiRules.pinOf(certificate.publicKey.encoded)
        tls?.let { (cached, factory) -> if (cached == id) return factory }
        return WifiTls.socketFactory(details.agentPin, key, certificate).also { tls = id to it }
    }

    private fun error(code: Int, what: String) =
        Reply(code, "application/json", ByteArrayInputStream("""{"error":"$what"}""".toByteArray()))

    companion object {
        const val BASE = "http://127.0.0.1:8765"
    }
}
