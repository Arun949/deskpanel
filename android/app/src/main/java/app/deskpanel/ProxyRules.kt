package app.deskpanel

/**
 * What the panel may ask the agent for, through MainActivity's proxy. Pure, so it is unit-tested. The agent checks
 * everything again; this is the second lock (PhoneBox's proxy forwarded any /agent path, Module 3).
 */
object ProxyRules {
    private val ALLOWED_GET = setOf("/api/v1/state", "/api/v1/layout") // the stream is opened natively, not by the page
    private val MEDIA = Regex("^/api/v1/media/(play-pause|next|prev)$")
    private val ACTION = Regex("^/api/v1/actions/[a-z0-9][a-z0-9-]{0,39}$")
    private val VOLUME_QUERY = Regex("^(level=(100|[1-9]?[0-9])|mute=(true|false))$")
    val IDEM = Regex("^[A-Za-z0-9-]{8,64}$")

    fun allowedGet(path: String, query: String?): Boolean = query == null && path in ALLOWED_GET

    fun allowedPost(path: String, query: String?): Boolean = when {
        MEDIA.matches(path) || ACTION.matches(path) -> query == null
        path == "/api/v1/volume" -> query != null && VOLUME_QUERY.matches(query)
        else -> false
    }
}
