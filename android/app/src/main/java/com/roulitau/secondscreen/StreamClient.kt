package com.roulitau.secondscreen

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Se connecte au PC, décode le flux H.264 sur la Surface et envoie les gestes.
 *
 * Protocole : [type:1 octet][longueur:4 octets big-endian][payload]
 *   PC -> tablette : 0x01 INFO (JSON w,h,fps), 0x02 VIDEO (une image H.264 Annex B)
 *   tablette -> PC : 0x10 HELLO (JSON), 0x11 TOUCH (JSON)
 */
class StreamClient(
    private val host: String,
    private val port: Int,
    private val pin: String,
    private val surface: Surface,
    private val screenW: Int,
    private val screenH: Int,
    private val onInfo: (Int, Int) -> Unit,
    private val onClosed: (String) -> Unit
) : Thread("stream-client") {

    companion object {
        const val T_INFO = 0x01
        const val T_VIDEO = 0x02
        const val T_HELLO = 0x10
        const val T_TOUCH = 0x11

        private fun frame(type: Int, payload: ByteArray): ByteArray =
            ByteBuffer.allocate(5 + payload.size)
                .put(type.toByte())
                .putInt(payload.size)
                .put(payload)
                .array()
    }

    @Volatile private var running = true
    @Volatile private var closedByUser = false
    @Volatile private var socket: Socket? = null
    private val outQueue = LinkedBlockingQueue<ByteArray>()
    private var codec: MediaCodec? = null
    private var drainThread: Thread? = null

    fun sendTouch(o: JSONObject) {
        outQueue.offer(frame(T_TOUCH, o.toString().toByteArray(Charsets.UTF_8)))
    }

    /** Arrêt demandé par l'utilisateur (pas de message d'erreur). */
    fun close() {
        closedByUser = true
        running = false
        try { socket?.close() } catch (_: Exception) {}
        interrupt()
    }

    override fun run() {
        var reason = "Déconnecté"
        try {
            val s = Socket()
            s.tcpNoDelay = true
            s.connect(InetSocketAddress(host, port), 5000)
            socket = s
            val input = DataInputStream(BufferedInputStream(s.getInputStream(), 1 shl 16))
            val output = s.getOutputStream()

            val hello = JSONObject().put("v", 1).put("w", screenW).put("h", screenH).put("pin", pin)
            output.write(frame(T_HELLO, hello.toString().toByteArray(Charsets.UTF_8)))
            output.flush()

            Thread({
                try {
                    while (running) {
                        val msg = outQueue.poll(200, TimeUnit.MILLISECONDS) ?: continue
                        output.write(msg)
                        output.flush()
                    }
                } catch (_: Exception) {
                }
            }, "stream-writer").apply { isDaemon = true }.start()

            while (running) {
                val type = input.readUnsignedByte()
                val len = input.readInt()
                if (len < 0 || len > 16_000_000) throw IOException("Paquet invalide")
                val data = ByteArray(len)
                input.readFully(data)
                when (type) {
                    T_INFO -> startDecoder(JSONObject(String(data, Charsets.UTF_8)))
                    T_VIDEO -> feed(data)
                }
            }
        } catch (e: Exception) {
            if (!closedByUser) reason = e.message ?: e.javaClass.simpleName
        } finally {
            running = false
            cleanup()
            if (!closedByUser) onClosed(reason)
        }
    }

    private fun startDecoder(info: JSONObject) {
        if (info.has("error")) throw IOException(info.getString("error"))
        val w = info.getInt("w")
        val h = info.getInt("h")
        onInfo(w, h)

        val fmt = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, w, h)
        fmt.setInteger(MediaFormat.KEY_PRIORITY, 0) // temps réel
        if (Build.VERSION.SDK_INT >= 30) {
            fmt.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        }
        val c = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        c.configure(fmt, surface, null, 0)
        c.start()
        codec = c

        drainThread = Thread({
            val bi = MediaCodec.BufferInfo()
            try {
                while (running) {
                    val i = c.dequeueOutputBuffer(bi, 10_000)
                    if (i >= 0) c.releaseOutputBuffer(i, true) // affiche tout de suite
                }
            } catch (_: Exception) {
            }
        }, "stream-drain").apply { isDaemon = true; start() }
    }

    private fun feed(data: ByteArray) {
        val c = codec ?: return
        val idx = c.dequeueInputBuffer(20_000)
        if (idx < 0) return // décodeur en retard : on saute cette image
        val buf = c.getInputBuffer(idx) ?: return
        buf.clear()
        if (data.size > buf.capacity()) {
            c.queueInputBuffer(idx, 0, 0, 0, 0)
            return
        }
        buf.put(data)
        c.queueInputBuffer(idx, 0, data.size, System.nanoTime() / 1000, 0)
    }

    private fun cleanup() {
        try { drainThread?.join(500) } catch (_: Exception) {}
        try { codec?.stop() } catch (_: Exception) {}
        try { codec?.release() } catch (_: Exception) {}
        codec = null
        try { socket?.close() } catch (_: Exception) {}
    }
}
