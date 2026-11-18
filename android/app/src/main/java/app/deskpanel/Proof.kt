package app.deskpanel

import java.security.MessageDigest
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * The agent-identity check (pure JVM code, unit-tested against a vector shared with the C# agent).
 * The agent proves it knows this phone's key by returning HMAC(proofKey, "deskpanel-proof|v1|id|nonce|runId"),
 * where proofKey = SHA-256("deskpanel|proof|" + key). Something else listening on 127.0.0.1:8765 can't compute it.
 */
object Proof {
    private val DEVICE_ID = Regex("^[0-9a-f]{16}$")
    private val KEY = Regex("^[A-Za-z0-9_-]{43}$")

    fun isDeviceId(s: String?) = s != null && DEVICE_ID.matches(s)
    fun isKey(s: String?) = s != null && KEY.matches(s)

    fun expected(key: String, deviceId: String, nonce: String, runId: String): String {
        val proofKey = MessageDigest.getInstance("SHA-256").digest("deskpanel|proof|$key".toByteArray(Charsets.UTF_8))
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(proofKey, "HmacSHA256")) }
        return base64Url(mac.doFinal("deskpanel-proof|v1|$deviceId|$nonce|$runId".toByteArray(Charsets.UTF_8)))
    }

    /** Constant-time comparison, so timing reveals nothing about how much of a forged proof matched. */
    fun matches(expected: String, given: String?): Boolean =
        given != null && MessageDigest.isEqual(expected.toByteArray(Charsets.US_ASCII), given.toByteArray(Charsets.US_ASCII))

    fun base64Url(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
}
