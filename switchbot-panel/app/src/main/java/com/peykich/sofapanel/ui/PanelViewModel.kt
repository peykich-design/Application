package com.peykich.sofapanel.ui

import android.app.Application
import android.net.Uri
import android.os.SystemClock
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.peykich.sofapanel.PanelApp
import com.peykich.sofapanel.api.Command
import com.peykich.sofapanel.api.Device
import com.peykich.sofapanel.data.AppSettings
import com.peykich.sofapanel.data.CardKind
import com.peykich.sofapanel.data.CardSlot
import com.peykich.sofapanel.data.CachedLists
import com.peykich.sofapanel.data.PanelConfig
import com.peykich.sofapanel.domain.CommandCatalog
import com.peykich.sofapanel.domain.DeviceStep
import com.peykich.sofapanel.domain.Macro
import com.peykich.sofapanel.domain.MacroRules
import com.peykich.sofapanel.domain.SceneStep
import com.peykich.sofapanel.domain.Step
import com.peykich.sofapanel.domain.TimeRules
import com.peykich.sofapanel.domain.newId
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.LocalTime

sealed class Screen {
    data object Main : Screen()
    data object Settings : Screen()
    data class Editor(val macro: Macro, val returnTo: Screen) : Screen()
}

/** 無操作時の表示。ACTIVE は通常の画面 */
enum class IdleMode { ACTIVE, STANDBY, NIGHT }

class PanelViewModel(application: Application) : AndroidViewModel(application) {
    private val panelApp = application as PanelApp
    val repo = panelApp.repo
    val runner = panelApp.runner
    val charge = panelApp.charge
    private val appScope = panelApp.appScope

    val settings: StateFlow<AppSettings> = repo.settings
    val lists: StateFlow<CachedLists> = repo.lists
    val macros: StateFlow<List<Macro>> = repo.macroStore.macros
    /** パネルやマクロで使える機器（SwitchBot ＋ Google TV） */
    val devices: StateFlow<List<Device>> = repo.allDevices

    var screen by mutableStateOf<Screen>(Screen.Main)
        private set
    var idle by mutableStateOf(IdleMode.ACTIVE)
        private set

    private val _now = MutableStateFlow(System.currentTimeMillis())
    val now: StateFlow<Long> = _now

    /** 失敗したカードの番号（揺らす） */
    private val _shake = MutableSharedFlow<Int>(extraBufferCapacity = 4)
    val shake: SharedFlow<Int> = _shake

    private var lastInteraction = SystemClock.elapsedRealtime()

    /** 背景画像（設定していなければ null） */
    private val _background = MutableStateFlow<ImageBitmap?>(null)
    val background: StateFlow<ImageBitmap?> = _background

    init {
        if (!repo.hasCredentials.value) {
            screen = Screen.Settings
        } else {
            refreshLists(silent = true)
        }
        viewModelScope.launch {
            while (true) {
                _now.value = System.currentTimeMillis()
                checkIdle()
                delay(1000)
            }
        }
        viewModelScope.launch {
            settings.map { it.backgroundVersion }.distinctUntilChanged().collect { version ->
                _background.value = if (version == 0L) {
                    null
                } else {
                    withContext(Dispatchers.IO) { runCatching { repo.backgroundStore.load()?.asImageBitmap() }.getOrNull() }
                }
            }
        }
        viewModelScope.launch {
            runner.failed.collect { id ->
                val i = settings.value.cards.indexOfFirst { it.kind == CardKind.MACRO && it.refId == id }
                if (i >= 0) _shake.emit(i)
            }
        }
    }

    // ---- 無操作と待機画面 ----

    private fun checkIdle() {
        val s = settings.value
        val idleMs = SystemClock.elapsedRealtime() - lastInteraction
        val limit = s.idleSeconds * 1000L
        val t = LocalTime.now()
        val night = s.nightEnabled && TimeRules.inWindow(t.hour * 60 + t.minute, s.nightStart, s.nightEnd)
        val running = runner.state.value != null
        when (idle) {
            IdleMode.ACTIVE ->
                if (screen == Screen.Main && !running && idleMs >= limit) {
                    idle = if (night) IdleMode.NIGHT else IdleMode.STANDBY
                }
            IdleMode.STANDBY -> if (night && idleMs >= limit) idle = IdleMode.NIGHT
            IdleMode.NIGHT -> if (!night) idle = IdleMode.STANDBY
        }
    }

    /**
     * 画面に触れたとき（Activity から呼ぶ）。
     * 待機・夜間表示から戻すためのタップなら true を返し、そのタップでは操作を実行しない。
     */
    fun onUserInteraction(): Boolean {
        lastInteraction = SystemClock.elapsedRealtime()
        return when (idle) {
            IdleMode.ACTIVE -> false
            IdleMode.STANDBY -> {
                idle = IdleMode.ACTIVE
                true
            }
            IdleMode.NIGHT -> {
                idle = IdleMode.STANDBY
                true
            }
        }
    }

    // ---- 画面の切り替え ----

    fun openSettings() {
        screen = Screen.Settings
    }

    fun goMain() {
        screen = Screen.Main
        lastInteraction = SystemClock.elapsedRealtime()
    }

    fun openEditor(macro: Macro, returnTo: Screen = screen) {
        screen = Screen.Editor(macro, returnTo)
    }

    /** 新しいマクロ。copyFrom を渡すとその複製から始める */
    fun newMacro(copyFrom: Macro?) {
        val m = if (copyFrom == null) {
            Macro(name = "")
        } else {
            Macro(name = copyFrom.name + "のコピー", icon = copyFrom.icon, steps = copyFrom.steps.map { it.withUid(newId()) })
        }
        openEditor(m)
    }

    fun closeEditor() {
        val e = screen as? Screen.Editor ?: return
        screen = e.returnTo
        lastInteraction = SystemClock.elapsedRealtime()
    }

    // ---- カード ----

    fun cardMacro(slot: CardSlot): Macro? =
        if (slot.kind == CardKind.MACRO) macros.value.firstOrNull { it.id == slot.refId } else null

    fun onCardTap(index: Int) {
        val slot = settings.value.cards.getOrNull(index) ?: return
        when (slot.kind) {
            CardKind.NONE -> repo.notice("このカードは未割り当てです（部屋名を2秒長押し → 設定）")
            CardKind.SCENE -> runScene(index, slot)
            CardKind.MACRO -> {
                val m = cardMacro(slot)
                when {
                    m == null -> {
                        repo.notice("マクロが見つかりません", error = true)
                        _shake.tryEmit(index)
                    }
                    // 実行中のカードをもう一度タップしたら中断（確認なし）
                    runner.state.value?.macroId == m.id -> runner.cancel()
                    else -> runner.start(m, macros.value)
                }
            }
        }
    }

    fun onCardLongPress(index: Int) {
        val slot = settings.value.cards.getOrNull(index) ?: return
        when (slot.kind) {
            CardKind.MACRO -> cardMacro(slot)?.let { openEditor(it, Screen.Main) }
            CardKind.SCENE -> repo.notice("シーンの中身は SwitchBot アプリで編集してください")
            CardKind.NONE -> repo.notice("カードの割り当ては設定画面で行います")
        }
    }

    private fun runScene(index: Int, slot: CardSlot) {
        val name = lists.value.scenes.firstOrNull { it.id == slot.refId }?.name ?: slot.name
        appScope.launch {
            try {
                repo.withRetry { it.executeScene(slot.refId) }
                repo.lastRunName.value = name
                repo.lastRunRef.value = slot.refId
                repo.post("「$name」を送信しました")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                repo.post(repo.reason(e, name), error = true)
                _shake.tryEmit(index)
            }
        }
    }

    // ---- 個別操作・ステップ送信 ----

    fun sendPanel(panel: PanelConfig, command: Command) {
        appScope.launch {
            try {
                repo.sendCommand(panel.deviceId, command)
                // 赤外線リモコンは状態が分からないので「送信した」ことだけを出す
                repo.post("${panel.deviceName}：${command.label}を送信しました")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                repo.post(repo.reason(e, panel.deviceName), error = true)
            }
        }
    }

    /** 設定画面の Google TV「試す」 */
    fun sendTv(command: Command) {
        val name = repo.googleTv.device.value?.name ?: "Google TV"
        appScope.launch {
            try {
                repo.sendCommand(com.peykich.sofapanel.googletv.GoogleTvManager.DEVICE_ID, command)
                repo.notice("$name：${command.label}を送信しました")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                repo.notice(repo.reason(e, name), error = true)
            }
        }
    }

    /** マクロ編集中に「このステップだけ送る」 */
    fun sendStep(step: Step) {
        val label = MacroRules.label(step, macros.value)
        appScope.launch {
            try {
                repo.sendStep(step)
                repo.post("$label を送信しました")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                val name = (step as? DeviceStep)?.deviceName ?: (step as? SceneStep)?.sceneName
                repo.post(repo.reason(e, name), error = true)
            }
        }
    }

    fun testRun(macro: Macro) {
        val others = macros.value.filter { it.id != macro.id }
        runner.start(macro, others + macro)
    }

    fun stopRun() = runner.cancel()

    /** 保存できたら null、できなければ理由 */
    fun saveMacro(macro: Macro): String? {
        val m = macro.copy(name = macro.name.trim())
        MacroRules.validate(m, macros.value)?.let { return it }
        return try {
            repo.macroStore.save(m)
            repo.notice("マクロ「${m.name}」を保存しました")
            closeEditor()
            null
        } catch (e: Exception) {
            "保存に失敗しました"
        }
    }

    fun deleteMacro(id: String) {
        repo.macroStore.delete(id)
        updateSettings { s ->
            s.copy(cards = s.cards.map { if (it.kind == CardKind.MACRO && it.refId == id) CardSlot() else it })
        }
    }

    // ---- 設定 ----

    fun updateSettings(transform: (AppSettings) -> AppSettings) {
        viewModelScope.launch {
            repo.settingsStore.update(transform)
            charge.evaluate()
        }
    }

    fun saveCredentials(token: String, secret: String) {
        if (token.isBlank() || secret.isBlank()) {
            repo.notice("トークンとシークレットの両方を入れてください", error = true)
            return
        }
        repo.saveCredentials(token, secret)
        refreshLists(silent = false)
    }

    fun refreshLists(silent: Boolean) {
        appScope.launch {
            repo.refreshLists()
                .onSuccess { l ->
                    if (!silent) repo.notice("接続できました：機器${l.devices.size}台・シーン${l.scenes.size}件")
                    autoAssignOnce(l)
                    if (repo.climate.value == null) repo.refreshClimate()
                }
                .onFailure { e -> repo.notice("一覧を取得できません：" + repo.reason(e), error = true) }
        }
    }

    /** 初回だけ、シーンをカードに、機器を操作パネルに仮で割り当てる（あとで設定画面で変更できる） */
    private suspend fun autoAssignOnce(l: CachedLists) {
        if (settings.value.autoAssigned || (l.devices.isEmpty() && l.scenes.isEmpty())) return
        val cards = settings.value.cards.toMutableList()
        l.scenes.take(AppSettings.CARD_COUNT).forEachIndexed { i, s ->
            if (cards[i].kind == CardKind.NONE) cards[i] = CardSlot(CardKind.SCENE, s.id, s.name)
        }
        val panels = suggestPanels(l.devices)
        repo.settingsStore.update { s ->
            s.copy(
                cards = cards,
                panels = s.panels.mapIndexed { i, p -> if (p.deviceId.isEmpty()) panels.getOrNull(i) ?: p else p },
                autoAssigned = true,
            )
        }
    }

    private fun suggestPanels(devices: List<Device>): List<PanelConfig> {
        val out = mutableListOf<PanelConfig>()
        val light = devices.firstOrNull { CommandCatalog.supportsBrightness(it) }
            ?: devices.firstOrNull { it.infrared && it.type.removePrefix("DIY ") == "Light" }
        light?.let { out += defaultPanel(it, "照明") }
        devices.firstOrNull { it.infrared && it.type.removePrefix("DIY ") in setOf("TV", "IPTV/Streamer", "Set Top Box") }
            ?.let { out += defaultPanel(it, "TV") }
        devices.firstOrNull { it.name.contains("スクリーン") || it.name.contains("screen", ignoreCase = true) }
            ?.let { out += defaultPanel(it, "スクリーン") }
        return out
    }

    fun defaultPanel(d: Device, title: String = ""): PanelConfig {
        val cmds = CommandCatalog.commandsFor(d)
        val isScreen = d.name.contains("スクリーン") || d.name.contains("screen", ignoreCase = true)
        val buttons = when {
            CommandCatalog.supportsBrightness(d) -> cmds.filter { it.command == "turnOff" || it.command == "turnOn" }
            cmds.any { it.command == "volumeAdd" } ->
                cmds.filter { it.command == "turnOn" || it.command == "volumeSub" || it.command == "volumeAdd" }
            isScreen && d.infrared -> listOf(CommandCatalog.custom("上"), CommandCatalog.custom("停止"), CommandCatalog.custom("下"))
            else -> cmds.take(3)
        }
        return PanelConfig(
            title = title,
            deviceId = d.id,
            deviceName = d.name,
            buttons = buttons,
            dimmer = CommandCatalog.supportsBrightness(d),
        )
    }

    // ---- 背景画像 ----

    fun setBackground(uri: Uri) {
        viewModelScope.launch {
            val ok = withContext(Dispatchers.IO) { runCatching { repo.backgroundStore.import(uri) }.isSuccess }
            if (ok) {
                repo.settingsStore.update { it.copy(backgroundVersion = System.currentTimeMillis()) }
                repo.notice("背景画像を設定しました")
            } else {
                repo.notice("画像を読み込めませんでした", error = true)
            }
        }
    }

    fun clearBackground() {
        viewModelScope.launch {
            withContext(Dispatchers.IO) { repo.backgroundStore.clear() }
            repo.settingsStore.update { it.copy(backgroundVersion = 0) }
            repo.notice("背景画像を外しました")
        }
    }

    // ---- 書き出し・読み込み ----

    fun exportMacros(uri: Uri) {
        viewModelScope.launch {
            val ok = withContext(Dispatchers.IO) {
                runCatching {
                    val text = repo.macroStore.exportJson()
                    getApplication<Application>().contentResolver.openOutputStream(uri, "wt")?.use {
                        it.write(text.toByteArray(Charsets.UTF_8))
                    } ?: error("no stream")
                }.isSuccess
            }
            repo.notice(if (ok) "マクロを書き出しました" else "書き出しに失敗しました", error = !ok)
        }
    }

    fun importMacros(uri: Uri) {
        viewModelScope.launch {
            val count = withContext(Dispatchers.IO) {
                runCatching {
                    val text = getApplication<Application>().contentResolver.openInputStream(uri)?.use {
                        it.readBytes().toString(Charsets.UTF_8)
                    } ?: error("no stream")
                    repo.macroStore.importJson(text)
                }.getOrNull()
            }
            if (count != null) repo.notice("マクロを${count}件読み込みました") else repo.notice("読み込めませんでした（形式が違います）", error = true)
        }
    }
}
