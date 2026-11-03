package com.peykich.sofapanel.domain

import com.peykich.sofapanel.api.Command
import com.peykich.sofapanel.api.Device

/** 機器の種類ごとに使えるコマンドの一覧（SwitchBot API v1.1 の仕様に基づく） */
object CommandCatalog {
    private val BULBS = setOf(
        "Color Bulb", "Strip Light", "Strip Light 3", "Ceiling Light", "Ceiling Light Pro",
        "Floor Lamp", "RGBICWW Strip Light", "RGBICWW Floor Lamp", "RGBIC Neon Wire Rope Light",
        "RGBIC Neon Rope Light", "Candle Warmer Lamp",
    )
    private val PLUGS = setOf("Plug", "Plug Mini (US)", "Plug Mini (JP)", "Plug Mini (EU)", "Relay Switch 1PM", "Relay Switch 1")
    private val CURTAINS = setOf("Curtain", "Curtain3", "Curtain 3", "Roller Shade")
    private val CLIMATE = setOf("Hub 2", "Hub 3", "Meter", "MeterPlus", "Meter Pro", "Meter Pro(CO2)", "WoIOSensor")

    val BRIGHTNESS_PRESETS = listOf(10, 30, 50, 80, 100)

    fun isPlug(d: Device) = !d.infrared && d.type in PLUGS
    fun isClimate(d: Device) = !d.infrared && d.type in CLIMATE
    fun supportsBrightness(d: Device) = !d.infrared && d.type in BULBS

    /** 赤外線リモコンはカスタムボタン（SwitchBot アプリで登録した名前）を送れる */
    fun supportsCustom(d: Device) = d.infrared

    fun brightness(percent: Int) = Command("setBrightness", percent.coerceIn(1, 100).toString(), label = "明るさ$percent%")

    fun custom(name: String) = Command(name, "default", "customize", label = name)

    /** Google TV（同じネットワーク内で直接操作。SwitchBot は通さない） */
    const val GOOGLE_TV_TYPE = "Google TV"
    const val GOOGLE_TV_COMMAND = "googletv"

    fun isGoogleTv(d: Device) = d.type == GOOGLE_TV_TYPE

    private fun tvKey(code: Int, label: String) = Command("key", code.toString(), GOOGLE_TV_COMMAND, label)

    val GOOGLE_TV_COMMANDS = listOf(
        tvKey(223, "スリープ"),
        tvKey(224, "起動"),
        tvKey(26, "電源"),
        tvKey(3, "ホーム"),
        tvKey(24, "音量＋"),
        tvKey(25, "音量−"),
        tvKey(164, "消音"),
    )

    fun commandsFor(d: Device): List<Command> {
        if (isGoogleTv(d)) return GOOGLE_TV_COMMANDS
        val on = Command("turnOn", label = if (d.infrared) "電源" else "オン")
        val off = Command("turnOff", label = if (d.infrared) "電源オフ" else "オフ")
        if (d.infrared) {
            val type = d.type.removePrefix("DIY ").trim()
            return when (type) {
                "Others" -> emptyList()
                "TV", "IPTV/Streamer", "IPTV", "Streamer", "Set Top Box" -> listOf(
                    on, off,
                    Command("volumeAdd", label = "音量＋"), Command("volumeSub", label = "音量−"),
                    Command("channelAdd", label = "チャンネル＋"), Command("channelSub", label = "チャンネル−"),
                )
                "DVD", "Speaker" -> listOf(
                    on, off, Command("setMute", label = "消音"),
                    Command("Play", label = "再生"), Command("Pause", label = "一時停止"), Command("Stop", label = "停止"),
                    Command("volumeAdd", label = "音量＋"), Command("volumeSub", label = "音量−"),
                    Command("Next", label = "次へ"), Command("Previous", label = "前へ"),
                )
                "Light" -> listOf(
                    on, off,
                    Command("brightnessUp", label = "明るく"), Command("brightnessDown", label = "暗く"),
                )
                "Fan" -> listOf(
                    on, off, Command("swing", label = "首振り"),
                    Command("lowSpeed", label = "弱"), Command("middleSpeed", label = "中"), Command("highSpeed", label = "強"),
                )
                "Air Conditioner" -> listOf(
                    Command("setAll", "26,2,1,on", label = "冷房26℃"),
                    Command("setAll", "28,2,1,on", label = "冷房28℃"),
                    Command("setAll", "22,5,1,on", label = "暖房22℃"),
                    Command("setAll", "24,3,1,on", label = "除湿"),
                    off,
                )
                else -> listOf(on, off)
            }
        }
        return when {
            d.type in BULBS -> listOf(on, off, Command("toggle", label = "切替")) + BRIGHTNESS_PRESETS.map { brightness(it) }
            d.type in PLUGS -> listOf(on, off, Command("toggle", label = "切替"))
            d.type in CURTAINS -> listOf(
                Command("turnOn", label = "開ける"), Command("turnOff", label = "閉める"), Command("pause", label = "停止"),
            )
            d.type == "Blind Tilt" -> listOf(
                Command("fullyOpen", label = "開ける"), Command("closeUp", label = "上に閉める"), Command("closeDown", label = "下に閉める"),
            )
            d.type == "Bot" -> listOf(Command("press", label = "押す"), on, off)
            d.type in CLIMATE -> emptyList()
            d.type.startsWith("Hub") || d.type.contains("Sensor") || d.type.contains("Lock") || d.type == "Keypad" -> emptyList()
            else -> listOf(on, off)
        }
    }

    /** マクロや操作パネルで使える（何かしら送れる）機器か */
    fun isControllable(d: Device) = commandsFor(d).isNotEmpty() || supportsCustom(d)
}
