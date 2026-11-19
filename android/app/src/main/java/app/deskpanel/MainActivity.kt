package app.deskpanel

import android.annotation.SuppressLint
import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.view.View
import android.view.WindowManager
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.webkit.WebViewAssetLoader
import java.io.ByteArrayInputStream
import org.json.JSONObject

/**
 * A full-screen WebView showing the panel from the APK's assets on a fake HTTPS origin. The panel calls
 * /agent/api/v1/...; this activity forwards only an allow-listed set of GETs to the agent (the panel never sees the
 * key) and pushes the live stream in with evaluateJavascript. There is no JavaScript bridge at all.
 */
class MainActivity : Activity() {
    private lateinit var web: WebView
    private lateinit var client: AgentClient
    private lateinit var stream: AgentStream
    @Volatile private var destroyed = false

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        client = AgentClient(KeyVault(this), WifiStore(this))
        val assets = WebViewAssetLoader.Builder()
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()

        web = WebView(this)
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = false
            allowContentAccess = false
            setSupportMultipleWindows(false)
        }
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)
        web.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val url = request.url
                if (url.host == ASSET_HOST && url.path?.startsWith("/agent/api/") == true) return proxy(request)
                if (url.host != ASSET_HOST) return FORBIDDEN() // the panel loads nothing from anywhere else
                return assets.shouldInterceptRequest(url)?.also {
                    if (url.path?.endsWith(".js") == true) it.mimeType = "text/javascript"
                }
            }

            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest) =
                request.url.host != ASSET_HOST // never navigate away from the panel
        }
        setContentView(web)
        hideSystemUi()
        web.loadUrl("https://$ASSET_HOST/assets/panel/index.html")

        stream = AgentStream(client) { event, data ->
            val js = "window.onAgentStream && window.onAgentStream(${JSONObject.quote(event)}, ${JSONObject.quote(data)})"
            runOnUiThread { if (!destroyed) web.evaluateJavascript(js, null) }
        }
    }

    /**
     * Only the panel's own requests (header set by its fetch), and only these exact shapes. Defence in depth: the agent
     * checks all of this again (PhoneBox's proxy forwarded any path, Module 3).
     */
    private fun proxy(request: WebResourceRequest): WebResourceResponse {
        val fromPanel = request.requestHeaders.keys.any { it.equals(PANEL_HEADER, ignoreCase = true) }
        val path = request.url.encodedPath!!.removePrefix("/agent")
        val query = request.url.encodedQuery
        if (!fromPanel) return FORBIDDEN()
        val r = when (request.method) {
            "GET" -> if (ProxyRules.allowedGet(path, query)) client.get(path) else return FORBIDDEN()
            "POST" -> {
                if (!ProxyRules.allowedPost(path, query)) return FORBIDDEN()
                val idem = request.requestHeaders.entries.firstOrNull { it.key.equals("Idempotency-Key", true) }?.value
                client.post(if (query == null) path else "$path?$query", idem?.takeIf { IDEM.matches(it) })
            }
            else -> return FORBIDDEN()
        }
        val type = r.contentType?.substringBefore(';')?.trim() ?: "application/json"
        val code = if (r.code in 200..599 && r.code !in 300..399) r.code else 502
        return WebResourceResponse(type, "utf-8", code, if (code < 400) "OK" else "Error", NO_STORE, r.body)
    }

    override fun onResume() {
        super.onResume()
        stream.start()
    }

    override fun onPause() {
        stream.stop()
        super.onPause()
    }

    override fun onDestroy() {
        destroyed = true
        stream.stop()
        web.destroy()
        super.onDestroy()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemUi()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemUi() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.insetsController?.let {
                it.hide(android.view.WindowInsets.Type.systemBars())
                it.systemBarsBehavior = android.view.WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_STABLE)
        }
    }

    private companion object {
        const val ASSET_HOST = "appassets.androidplatform.net"
        const val PANEL_HEADER = "X-DeskPanel-Panel"
        val IDEM = ProxyRules.IDEM
        val NO_STORE = mapOf("Cache-Control" to "no-store")
        fun FORBIDDEN() = WebResourceResponse("text/plain", "utf-8", 403, "Forbidden", NO_STORE, ByteArrayInputStream(ByteArray(0)))
    }
}
