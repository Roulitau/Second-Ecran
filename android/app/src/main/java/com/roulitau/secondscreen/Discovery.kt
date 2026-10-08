package com.roulitau.secondscreen

import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * Trouve le PC tout seul : d'abord 127.0.0.1 (mode débogage USB), puis on teste le port sur
 * tout le réseau local de la tablette (partage de connexion USB : le PC a une adresse du genre
 * 192.168.42.x). Les réseaux "câble" (rndis/usb/eth) sont essayés avant le Wi-Fi.
 */
object Discovery {

    private fun open(host: String, port: Int, timeoutMs: Int): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(host, port), timeoutMs); true }
    } catch (e: Exception) { false }

    fun find(port: Int): String? {
        if (open("127.0.0.1", port, 400)) return "127.0.0.1"

        val subnets = ArrayList<Triple<Int, String, String>>() // priorité, préfixe a.b.c, ip locale
        for (nif in NetworkInterface.getNetworkInterfaces().toList()) {
            if (!nif.isUp || nif.isLoopback) continue
            val cable = nif.name.startsWith("rndis") || nif.name.startsWith("usb") ||
                nif.name.startsWith("eth") || nif.name.startsWith("ncm")
            for (a in nif.inetAddresses) {
                if (a !is Inet4Address || a.isLoopbackAddress) continue
                val ip = a.hostAddress ?: continue
                val p = ip.substringBeforeLast('.')
                subnets.add(Triple(if (cable) 0 else 1, p, ip))
            }
        }
        subnets.sortBy { it.first }

        for ((_, prefix, own) in subnets) {
            val pool = Executors.newFixedThreadPool(48)
            val found = AtomicReference<String?>(null)
            for (i in 1..254) {
                val host = "$prefix.$i"
                if (host == own) continue
                pool.execute {
                    if (found.get() == null && open(host, port, 350)) found.compareAndSet(null, host)
                }
            }
            pool.shutdown()
            pool.awaitTermination(15, TimeUnit.SECONDS)
            found.get()?.let { return it }
        }
        return null
    }
}
