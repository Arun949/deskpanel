package app.deskpanel

import java.io.ByteArrayInputStream
import java.security.KeyPairGenerator
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TransportTest {
    /** A certificate around the shared vector key (pin below, checked with openssl; same key as the agent's tests). */
    private val vectorCert = """
        -----BEGIN CERTIFICATE-----
        MIIBbTCCARKgAwIBAgIBATAKBggqhkjOPQQDAjAUMRIwEAYDVQQDDAlEZXNrUGFu
        ZWwwIBcNMjYxMTE5MTgzMTI3WhgPMjEyNjEwMjYxODMxMjdaMBQxEjAQBgNVBAMM
        CURlc2tQYW5lbDBZMBMGByqGSM49AgEGCCqGSM49AwEHA0IABANPdVCRBNWX3hyp
        5ItIJZE0sTzRIgxeyjqLPkA6uBy7iTIg7sGl6mAWiJ7lMHdEmChvnsrFZxwhg4Q3
        NF/jIVSjUzBRMB0GA1UdDgQWBBSbujy3pnbpUo0D05OyZ/g+W2gZ7jAfBgNVHSME
        GDAWgBSbujy3pnbpUo0D05OyZ/g+W2gZ7jAPBgNVHRMBAf8EBTADAQH/MAoGCCqG
        SM49BAMCA0kAMEYCIQDJZFu4sAin1HXIzcfVijx1/hI5lBEgysElQOP25r2mwQIh
        APKMqMahckVVqEYxg5poW/kj5QsoMRxBEhVT7bu7fg5g
        -----END CERTIFICATE-----
    """.trimIndent()
    private val vectorPin = "NE6VEyqZI4eYeSx2Ggl8Mtp8B8QsLaLVULsd-Nz5W0o"

    private fun cert() = CertificateFactory.getInstance("X.509")
        .generateCertificate(ByteArrayInputStream(vectorCert.toByteArray())) as X509Certificate

    @Test
    fun usbComesFirstThenTheLastGoodWifiAddress() {
        val wifi = WifiDetails(8766, vectorPin, listOf("10.0.0.2", "10.0.0.3"))
        assertEquals(listOf(Route.Usb), Route.order(null, null))
        assertEquals(listOf(Route.Usb, Route.Wifi("10.0.0.2", 8766), Route.Wifi("10.0.0.3", 8766)), Route.order(wifi, null))
        assertEquals(listOf(Route.Usb, Route.Wifi("10.0.0.3", 8766), Route.Wifi("10.0.0.2", 8766)), Route.order(wifi, "10.0.0.3"))
        assertEquals(listOf(Route.Usb, Route.Wifi("10.0.0.2", 8766), Route.Wifi("10.0.0.3", 8766)), Route.order(wifi, "10.9.9.9"))
    }

    @Test
    fun theAgentWithThePinnedKeyIsAccepted() {
        PinnedTrust(vectorPin).checkServerTrusted(arrayOf(cert()), "ECDHE_ECDSA") // no exception
        assertTrue(WifiRules.pinMatches(cert().publicKey.encoded, vectorPin))
    }

    @Test(expected = CertificateException::class)
    fun anyOtherKeyIsRefused() = PinnedTrust("A".repeat(43)).checkServerTrusted(arrayOf(cert()), "ECDHE_ECDSA")

    @Test(expected = CertificateException::class)
    fun anEmptyChainIsRefused() = PinnedTrust(vectorPin).checkServerTrusted(arrayOf(), "ECDHE_ECDSA")

    @Test(expected = CertificateException::class)
    fun itNeverActsAsAServer() { PinnedTrust(vectorPin).checkClientTrusted(arrayOf(cert()), "ECDHE_ECDSA") }

    @Test
    fun aMalformedPinNeverMatches() {
        assertFalse(WifiRules.pinMatches(cert().publicKey.encoded, ""))
        assertFalse(WifiRules.pinMatches(cert().publicKey.encoded, vectorPin.dropLast(1)))
        val other = KeyPairGenerator.getInstance("EC").apply { initialize(256) }.generateKeyPair().public.encoded
        assertFalse(WifiRules.pinMatches(other, vectorPin))
    }
}
