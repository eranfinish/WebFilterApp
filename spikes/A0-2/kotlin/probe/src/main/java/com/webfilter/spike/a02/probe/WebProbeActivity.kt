package com.webfilter.spike.a02.probe

import android.app.Activity
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient

/**
 * Spike A0.2 WebView check. Loads one URL and logs main-frame errors with their WebView error codes (tag "A02P").
 *   am start -n com.webfilter.spike.a02.probe/.WebProbeActivity --es url https://blocked.example/
 * Finishes itself after 15 s.
 */
class WebProbeActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val url = intent.getStringExtra("url") ?: "about:blank"
        val wv = WebView(this)
        setContentView(wv)
        wv.webViewClient = object : WebViewClient() {
            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                if (request.isForMainFrame) {
                    Log.i(ProbeReceiver.TAG, "WEBVIEW error url=${request.url} code=${error.errorCode} desc=${error.description} t=${ProbeReceiver.now()}")
                }
            }

            override fun onPageFinished(view: WebView, finishedUrl: String?) {
                Log.i(ProbeReceiver.TAG, "WEBVIEW finished url=$finishedUrl t=${ProbeReceiver.now()}")
            }
        }
        Log.i(ProbeReceiver.TAG, "WEBVIEW load url=$url t=${ProbeReceiver.now()}")
        wv.loadUrl(url)
        Handler(Looper.getMainLooper()).postDelayed({ finish() }, 15000)
    }
}
