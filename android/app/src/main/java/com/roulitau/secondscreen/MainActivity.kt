package com.roulitau.secondscreen

import android.app.Activity
import android.content.SharedPreferences
import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView

class MainActivity : Activity() {

    private lateinit var prefs: SharedPreferences
    private var streamView: StreamView? = null
    private val handler = Handler(Looper.getMainLooper())
    private var retry: Runnable? = null
    private var resumeStream = false
    private var lastHost = "auto"
    private var lastPort = 5555
    private var lastPin = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = getSharedPreferences("cfg", MODE_PRIVATE)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemUi()
        // Connexion automatique : PC trouvé tout seul (partage de connexion USB ou adb reverse)
        showStream(prefs.getString("ip", "auto") ?: "auto",
            prefs.getString("port", "5555")?.toIntOrNull() ?: 5555,
            prefs.getString("pin", "") ?: "")
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemUi()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemUi() {
        window.decorView.systemUiVisibility = (
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            )
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density).toInt()

    // ---- Écran de connexion -----------------------------------------------------------

    private fun showConnect(message: String?) {
        cancelRetry()
        resumeStream = false
        streamView?.disconnect()
        streamView = null

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setBackgroundColor(Color.BLACK)
            setPadding(dp(32), dp(16), dp(32), dp(16))
        }
        val title = TextView(this).apply {
            text = "Second écran"
            textSize = 28f
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
        }
        val ip = EditText(this).apply {
            hint = "Adresse du PC (auto = recherche seule)"
            setText(prefs.getString("ip", "auto"))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            setTextColor(Color.WHITE)
            setHintTextColor(Color.LTGRAY)
            textSize = 20f
        }
        val port = EditText(this).apply {
            hint = "Port"
            setText(prefs.getString("port", "5555"))
            inputType = InputType.TYPE_CLASS_NUMBER
            setTextColor(Color.WHITE)
            setHintTextColor(Color.LTGRAY)
            textSize = 20f
        }
        val code = EditText(this).apply {
            hint = "Code d'accès (optionnel)"
            setText(prefs.getString("pin", ""))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            setTextColor(Color.WHITE)
            setHintTextColor(Color.LTGRAY)
            textSize = 20f
        }
        val status = TextView(this).apply {
            text = message ?: ""
            setTextColor(Color.rgb(255, 191, 0)) // ambre, lisible sur fond noir
            textSize = 16f
            gravity = Gravity.CENTER
        }
        val btn = Button(this).apply {
            text = "▶  Connecter"
            textSize = 20f
            setOnClickListener {
                val host = ip.text.toString().trim()
                val p = port.text.toString().trim().toIntOrNull()
                if (host.isEmpty() || p == null) {
                    status.text = "⚠ IP ou port invalide"
                    return@setOnClickListener
                }
                val pin = code.text.toString().trim()
                prefs.edit().putString("ip", host).putString("port", p.toString())
                    .putString("pin", pin).apply()
                showStream(host, p, pin)
            }
        }

        val lp = { LinearLayout.LayoutParams(dp(420), ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12) } }
        root.addView(title, lp())
        root.addView(ip, lp())
        root.addView(port, lp())
        root.addView(code, lp())
        root.addView(btn, lp())
        root.addView(status, lp())
        setContentView(root)
    }

    // ---- Écran de streaming -----------------------------------------------------------

    private fun cancelRetry() {
        retry?.let { handler.removeCallbacks(it) }
        retry = null
    }

    private fun isAuto(h: String) = h.isEmpty() || h == "auto" || h == "127.0.0.1"

    private fun showStream(host: String, port: Int, pin: String) {
        if (isAuto(host)) { searchThenStream(port, pin); return }
        startStream(host, port, pin, host)
    }

    private fun searchThenStream(port: Int, pin: String) {
        cancelRetry()
        lastHost = "auto"; lastPort = port; lastPin = pin
        resumeStream = true
        streamView?.disconnect(); streamView = null
        val wait = TextView(this).apply {
            text = "🔎 Recherche du PC…\n(câble branché, Partage par USB activé, serveur démarré)"
            setTextColor(Color.rgb(255, 191, 0))
            textSize = 20f
            gravity = Gravity.CENTER
            setBackgroundColor(Color.BLACK)
        }
        setContentView(wait)
        Thread {
            val found = try { Discovery.find(port) } catch (e: Exception) { null }
            handler.post {
                if (!resumeStream || lastHost != "auto") return@post
                if (found != null) startStream(found, port, pin, "auto")
                else {
                    wait.text = "⚠ PC introuvable\n⏳ Nouvelle recherche…"
                    val r = Runnable { searchThenStream(port, pin) }
                    retry = r
                    handler.postDelayed(r, 2000)
                }
            }
        }.start()
    }

    private fun startStream(host: String, port: Int, pin: String, remember: String) {
        cancelRetry()
        lastHost = remember; lastPort = port; lastPin = pin
        resumeStream = true
        val container = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }
        val wait = TextView(this).apply {
            text = "⏳ Connexion au PC…\n(câble branché, serveur démarré)"
            setTextColor(Color.rgb(255, 191, 0))
            textSize = 20f
            gravity = Gravity.CENTER
        }
        val sv = StreamView(this, host, port, pin, { wait.visibility = View.GONE }) { reason ->
            // PC en veille, câble débranché, serveur arrêté... : on réessaie tout seul
            wait.text = "⚠ $reason\n⏳ Nouvelle tentative…"
            wait.visibility = View.VISIBLE
            val r = Runnable { showStream(remember, port, pin) }
            retry = r
            handler.postDelayed(r, 2000)
        }
        container.addView(
            sv,
            FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT,
                Gravity.CENTER
            )
        )
        container.addView(
            wait,
            FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER
            )
        )
        streamView?.disconnect()
        streamView = sv
        setContentView(container)
    }

    @Suppress("DEPRECATION", "OVERRIDE_DEPRECATION")
    override fun onBackPressed() {
        if (streamView != null) showConnect(null) else super.onBackPressed()
    }

    override fun onStop() {
        super.onStop()
        // appli en arrière-plan / tablette en veille : on coupe, et on reprend au retour
        val wasStreaming = resumeStream && streamView != null
        cancelRetry()
        streamView?.disconnect()
        streamView = null
        resumeStream = wasStreaming
    }

    override fun onStart() {
        super.onStart()
        if (resumeStream && streamView == null) showStream(lastHost, lastPort, lastPin)
    }
}
