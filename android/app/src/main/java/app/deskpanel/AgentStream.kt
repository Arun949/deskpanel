package app.deskpanel

import org.json.JSONObject

/**
 * Holds one /api/v1/stream connection and hands every event to [deliver] ("open", "close", "state").
 * A generation counter makes sure a thread from an earlier start() never delivers after stop() (PhoneBox's pattern).
 * When the agent's runId changes (restart), the proof is checked again before continuing.
 */
class AgentStream(private val client: AgentClient, private val deliver: (event: String, data: String) -> Unit) {
    private val gate = Any()
    @Volatile private var generation = 0
    @Volatile private var running = false
    private var thread: Thread? = null

    fun start() {
        synchronized(gate) {
            if (running) return
            running = true
            val gen = ++generation
            thread = Thread({ loop(gen) }, "agent-stream").apply { isDaemon = true; start() }
        }
    }

    fun stop() {
        val t = synchronized(gate) {
            running = false
            ++generation
            thread.also { thread = null }
        }
        t?.interrupt()
    }

    private fun alive(gen: Int) = running && gen == generation

    private fun dispatch(gen: Int, event: String, data: String) = synchronized(gate) {
        if (alive(gen)) deliver(event, data)
    }

    private fun loop(gen: Int) {
        while (alive(gen)) {
            var opened = false
            try {
                val reply = client.get("/api/v1/stream", timeoutMs = 25_000) // a full state arrives every 10 s
                if (reply.code == 200) {
                    opened = true
                    dispatch(gen, "open", JSONObject().put("via", client.via() ?: "usb").toString())
                    reply.body.bufferedReader(Charsets.UTF_8).use { r ->
                        var event = "message"
                        val data = StringBuilder()
                        while (alive(gen)) {
                            val line = r.readLine() ?: break
                            when {
                                line.isEmpty() -> {
                                    if (data.isNotEmpty()) {
                                        if (event == "state" && restarted(data.toString())) break
                                        dispatch(gen, event, data.toString())
                                        if (client.usbIsBack()) break // on Wi-Fi and the cable is back: reconnect over USB
                                    }
                                    event = "message"; data.setLength(0)
                                }
                                line.startsWith("event:") -> event = line.substring(6).trim()
                                line.startsWith("data:") -> data.append(line.substring(5).trimStart())
                            }
                        }
                    }
                } else {
                    reply.body.close()
                }
            } catch (_: Exception) {
            }
            if (opened) dispatch(gen, "close", "{}")
            try { Thread.sleep(if (opened) 500L else 3000L) } catch (_: InterruptedException) { return }
        }
    }

    /** A message from a different agent run than the one proven: drop the connection and prove again. */
    private fun restarted(json: String): Boolean {
        val runId = try { JSONObject(json).optString("runId") } catch (_: Exception) { return true }
        if (runId == client.verifiedRunId()) return false
        client.forgetVerification()
        return true
    }
}
