package app.deskpanel

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ProxyRulesTest {
    @Test fun readsAreLimitedToStateAndLayout() {
        assertTrue(ProxyRules.allowedGet("/api/v1/state", null))
        assertTrue(ProxyRules.allowedGet("/api/v1/layout", null))
        assertFalse(ProxyRules.allowedGet("/api/v1/stream", null))       // native only
        assertFalse(ProxyRules.allowedGet("/api/v1/state", "x=1"))
        assertFalse(ProxyRules.allowedGet("/admin/api/devices", null))
    }

    @Test fun controlsHaveExactShapes() {
        assertTrue(ProxyRules.allowedPost("/api/v1/media/next", null))
        assertTrue(ProxyRules.allowedPost("/api/v1/actions/open-vscode", null))
        assertTrue(ProxyRules.allowedPost("/api/v1/volume", "level=0"))
        assertTrue(ProxyRules.allowedPost("/api/v1/volume", "level=100"))
        assertTrue(ProxyRules.allowedPost("/api/v1/volume", "mute=true"))
    }

    @Test fun everythingElseIsRefused() {
        assertFalse(ProxyRules.allowedPost("/api/v1/media/shutdown", null))
        assertFalse(ProxyRules.allowedPost("/api/v1/media/next", "x=1"))
        assertFalse(ProxyRules.allowedPost("/api/v1/actions/../admin", null))
        assertFalse(ProxyRules.allowedPost("/api/v1/actions/Open", null))
        assertFalse(ProxyRules.allowedPost("/api/v1/volume", "level=101"))
        assertFalse(ProxyRules.allowedPost("/api/v1/volume", "level=50&mute=true"))
        assertFalse(ProxyRules.allowedPost("/api/v1/volume", null))
        assertFalse(ProxyRules.allowedPost("/admin/api/actions", null))
        assertFalse(ProxyRules.allowedPost("/api/v1/state", null))
    }

    @Test fun idempotencyKeysAreValidated() {
        assertTrue(ProxyRules.IDEM.matches("0b3c1a2e-5d6f-4a7b-8c9d-0e1f2a3b4c5d"))
        assertFalse(ProxyRules.IDEM.matches("short"))
        assertFalse(ProxyRules.IDEM.matches("bad key with spaces"))
    }
}
