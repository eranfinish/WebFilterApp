package com.webfilter.spike.a03

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
 * Part B: a minimal WebView browser under test.
 *
 * Start with: am start -n com.webfilter.spike.a03/.WebTestActivity --es url <url> [--es deny <a,b,c>]
 * `deny` is a comma-separated list of substrings; a main-frame URL containing one is blocked and the
 * local block page is shown instead (Q8). Everything is logged under tag A03WV with wall-clock ms.
 *
 * Ground truth for Q7: the page gets a script that reports history.pushState/replaceState/popstate
 * through the JS bridge (`GT`), and a 500 ms poll of WebView.getUrl() logs `POLL` on every change.
 * Those are compared with the WebViewClient callbacks in the report.
 */
class WebTestActivity : Activity() {

    private lateinit var wv: WebView
    private val handler = Handler(Looper.getMainLooper())
    private var lastPolled: String? = null
    private var denySubstrings: List<String> = emptyList()

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
        denySubstrings = (intent.getStringExtra("deny") ?: "").split(",").map { it.trim() }.filter { it.isNotEmpty() }
        wv = WebView(this)
        setContentView(wv)
        WebView.setWebContentsDebuggingEnabled(true)
        wv.settings.javaScriptEnabled = true
        wv.settings.domStorageEnabled = true
        wv.settings.javaScriptCanOpenWindowsAutomatically = false
        wv.settings.mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
        wv.addJavascriptInterface(Bridge(), "A03")
        wv.webViewClient = Client()
        handler.post(poll)
        val url = intent.getStringExtra("url") ?: "https://m.youtube.com/"
        log("START", url)
        // The app's own loadUrl does NOT reach shouldOverrideUrlLoading (seen in the Q8 run), so the
        // deny rule must be applied to the start URL here, before loading it.
        if (isDenied(url)) showBlockPage(url) else wv.loadUrl(url)
    }

    override fun onDestroy() {
        handler.removeCallbacks(poll)
        super.onDestroy()
    }

    private fun isDenied(u: String): Boolean = denySubstrings.any { u.contains(it) }

    private fun showBlockPage(blocked: String) {
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
                A03.request(decodeURIComponent('$esc'));
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

    private fun injectGroundTruth() {
        val js = """
            (function(){
              if (window.__a03) return; window.__a03 = true;
              function rep(kind){ return function(){ var r = (this._a03 = arguments);
                try { A03.gt(kind, String(location.href)); } catch(e) {}
                return null; }; }
              var ps = history.pushState, rs = history.replaceState;
              history.pushState = function(s,t,u){ var rv = ps.apply(this, arguments); try{A03.gt('pushState', String(location.href));}catch(e){} return rv; };
              history.replaceState = function(s,t,u){ var rv = rs.apply(this, arguments); try{A03.gt('replaceState', String(location.href));}catch(e){} return rv; };
              window.addEventListener('popstate', function(){ try{A03.gt('popstate', String(location.href));}catch(e){} });
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
        }

        override fun onPageStarted(view: WebView, url: String, favicon: Bitmap?) {
            log("onPageStarted", url)
        }

        override fun onPageFinished(view: WebView, url: String) {
            log("onPageFinished", url)
            injectGroundTruth()
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
        const val TAG = "A03WV"
    }
}
