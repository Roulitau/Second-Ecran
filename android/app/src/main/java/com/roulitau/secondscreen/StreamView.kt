package com.roulitau.secondscreen

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.ViewConfiguration
import org.json.JSONObject
import kotlin.math.hypot

/**
 * Affiche la vidéo (en gardant le ratio) et traduit les gestes en souris :
 *   toucher = clic gauche, glisser = maintenir + déplacer,
 *   appui long = clic droit, deux doigts = défilement.
 */
class StreamView(
    context: Context,
    private val host: String,
    private val port: Int,
    private val pin: String,
    private val onClosed: (String) -> Unit
) : SurfaceView(context), SurfaceHolder.Callback {

    private var client: StreamClient? = null
    private var videoW = 0
    private var videoH = 0
    private val ui = Handler(Looper.getMainLooper())

    private val slop = ViewConfiguration.get(context).scaledTouchSlop.toFloat()
    private val longPressMs = 450L

    private var downX = 0f
    private var downY = 0f
    private var dragging = false
    private var longFired = false
    private var multi = false
    private var lastCentroidY = 0f

    private val longPress = Runnable {
        if (!dragging && !multi) {
            longFired = true
            performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
            send("rclick", downX, downY)
        }
    }

    init {
        holder.addCallback(this)
    }

    // ---- Surface / connexion ----------------------------------------------------------

    override fun surfaceCreated(h: SurfaceHolder) {
        val dm = resources.displayMetrics
        client = StreamClient(
            host, port, pin, h.surface, dm.widthPixels, dm.heightPixels,
            onInfo = { w, hh ->
                ui.post {
                    videoW = w
                    videoH = hh
                    requestLayout()
                }
            },
            onClosed = { reason -> ui.post { onClosed(reason) } }
        ).also { it.start() }
    }

    override fun surfaceChanged(h: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(h: SurfaceHolder) {
        client?.close()
        client = null
    }

    fun disconnect() {
        client?.close()
        client = null
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val maxW = MeasureSpec.getSize(widthMeasureSpec)
        val maxH = MeasureSpec.getSize(heightMeasureSpec)
        if (videoW <= 0 || videoH <= 0) {
            setMeasuredDimension(maxW, maxH)
            return
        }
        val scale = minOf(maxW.toFloat() / videoW, maxH.toFloat() / videoH)
        setMeasuredDimension((videoW * scale).toInt(), (videoH * scale).toInt())
    }

    // ---- Gestes -----------------------------------------------------------------------

    private fun send(action: String, x: Float, y: Float) {
        val o = JSONObject()
            .put("a", action)
            .put("x", (x / width.coerceAtLeast(1)).coerceIn(0f, 1f).toDouble())
            .put("y", (y / height.coerceAtLeast(1)).coerceIn(0f, 1f).toDouble())
        client?.sendTouch(o)
    }

    private fun sendScroll(dyPx: Float) {
        val o = JSONObject()
            .put("a", "scroll")
            .put("dy", (dyPx / height.coerceAtLeast(1)).toDouble())
        client?.sendTouch(o)
    }

    /** Position verticale moyenne des doigts, en ignorant éventuellement un doigt. */
    private fun centroidY(e: MotionEvent, skipIndex: Int = -1): Float {
        var sum = 0f
        var n = 0
        for (i in 0 until e.pointerCount) {
            if (i == skipIndex) continue
            sum += e.getY(i)
            n++
        }
        return if (n > 0) sum / n else 0f
    }

    override fun onTouchEvent(e: MotionEvent): Boolean {
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = e.x
                downY = e.y
                dragging = false
                longFired = false
                multi = false
                ui.postDelayed(longPress, longPressMs)
            }

            MotionEvent.ACTION_POINTER_DOWN -> {
                ui.removeCallbacks(longPress)
                if (dragging) {
                    send("up", e.getX(0), e.getY(0))
                    dragging = false
                }
                multi = true
                lastCentroidY = centroidY(e)
            }

            MotionEvent.ACTION_MOVE -> {
                if (multi) {
                    val cy = centroidY(e)
                    val dy = cy - lastCentroidY
                    if (dy != 0f) sendScroll(dy)
                    lastCentroidY = cy
                } else if (!longFired) {
                    if (!dragging && hypot(e.x - downX, e.y - downY) > slop) {
                        ui.removeCallbacks(longPress)
                        send("down", downX, downY)
                        dragging = true
                    }
                    if (dragging) send("move", e.x, e.y)
                }
            }

            MotionEvent.ACTION_POINTER_UP -> {
                if (multi) lastCentroidY = centroidY(e, e.actionIndex)
            }

            MotionEvent.ACTION_UP -> {
                ui.removeCallbacks(longPress)
                if (!multi && !longFired) {
                    if (dragging) send("up", e.x, e.y) else send("click", e.x, e.y)
                }
                dragging = false
                multi = false
                longFired = false
            }

            MotionEvent.ACTION_CANCEL -> {
                ui.removeCallbacks(longPress)
                if (dragging) send("up", e.x, e.y)
                dragging = false
                multi = false
                longFired = false
            }
        }
        return true
    }
}
