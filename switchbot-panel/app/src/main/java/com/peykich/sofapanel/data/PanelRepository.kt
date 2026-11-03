package com.peykich.sofapanel.data

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import com.peykich.sofapanel.api.ApiError
import com.peykich.sofapanel.api.ApiParser
import com.peykich.sofapanel.api.Climate
import com.peykich.sofapanel.api.Command
import com.peykich.sofapanel.api.Credentials
import com.peykich.sofapanel.api.Device
import com.peykich.sofapanel.api.SwitchBotClient
import com.peykich.sofapanel.domain.CommandCatalog
import com.peykich.sofapanel.googletv.GoogleTvException
import com.peykich.sofapanel.googletv.GoogleTvManager
import com.peykich.sofapanel.domain.DeviceStep
import com.peykich.sofapanel.domain.SceneStep
import com.peykich.sofapanel.domain.Step
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.withContext
import java.time.LocalDate

/** 上部バーに出す一行のメッセージ。details はタップで見せる内訳 */
data class StatusMessage(
    val text: String,
    val error: Boolean = false,
    val details: List<String> = emptyList(),
    val at: Long = System.currentTimeMillis(),
)

/** SwitchBot API と端末内の保存先をまとめて扱う */
class PanelRepository(context: Context, private val scope: CoroutineScope) {
    private val app = context.applicationContext
    val secrets = SecretStore(app)
    val settingsStore = SettingsStore(app)
    val macroStore = MacroStore(app)
    val backgroundStore = BackgroundStore(app)
    val googleTv = GoogleTvManager(app, secrets, scope)
    private val cache = ListCache(app)
    private val counterPrefs = app.getSharedPreferences("api_calls", Context.MODE_PRIVATE)

    val client = SwitchBotClient(credentials = { secrets.get() }, onCall = ::countCall)

    val settings: StateFlow<AppSettings> = settingsStore.flow.stateIn(scope, SharingStarted.Eagerly, AppSettings())
    private val _lists = MutableStateFlow(cache.load())
    val lists: StateFlow<CachedLists> = _lists

    /** パネルやマクロで使える機器（SwitchBot の機器 ＋ ペアリング済みの Google TV） */
    val allDevices: StateFlow<List<Device>> = combine(_lists, googleTv.device) { l, tv -> l.devices + listOfNotNull(tv) }
        .stateIn(scope, SharingStarted.Eagerly, _lists.value.devices + listOfNotNull(googleTv.device.value))

    val climate = MutableStateFlow<Climate?>(null)
    @Volatile
    var climateFetchedAt = 0L
        private set
    val status = MutableStateFlow<StatusMessage?>(null)
    val online = MutableStateFlow(true)
    val apiCallsToday = MutableStateFlow(0)
    /** 待機画面に出す「最後に実行したシーン／マクロ名」 */
    val lastRunName = MutableStateFlow<String?>(null)
    /** 最後に実行したカードの割り当て先（シーン ID またはマクロ ID） */
    val lastRunRef = MutableStateFlow<String?>(null)
    val hasCredentials = MutableStateFlow(false)

    init {
        apiCallsToday.value = counterPrefs.getInt(todayKey(), 0)
        hasCredentials.value = secrets.get() != null
        watchNetwork()
    }

    fun saveCredentials(token: String, secret: String) {
        secrets.save(Credentials(token.trim(), secret.trim()))
        hasCredentials.value = true
    }

    suspend fun <T> api(block: (SwitchBotClient) -> T): T = withContext(Dispatchers.IO) { block(client) }

    /** 通信エラー・タイムアウトなら1回だけ再送する */
    suspend fun <T> withRetry(block: (SwitchBotClient) -> T): T = try {
        api(block)
    } catch (e: ApiError) {
        if (e.retryable) api(block) else throw e
    }

    suspend fun refreshLists(): Result<CachedLists> = try {
        val devices = withRetry { it.devices() }
        val scenes = withRetry { it.scenes() }
        val lists = CachedLists(devices, scenes, System.currentTimeMillis())
        withContext(Dispatchers.IO) { cache.save(lists) }
        _lists.value = lists
        Result.success(lists)
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        Result.failure(e)
    }

    /** マクロの1ステップ（機器操作・シーン実行）を送る。失敗したら例外 */
    suspend fun sendStep(step: Step) {
        when (step) {
            is DeviceStep -> sendCommand(step.deviceId, step.command)
            is SceneStep -> withRetry { it.executeScene(step.sceneId) }
            else -> Unit
        }
    }

    /** 機器にコマンドを送る（Google TV は LAN で直接、それ以外は SwitchBot API） */
    suspend fun sendCommand(deviceId: String, command: Command) {
        if (command.commandType == CommandCatalog.GOOGLE_TV_COMMAND) {
            googleTv.send(command)
        } else {
            withRetry { it.command(deviceId, command) }
        }
    }

    fun device(id: String): Device? = _lists.value.devices.firstOrNull { it.id == id }

    /** 温湿度を取る機器（Hub 2 を優先） */
    fun climateDevice(): Device? {
        val devices = _lists.value.devices.filter { CommandCatalog.isClimate(it) }
        return devices.firstOrNull { it.type.startsWith("Hub") } ?: devices.firstOrNull()
    }

    suspend fun refreshClimate() {
        val d = climateDevice() ?: return
        climateFetchedAt = System.currentTimeMillis()
        try {
            val c = api { ApiParser.climate(it.status(d.id)) }
            if (c != null) climate.value = c
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // 温湿度は表示だけなので、失敗しても次回に任せる
        }
    }

    /** 最後の送信結果。お知らせが消えたらこれに戻す */
    private var lastResult: StatusMessage? = null

    /** 送信結果（「映画」を送信しました など）。次の結果が出るまで上部バーに残す */
    fun post(text: String, error: Boolean = false, details: List<String> = emptyList()) {
        val msg = StatusMessage(text, error, details)
        lastResult = msg
        status.value = msg
    }

    /** 設定の保存などのお知らせ。数秒で消えて、直前の送信結果の表示に戻る */
    fun notice(text: String, error: Boolean = false) {
        val msg = StatusMessage(text, error)
        status.value = msg
        scope.launch {
            delay(if (error) 10_000L else 4_000L)
            status.compareAndSet(msg, lastResult)
        }
    }

    /** 失敗の理由を、機器名付きの短い文にする */
    fun reason(e: Throwable, name: String? = null): String {
        val base = when (e) {
            is ApiError -> e.message.orEmpty()
            is GoogleTvException -> e.message.orEmpty()
            else -> "エラー（${e.javaClass.simpleName}）"
        }
        val withName = e is ApiError.Status || e is ApiError.Timeout || e is ApiError.Network || e is ApiError.Http ||
            e is GoogleTvException
        return if (withName && !name.isNullOrBlank()) "$name：$base" else base
    }

    private fun todayKey() = "calls_" + LocalDate.now().toString()

    private fun countCall() {
        val key = todayKey()
        val n = counterPrefs.getInt(key, 0) + 1
        val editor = counterPrefs.edit()
        // 古い日付の記録は消す
        counterPrefs.all.keys.filter { it != key }.forEach { editor.remove(it) }
        editor.putInt(key, n).apply()
        apiCallsToday.value = n
    }

    private fun watchNetwork() {
        val cm = app.getSystemService(ConnectivityManager::class.java) ?: return
        fun check(caps: NetworkCapabilities?) = caps?.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) == true
        online.value = check(cm.activeNetwork?.let { cm.getNetworkCapabilities(it) })
        runCatching {
            cm.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    online.value = true
                }

                override fun onLost(network: Network) {
                    online.value = false
                }

                override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                    online.value = check(caps)
                }
            })
        }
    }
}
