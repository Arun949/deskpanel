package app.deskpanel

import java.util.Base64
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class WifiRulesTest {
    /** Shared with the agent's WifiPartsTests/WifiSetupTests: this P-256 public key has this pin (checked with openssl). */
    private val vectorSpki = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEA091UJEE1ZfeHKnki0glkTSxPNEiDF7KOos-QDq4HLuJMiDuwaXqYBaInuUwd0SYKG-eysVnHCGDhDc0X-MhVA"
    private val vectorPin = "NE6VEyqZI4eYeSx2Ggl8Mtp8B8QsLaLVULsd-Nz5W0o"

    @Test
    fun pinMatchesTheAgent() = assertEquals(vectorPin, WifiRules.pinOf(Base64.getUrlDecoder().decode(vectorSpki)))

    @Test
    fun onlyPrivateIpv4HostsAreKept() {
        assertEquals(listOf("192.168.1.20", "10.0.0.5"), WifiRules.parseHosts("192.168.1.20,8.8.8.8,10.0.0.5,192.168.1.20"))
        assertEquals(listOf("172.16.0.1"), WifiRules.parseHosts("172.16.0.1,172.32.0.1"))
        assertNull(WifiRules.parseHosts("8.8.8.8"))
        assertNull(WifiRules.parseHosts("192.168.1.256,fe80::1,localhost,"))
        assertNull(WifiRules.parseHosts(null))
        assertEquals(4, WifiRules.parseHosts("10.0.0.1,10.0.0.2,10.0.0.3,10.0.0.4,10.0.0.5")!!.size)
    }

    @Test
    fun detailsNeedEveryPartValid() {
        val ok = WifiRules.details(8766, vectorPin, "192.168.1.20")
        assertEquals(WifiDetails(8766, vectorPin, listOf("192.168.1.20")), ok)
        assertNull(WifiRules.details(80, vectorPin, "192.168.1.20"))
        assertNull(WifiRules.details(8766, "short", "192.168.1.20"))
        assertNull(WifiRules.details(8766, vectorPin, "8.8.8.8"))
        assertNull(WifiRules.details(8766, null, "192.168.1.20"))
    }
}
