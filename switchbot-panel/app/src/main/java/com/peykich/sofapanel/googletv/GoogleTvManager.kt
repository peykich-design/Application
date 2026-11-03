package com.peykich.sofapanel.googletv

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.util.Base64
import com.peykich.sofapanel.api.ApiParser
import com.peykich.sofapanel.api.Command
import com.peykich.sofapanel.api.Device
import com.peykich.sofapanel.data.SecretStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable

/** ペアリング済みの Google TV（証明書と秘密鍵を含むので暗号化して保存する） */
@Serializable
data class GoogleTvConfig(
    val host: String = "",
    val name: String = "",
    /** TV の証明書の SHA-256。空ならまだペアリングしていない */
    val fingerprint: String = "",
    val privateKey: String = "",
    val certificate: String = "",
)

/** 同じネットワークで見つかった Google TV */
data class FoundTv(val name: String, val host: String)

sealed class PairingState {
    data object Idle : PairingState()
    data object Connecting : PairingState()
    /** TV にコードが表示されて、入力を待っている */
    data class WaitingCode(val host: String) : PairingState()
    data object Verifying : PairingState()
    data class Failed(val message: String) : PairingState()
}

/** Google TV のペアリング・保存・キー送信・検索をまとめる */
class GoogleTvManager(context: Context, private val secrets: SecretStore, private val scope: CoroutineScope) {
    private val app = context.applicationContext
    private val _config = MutableStateFlow(load())
    private val _device = MutableStateFlow(deviceOf(_config.value))
    /** パネルやマクロに出す機器（ペアリング済みのときだけ） */
    val device: StateFlow<Device?> = _device
    val config: StateFlow<GoogleTvConfig> = _config

    private val _pairing = MutableStateFlow<PairingState>(PairingState.Idle)
    val pairing: StateFlow<PairingState> = _pairing
    private var session: GoogleTvClient.PairingSession? = null
    private var pendingName = ""
    private var pendingHost = ""

    private val _found = MutableStateFlow<List<FoundTv>>(emptyList())
    val found: StateFlow<List<FoundTv>> = _found
    private val _searching = MutableStateFlow(false)
    val searching: StateFlow<Boolean> = _searching

    private fun load(): GoogleTvConfig = secrets.loadText(KEY)?.let {
        runCatching { ApiParser.json.decodeFromString(GoogleTvConfig.serializer(), it) }.getOrNull()
    } ?: GoogleTvConfig()

    private fun save(c: GoogleTvConfig) {
        secrets.saveText(KEY, ApiParser.json.encodeToString(GoogleTvConfig.serializer(), c))
        _config.value = c
        _device.value = deviceOf(c)
    }

    private fun deviceOf(c: GoogleTvConfig): Device? =
        if (c.fingerprint.isEmpty()) null else Device(DEVICE_ID, c.name.ifBlank { "Google TV" }, DEVICE_TYPE)

    /** 一度作った証明書を使い続ける（TV が覚えるのはこの証明書） */
    private fun identity(): GoogleTvIdentity {
        val c = _config.value
        if (c.privateKey.isNotEmpty() && c.certificate.isNotEmpty()) {
            runCatching {
                return GoogleTvIdentity.fromBytes(
                    Base64.decode(c.privateKey, Base64.NO_WRAP),
                    Base64.decode(c.certificate, Base64.NO_WRAP),
                )
            }
        }
        val id = GoogleTvIdentity.generate()
        save(
            c.copy(
                privateKey = Base64.encodeToString(id.privateKeyBytes(), Base64.NO_WRAP),
                certificate = Base64.encodeToString(id.certificateBytes(), Base64.NO_WRAP),
            ),
        )
        return id
    }

    fun startPairing(host: String, name: String, clientName: String) {
        cancelPairing()
        pendingHost = host.trim()
        pendingName = name.trim().ifBlank { "Google TV" }
        if (pendingHost.isEmpty()) {
            _pairing.value = PairingState.Failed("IP アドレスを入れてください")
            return
        }
        _pairing.value = PairingState.Connecting
        scope.launch {
            try {
                val s = withContext(Dispatchers.IO) { GoogleTvClient(identity()).startPairing(pendingHost, clientName) }
                session = s
                _pairing.value = PairingState.WaitingCode(pendingHost)
            } catch (e: Exception) {
                _pairing.value = PairingState.Failed(e.message ?: "ペアリングを開始できません")
            }
        }
    }

    fun finishPairing(code: String) {
        val s = session ?: return
        _pairing.value = PairingState.Verifying
        scope.launch {
            try {
                val fingerprint = withContext(Dispatchers.IO) { s.finish(code) }
                save(_config.value.copy(host = pendingHost, name = pendingName, fingerprint = fingerprint))
                _pairing.value = PairingState.Idle
            } catch (e: Exception) {
                _pairing.value = PairingState.Failed(e.message ?: "ペアリングできませんでした")
            } finally {
                session = null
            }
        }
    }

    fun cancelPairing() {
        session?.close()
        session = null
        _pairing.value = PairingState.Idle
    }

    /** ペアリングを解除する（証明書は残すので、同じテレビなら次回もすぐ組める） */
    fun unpair() {
        cancelPairing()
        save(_config.value.copy(host = "", name = "", fingerprint = ""))
    }

    /** IP アドレスだけ変える（ルーターで IP が変わったとき。証明書の確認はそのまま） */
    fun updateHost(host: String) {
        if (_config.value.fingerprint.isNotEmpty()) save(_config.value.copy(host = host.trim()))
    }

    /** キーを送る。接続できないときは1回だけ再送する */
    suspend fun send(command: Command) {
        val c = _config.value
        if (c.fingerprint.isEmpty()) throw GoogleTvException("Google TV がペアリングされていません")
        val key = command.parameter.toIntOrNull() ?: throw GoogleTvException("未対応のキーです")
        withContext(Dispatchers.IO) {
            val client = GoogleTvClient(identity())
            try {
                client.sendKey(c.host, c.fingerprint, key)
            } catch (e: GoogleTvException) {
                if (!e.retryable) throw e
                client.sendKey(c.host, c.fingerprint, key)
            }
        }
    }

    /** 同じネットワークの Google TV / Android TV を数秒間さがす */
    @Suppress("DEPRECATION")
    fun search() {
        if (_searching.value) return
        val nsd = app.getSystemService(NsdManager::class.java) ?: return
        _found.value = emptyList()
        _searching.value = true
        val queue = ArrayDeque<NsdServiceInfo>()
        var resolving = false

        fun resolveNext() {
            if (resolving) return
            val info = queue.removeFirstOrNull() ?: return
            resolving = true
            nsd.resolveService(info, object : NsdManager.ResolveListener {
                override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                    scope.launch(Dispatchers.Main) {
                        resolving = false
                        resolveNext()
                    }
                }

                override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                    val host = if (Build.VERSION.SDK_INT >= 34) {
                        serviceInfo.hostAddresses.firstOrNull()?.hostAddress
                    } else {
                        serviceInfo.host?.hostAddress
                    }
                    scope.launch(Dispatchers.Main) {
                        if (host != null && _found.value.none { it.host == host }) {
                            _found.value = _found.value + FoundTv(serviceInfo.serviceName, host)
                        }
                        resolving = false
                        resolveNext()
                    }
                }
            })
        }

        val listener = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                _searching.value = false
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onServiceLost(serviceInfo: NsdServiceInfo) = Unit

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                scope.launch(Dispatchers.Main) {
                    queue.addLast(serviceInfo)
                    resolveNext()
                }
            }
        }
        try {
            nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
        } catch (e: Exception) {
            _searching.value = false
            return
        }
        scope.launch(Dispatchers.Main) {
            delay(SEARCH_MS)
            runCatching { nsd.stopServiceDiscovery(listener) }
            _searching.value = false
        }
    }

    companion object {
        const val DEVICE_ID = "googletv"
        const val DEVICE_TYPE = "Google TV"
        const val COMMAND_TYPE = "googletv"
        private const val KEY = "googletv"
        private const val SERVICE_TYPE = "_androidtvremote2._tcp."
        private const val SEARCH_MS = 6_000L
    }
}
