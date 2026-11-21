package com.webfilter.spike.a020.probe

import android.app.Activity
import android.os.Bundle
import android.util.Log
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient

/** Loads one URL in a WebView (this app's uid). Start: am start -n com.webfilter.spike.a020.probe/.WebProbeActivity --es url <url> */
class WebProbeActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val url = intent.getStringExtra("url") ?: "https://example.org/"
        val wv = WebView(this)
        wv.settings.javaScriptEnabled = false
        wv.webViewClient = object : WebViewClient() {
            override fun onPageFinished(view: WebView, u: String) {
                Log.i(ProbeReceiver.TAG, "WEBVIEW finished url=$u")
            }

            override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                if (request.isForMainFrame) {
                    Log.i(ProbeReceiver.TAG, "WEBVIEW error url=${request.url} code=${error.errorCode} desc=${error.description}")
                }
            }
        }
        setContentView(wv)
        wv.loadUrl(url)
    }
}
