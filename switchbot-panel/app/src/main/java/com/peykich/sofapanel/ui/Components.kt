package com.peykich.sofapanel.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.peykich.sofapanel.api.Command
import com.peykich.sofapanel.api.Device

val CardShape = RoundedCornerShape(20.dp)
val ButtonShape = RoundedCornerShape(12.dp)

/** ボタン地 #1F2226・角丸12dp・高さ44dp以上 */
@Composable
fun PanelButton(
    text: String,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    enabled: Boolean = true,
    highlight: Boolean = false,
    onClick: () -> Unit,
) {
    val accent = LocalAccent.current
    Row(
        modifier
            .heightIn(min = 44.dp)
            .clip(ButtonShape)
            .background(if (highlight) accent.copy(alpha = 0.18f) else PanelColors.Button.copy(alpha = LocalSurfaceAlpha.current))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
    ) {
        val color = when {
            !enabled -> PanelColors.SubText.copy(alpha = 0.5f)
            highlight -> accent
            else -> PanelColors.Text
        }
        if (icon != null) {
            Icon(icon, contentDescription = text, tint = color, modifier = Modifier.size(20.dp))
            if (text.isNotEmpty()) Spacer(Modifier.width(6.dp))
        }
        if (text.isNotEmpty()) {
            Text(text, color = color, fontSize = 14.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

/** 押すと選択肢が出るボタン（設定画面用） */
@Composable
fun ChoiceButton(
    current: String,
    options: List<Pair<String, () -> Unit>>,
    modifier: Modifier = Modifier,
) {
    var open by remember { mutableStateOf(false) }
    Box(modifier) {
        PanelButton(current, Modifier.fillMaxWidth()) { open = true }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            options.forEach { (label, action) ->
                DropdownMenuItem(
                    text = { Text(label, color = PanelColors.Text) },
                    onClick = {
                        open = false
                        action()
                    },
                )
            }
        }
    }
}

@Composable
fun PanelCard(modifier: Modifier = Modifier, borderColor: Color = PanelColors.Border, content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier
            .clip(CardShape)
            .background(PanelColors.Card.copy(alpha = LocalSurfaceAlpha.current))
            .border(1.dp, borderColor, CardShape)
            .padding(16.dp),
    ) { content() }
}

@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(text, modifier = modifier, color = PanelColors.SubText, fontSize = 13.sp, fontWeight = FontWeight.Medium)
}

fun deviceIcon(d: Device?): ImageVector {
    if (d == null) return PanelIcons.Power
    val type = d.type.removePrefix("DIY ")
    return when {
        d.name.contains("スクリーン") || d.name.contains("screen", ignoreCase = true) -> PanelIcons.Screen
        d.name.contains("プロジェクター") || type == "Projector" -> PanelIcons.Projector
        type == "Google TV" -> PanelIcons.Tv
        type == "TV" || type == "IPTV/Streamer" || type == "Set Top Box" -> PanelIcons.Tv
        type.contains("Light") || type.contains("Bulb") || type.contains("Lamp") -> PanelIcons.Bulb
        type.startsWith("Plug") -> PanelIcons.Plug
        type.contains("Curtain") || type.contains("Shade") || type.contains("Blind") -> PanelIcons.Screen
        type == "Speaker" || type == "DVD" -> PanelIcons.VolumeUp
        else -> PanelIcons.Power
    }
}

/** ボタンに出すアイコン。対応するものが無ければ null（文字で表示） */
fun commandIcon(c: Command): ImageVector? = when {
    c.command == "turnOn" && c.label == "電源" -> PanelIcons.Power
    c.commandType == "googletv" && c.parameter == "223" -> PanelIcons.Moon
    c.commandType == "googletv" && c.parameter == "26" -> PanelIcons.Power
    c.commandType == "googletv" && c.parameter == "24" -> PanelIcons.VolumeUp
    c.commandType == "googletv" && c.parameter == "25" -> PanelIcons.VolumeDown
    c.command == "volumeAdd" -> PanelIcons.VolumeUp
    c.command == "volumeSub" -> PanelIcons.VolumeDown
    c.command == "Play" -> PanelIcons.Play
    c.command == "Pause" -> PanelIcons.Pause
    c.label in setOf("上", "上げる", "開ける") -> PanelIcons.Up
    c.label in setOf("下", "下げる", "閉める") -> PanelIcons.Down
    c.label in setOf("停止", "止める") -> PanelIcons.Stop
    else -> null
}
