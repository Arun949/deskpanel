package app.deskpanel

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ProofTest {
    // Shared vector: the same inputs give the same value in the C# agent (DeviceStore.Proof) and in Python.
    private val key = "A".repeat(43)
    private val id = "0123456789abcdef"
    private val nonce = "nonce-nonce-nonce-01"
    private val runId = "fedcba9876543210"
    private val vector = "qkFWpPVfSTRkC4LjD-1oQ6pU3IKIulXbXAhfQfx2_m8"

    @Test fun matchesTheAgentsProof() = assertEquals(vector, Proof.expected(key, id, nonce, runId))

    @Test fun aProofForAnotherRunOrNonceIsRejected() {
        assertFalse(Proof.matches(Proof.expected(key, id, nonce, "0000000000000000"), vector))
        assertFalse(Proof.matches(Proof.expected(key, id, "another-nonce-000000", runId), vector))
    }

    @Test fun missingOrForgedProofsAreRejected() {
        assertFalse(Proof.matches(vector, null))
        assertFalse(Proof.matches(vector, ""))
        assertFalse(Proof.matches(vector, vector.dropLast(1) + "x"))
        assertTrue(Proof.matches(vector, vector))
    }

    @Test fun enrolmentValuesAreValidated() {
        assertTrue(Proof.isDeviceId(id))
        assertFalse(Proof.isDeviceId("0123456789ABCDEF"))
        assertFalse(Proof.isDeviceId("0123; rm -rf /"))
        assertTrue(Proof.isKey(key))
        assertFalse(Proof.isKey(key + "A"))
        assertFalse(Proof.isKey(null))
    }
}
