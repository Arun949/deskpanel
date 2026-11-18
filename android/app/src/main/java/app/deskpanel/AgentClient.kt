package app.deskpanel

import java.io.ByteArrayInputStream
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.SecureRandom
import org.json.JSONObject

/**
 * The only code that talks to the PC. It adds the device key and, before the key is sent anywhere, checks that the
 * server on 127.0.0.1:8765 proves it is this phone's agent (stops port squatting, Module 3).
 */
class AgentClient(private val vault: KeyVault) {
    class Reply(val code: Int, val contentType: String?, val body: InputStream)

    @Volatile private var verifiedRunId: String? = null
    private val random = SecureRandom()

    sealed class Check {
        data class Ok(val runId: String) : Check()
        object NotEnrolled : Check()
        object Unreachable : Check()
        object NotYourPc : Check()
    }

    /** Proof round trip. Sends only the device id and a fresh nonce; never the key. */
    fun verify(): Check {
        val e = vault.load() ?: return Check.NotEnrolled
        val nonce = Proof.base64Url(ByteArray(18).also(random::nextBytes))
        val json = try {
            val c = open("/api/v1/hello?d=${e.deviceId}&n=$nonce", timeoutMs = 2000)
            try {
                if (c.responseCode != 200) return Check.NotYourPc
                JSONObject(c.inputStream.bufferedReader().use { it.readText() })
            } finally { c.disconnect() }
        } catch (ex: Exception) {
            verifiedRunId = null
            return Check.Unreachable
        }
        val runId = json.optString("runId")
        val proof = if (json.isNull("proof")) null else json.optString("proof")
        if (runId.isEmpty() || !Proof.matches(Proof.expected(e.key, e.deviceId, nonce, runId), proof)) {
            verifiedRunId = null
            return Check.NotYourPc
        }
        verifiedRunId = runId
        return Check.Ok(runId)
    }

    /** The run id last proven; the stream re-verifies when the agent's run id changes (it restarted). */
    fun verifiedRunId(): String? = verifiedRunId

    fun forgetVerification() { verifiedRunId = null }

    /** A GET to the agent with the device key, only after a successful proof. */
    fun get(path: String, timeoutMs: Int = 4000): Reply {
        val e = vault.load() ?: return error(503, "not-enrolled")
        if (verifiedRunId == null && verify() !is Check.Ok) return error(502, "agent-not-verified")
        return try {
            val c = open(path, timeoutMs)
            c.setRequestProperty("Authorization", "Device ${e.deviceId}.${e.key}")
            val code = c.responseCode
            if (code == 401) verifiedRunId = null // key revoked or agent changed: prove again next time
            val body = (if (code >= 400) c.errorStream else c.inputStream) ?: ByteArrayInputStream(ByteArray(0))
            Reply(code, c.contentType, body)
        } catch (ex: Exception) {
            verifiedRunId = null
            error(503, "offline")
        }
    }

    private fun open(path: String, timeoutMs: Int): HttpURLConnection =
        (URL(BASE + path).openConnection() as HttpURLConnection).apply {
            instanceFollowRedirects = false
            connectTimeout = 800
            readTimeout = timeoutMs
            useCaches = false
            requestMethod = "GET"
        }

    private fun error(code: Int, what: String) =
        Reply(code, "application/json", ByteArrayInputStream("""{"error":"$what"}""".toByteArray()))

    companion object {
        const val BASE = "http://127.0.0.1:8765"
    }
}
