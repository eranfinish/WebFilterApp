package com.webfilter.spike.a03b

import android.app.Activity
import android.graphics.Bitmap
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.webkit.JavascriptInterface
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient

/**
 * WebView browser under test (Android 17 / WebView 145).
 *
 * Start with: am start -n com.webfilter.spike.a03b/.WebTestActivity --es url <url> [--es deny <a,b,c>]
 * `deny` is a comma-separated list of substrings; a main-frame URL containing one is blocked and the local
 * block page is shown instead. Everything is logged under tag A03BWV with wall-clock ms.
 *
 * Callbacks logged: shouldOverrideUrlLoading, shouldInterceptRequest, doUpdateVisitedHistory, onPageStarted,
 * onPageFinished, plus a JS history hook (`GT`) injected in onPageStarted (NOT document-start: the androidx.webkit
 * addDocumentStartJavaScript API is not in the offline Gradle cache, so a pushState before onPageStarted is missed).
 * A 500 ms poll of WebView.getUrl() logs POLL on every change, as ground truth for SPA navigations.
 */
class WebTestActivity : Activity() {

    private lateinit var wv: WebView
    private val handler = Handler(Looper.getMainLooper())
    private var lastPolled: String? = null
    private var denySubstrings: List<String> = emptyList()
    private var blockPageShown = false
    // Optional `click` extra: a CSS selector clicked with element.click() ~1.5 s after each page finishes (Q7 SPA trigger).
    private var clickSelector: String? = null
    private var clicked = false

    private val poll = object : Runnable {
        override fun run() {
            val now = wv.url
            if (now != lastPolled) {
                log("POLL", now ?: "null")
                lastPolled = now
            }
            handler.postDelayed(this, 500)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        clickSelector = intent.getStringExtra("click")
        denySubstrings = (intent.getStringExtra("deny") ?: "").split(",").map { it.trim() }.filter { it.isNotEmpty() }
        wv = WebView(this)
        setContentView(wv)
        WebView.setWebContentsDebuggingEnabled(true)
        wv.settings.javaScriptEnabled = true
        wv.settings.domStorageEnabled = true
        wv.settings.javaScriptCanOpenWindowsAutomatically = false
        wv.settings.mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
        wv.addJavascriptInterface(Bridge(), "A03B")
        wv.webViewClient = Client()
        handler.post(poll)
        val url = intent.getStringExtra("url") ?: "https://m.youtube.com/"
        log("START", url, "deny=${denySubstrings}")
        // The app's own loadUrl does NOT reach shouldOverrideUrlLoading (seen in A0.3), so the deny rule is
        // applied to the start URL here, before loading it.
        if (isDenied(url)) showBlockPage(url) else wv.loadUrl(url)
    }

    override fun onDestroy() {
        handler.removeCallbacks(poll)
        super.onDestroy()
    }

    private fun isDenied(u: String): Boolean = denySubstrings.any { u.contains(it) }

    private fun showBlockPage(blocked: String) {
        blockPageShown = true
        val esc = Uri.encode(blocked)
        val html = """
            <!doctype html><html><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1"></head>
            <body style="font-family:sans-serif;padding:16px">
            <h2>הדף הזה חסום</h2>
            <p id="u" style="word-break:break-all;direction:ltr"></p>
            <button id="req" style="font-size:18px;padding:12px 20px">בקש גישה</button>
            <p id="st"></p>
            <script>
              document.getElementById('u').textContent = decodeURIComponent('$esc');
              document.getElementById('req').onclick = function () {
                A03B.request(decodeURIComponent('$esc'));
                document.getElementById('st').textContent = 'הבקשה נשלחה (בדיקה)';
              };
            </script></body></html>
        """.trimIndent()
        log("BLOCKPAGE", blocked)
        wv.loadDataWithBaseURL("https://blockpage.local/", html, "text/html", "utf-8", null)
    }

    private fun log(event: String, detail: String, extra: String = "") {
        Log.i(TAG, "${System.currentTimeMillis()} $event $detail $extra".trimEnd())
    }

    private fun injectHook() {
        val js = """
            (function(){
              if (window.__a03b) return; window.__a03b = true;
              var ps = history.pushState, rs = history.replaceState;
              history.pushState = function(s,t,u){ var rv = ps.apply(this, arguments); try{A03B.gt('pushState', String(location.href));}catch(e){} return rv; };
              history.replaceState = function(s,t,u){ var rv = rs.apply(this, arguments); try{A03B.gt('replaceState', String(location.href));}catch(e){} return rv; };
              window.addEventListener('popstate', function(){ try{A03B.gt('popstate', String(location.href));}catch(e){} });
            })();
        """.trimIndent()
        wv.evaluateJavascript(js, null)
    }

    private inner class Client : WebViewClient() {

        override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
            val u = request.url.toString()
            log("shouldOverrideUrlLoading", u, "main=${request.isForMainFrame} gesture=${request.hasGesture()} redirect=${request.isRedirect}")
            if (request.isForMainFrame && isDenied(u)) {
                showBlockPage(u)
                return true
            }
            return false
        }

        override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
            log("shouldInterceptRequest", request.url.toString(), "main=${request.isForMainFrame}")
            return null
        }

        override fun doUpdateVisitedHistory(view: WebView, url: String, isReload: Boolean) {
            log("doUpdateVisitedHistory", url, "reload=$isReload")
            // Only callback that sees SPA navigations on this WebView (A0.3). A block here is too late for the
            // page to avoid loading, but the block page can replace it: test whether that works for /watch.
            if (!blockPageShown && isDenied(url)) {
                log("DENY_IN_HISTORY", url)
                showBlockPage(url)
            }
        }

        override fun onPageStarted(view: WebView, url: String, favicon: Bitmap?) {
            log("onPageStarted", url)
            blockPageShown = false
            injectHook()
        }

        override fun onPageFinished(view: WebView, url: String) {
            log("onPageFinished", url)
            injectHook()
            val sel = clickSelector
            if (sel != null && !clicked) {
                clicked = true
                handler.postDelayed({
                    val js = "(function(){try{var a=document.querySelector('" + sel.replace("'", "\\'") + "'); if(!a) return 'no-element anchors=' + document.querySelectorAll('a').length; a.click(); return 'clicked ' + a.getAttribute('href');}catch(e){return 'err ' + e;}})()"
                    wv.evaluateJavascript(js) { r -> log("CLICK_RESULT", r ?: "null(js threw or returned undefined)") }
                }, 3000)
            }
        }
    }

    private inner class Bridge {
        @JavascriptInterface
        fun gt(kind: String, url: String) {
            log("GT", kind, url)
        }

        @JavascriptInterface
        fun request(url: String) {
            log("REQUEST", url)
        }
    }

    companion object {
        const val TAG = "A03BWV"
    }
}
