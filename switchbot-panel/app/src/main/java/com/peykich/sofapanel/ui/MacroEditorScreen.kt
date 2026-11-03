@file:OptIn(ExperimentalFoundationApi::class, ExperimentalLayoutApi::class)

package com.peykich.sofapanel.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.toMutableStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInRoot
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
import com.peykich.sofapanel.api.Command
import com.peykich.sofapanel.api.Device
import com.peykich.sofapanel.api.Scene
import com.peykich.sofapanel.domain.CommandCatalog
import com.peykich.sofapanel.domain.DeviceStep
import com.peykich.sofapanel.domain.Macro
import com.peykich.sofapanel.domain.MacroRules
import com.peykich.sofapanel.domain.MacroStep
import com.peykich.sofapanel.domain.SceneStep
import com.peykich.sofapanel.domain.Step
import com.peykich.sofapanel.domain.WaitStep
import com.peykich.sofapanel.domain.newId
import kotlin.math.abs
import kotlin.math.roundToInt

/** 右側の「追加できる部品」 */
private sealed class Part {
    data class DevicePart(val device: Device) : Part()
    data object WaitPart : Part()
    data class ScenePart(val scene: Scene) : Part()
    data class MacroPart(val macro: Macro) : Part()
}

/** 機器のコマンドを選ぶ途中の状態。replaceIndex があればそのステップを置き換える */
private data class PickRequest(val device: Device, val insertIndex: Int, val replaceIndex: Int? = null)

/** 待ち時間を決める途中の状態 */
private data class WaitRequest(val seconds: Int, val insertIndex: Int, val replaceIndex: Int? = null)

@Composable
fun MacroEditorScreen(vm: PanelViewModel, target: Screen.Editor) {
    val macros by vm.macros.collectAsState()
    val lists by vm.lists.collectAsState()
    val allDevices by vm.devices.collectAsState()
    val run by vm.runner.state.collectAsState()
    val status by vm.repo.status.collectAsState()
    val accent = LocalAccent.current
    val density = LocalDensity.current

    var name by remember { mutableStateOf(target.macro.name) }
    var icon by remember { mutableStateOf(target.macro.icon) }
    val steps = remember { target.macro.steps.toMutableStateList() }
    var error by remember { mutableStateOf<String?>(null) }

    fun current() = target.macro.copy(name = name, icon = icon, steps = steps.toList())
    val others = macros.filter { it.id != target.macro.id }
    val edited = current()
    val totalSeconds = MacroRules.totalSeconds(edited, others + edited)
    val runningThis = run?.macroId == target.macro.id

    var pick by remember { mutableStateOf<PickRequest?>(null) }
    var waitEdit by remember { mutableStateOf<WaitRequest?>(null) }

    // 部品のドラッグ（長押しで持ち上げる）
    var ghost by remember { mutableStateOf<Part?>(null) }
    var ghostPos by remember { mutableStateOf(Offset.Zero) }
    var rootPos by remember { mutableStateOf(Offset.Zero) }
    var listBounds by remember { mutableStateOf(Rect.Zero) }
    val listState = rememberLazyListState()

    // ステップの並べ替え
    var draggingUid by remember { mutableStateOf<String?>(null) }
    var dragOffset by remember { mutableFloatStateOf(0f) }

    fun insert(step: Step, index: Int) {
        val i = if (index < 0 || index > steps.size) steps.size else index
        steps.add(i, step)
        error = null
    }

    fun addPart(part: Part, index: Int) {
        when (part) {
            is Part.DevicePart -> {
                pick = PickRequest(part.device, index)
            }
            Part.WaitPart -> {
                waitEdit = WaitRequest(10, index)
            }
            is Part.ScenePart -> insert(SceneStep(sceneId = part.scene.id, sceneName = part.scene.name), index)
            is Part.MacroPart -> insert(MacroStep(macroId = part.macro.id, macroName = part.macro.name), index)
        }
    }

    /** 指の位置（画面座標）に差し込むときの位置 */
    fun insertIndexAt(y: Float): Int {
        val local = y - listBounds.top
        for (item in listState.layoutInfo.visibleItemsInfo) {
            if (local < item.offset + item.size / 2f) return item.index
        }
        return steps.size
    }

    fun reorderIfNeeded() {
        val uid = draggingUid ?: return
        val info = listState.layoutInfo
        val cur = info.visibleItemsInfo.firstOrNull { it.key == uid } ?: return
        // 前回の入れ替えがまだ画面に反映されていなければ待つ
        if (cur.index != steps.indexOfFirst { it.uid == uid }) return
        val spacing = info.mainAxisItemSpacing
        val center = cur.offset + dragOffset + cur.size / 2f
        val next = info.visibleItemsInfo.firstOrNull { it.index == cur.index + 1 }
        val prev = info.visibleItemsInfo.firstOrNull { it.index == cur.index - 1 }
        if (next != null && center > next.offset + next.size / 2f) {
            steps.add(cur.index + 1, steps.removeAt(cur.index))
            dragOffset -= (next.size + spacing)
        } else if (prev != null && center < prev.offset + prev.size / 2f) {
            steps.add(cur.index - 1, steps.removeAt(cur.index))
            dragOffset += (prev.size + spacing)
        }
    }

    Box(Modifier.fillMaxSize().onGloballyPositioned { rootPos = it.positionInRoot() }) {
        Column(Modifier.fillMaxSize().padding(horizontal = 20.dp, vertical = 12.dp)) {
            // ---- 上部：マクロ名・アイコン・合計時間・テスト実行・保存 ----
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                PanelButton("", icon = PanelIcons.Back) { vm.closeEditor() }
                IconPicker(icon) { icon = it }
                OutlinedTextField(
                    value = name,
                    onValueChange = {
                        name = it
                        error = null
                    },
                    placeholder = { Text("マクロ名（例：映画）") },
                    singleLine = true,
                    modifier = Modifier.weight(1f),
                )
                Column(horizontalAlignment = Alignment.End) {
                    Text("合計", color = PanelColors.SubText, fontSize = 11.sp)
                    Text(
                        MacroRules.formatSeconds(totalSeconds),
                        color = PanelColors.Text,
                        fontFamily = OutfitFamily,
                        fontWeight = FontWeight.Light,
                        fontSize = 20.sp,
                    )
                }
                if (runningThis) {
                    PanelButton("中断", icon = PanelIcons.Stop, highlight = true) { vm.stopRun() }
                } else {
                    PanelButton("テスト実行", icon = PanelIcons.Play, enabled = steps.isNotEmpty()) {
                        val err = MacroRules.validate(current().copy(name = name.ifBlank { "テスト" }), others)
                        if (err != null) {
                            error = err
                        } else {
                            vm.testRun(current().copy(name = name.ifBlank { "テスト" }))
                        }
                    }
                }
                PanelButton("保存", icon = PanelIcons.Check, highlight = true) {
                    error = vm.saveMacro(current())
                }
            }
            // ---- 状態の一行 ----
            val line = when {
                error != null -> error
                runningThis -> run?.let { "実行中 ${it.index}/${it.total}・残り${MacroRules.formatSeconds(it.remainingSeconds)}" }
                else -> status?.text
            }
            Text(
                line ?: "部品はタップで末尾に追加、長押しでドラッグして好きな位置へ。ステップは右端のつまみで並べ替え、横スワイプで削除、長押しで複製。",
                color = if (error != null || status?.error == true && !runningThis) PanelColors.Error else PanelColors.SubText,
                fontSize = 13.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(vertical = 8.dp),
            )
            Row(Modifier.weight(1f).fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                // ---- 左：ステップの縦並び ----
                Box(
                    Modifier
                        .weight(0.58f)
                        .fillMaxHeight()
                        .onGloballyPositioned { listBounds = it.boundsInRoot() },
                ) {
                    LazyColumn(
                        state = listState,
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                        contentPadding = PaddingValues(bottom = 48.dp),
                        modifier = Modifier.fillMaxSize(),
                    ) {
                        itemsIndexed(steps, key = { _, s -> s.uid }) { index, step ->
                            val dragging = draggingUid == step.uid
                            StepRow(
                                step = step,
                                number = index + 1,
                                label = MacroRules.label(step, macros),
                                modifier = if (dragging) {
                                    Modifier.zIndex(1f).graphicsLayer { translationY = dragOffset }
                                } else {
                                    Modifier.animateItem()
                                },
                                dragging = dragging,
                                onTap = {
                                    val i = steps.indexOfFirst { it.uid == step.uid }
                                    when (step) {
                                        is WaitStep -> {
                                            waitEdit = WaitRequest(step.seconds, i, replaceIndex = i)
                                        }
                                        is DeviceStep -> {
                                            val d = allDevices.firstOrNull { it.id == step.deviceId }
                                            if (d != null) pick = PickRequest(d, i, replaceIndex = i)
                                        }
                                        else -> Unit
                                    }
                                },
                                onDuplicate = {
                                    val i = steps.indexOfFirst { it.uid == step.uid }
                                    if (i >= 0) steps.add(i + 1, step.withUid(newId()))
                                },
                                onDelete = {
                                    steps.removeAll { it.uid == step.uid }
                                },
                                onSend = { vm.sendStep(step) },
                                onHandleDragStart = {
                                    draggingUid = step.uid
                                    dragOffset = 0f
                                },
                                onHandleDrag = { dy ->
                                    dragOffset += dy
                                    reorderIfNeeded()
                                },
                                onHandleDragEnd = {
                                    draggingUid = null
                                    dragOffset = 0f
                                },
                            )
                        }
                    }
                    if (steps.isEmpty()) {
                        Text(
                            "右の部品をタップして追加してください",
                            color = PanelColors.SubText,
                            fontSize = 14.sp,
                            modifier = Modifier.align(Alignment.Center),
                        )
                    }
                    // 部品をドラッグ中は差し込み位置に線を出す
                    if (ghost != null && listBounds.contains(ghostPos)) {
                        val idx = insertIndexAt(ghostPos.y)
                        val items = listState.layoutInfo.visibleItemsInfo
                        val y = items.firstOrNull { it.index == idx }?.offset?.toFloat()
                            ?: items.lastOrNull()?.let { (it.offset + it.size).toFloat() }
                            ?: 0f
                        Box(
                            Modifier
                                .offset { IntOffset(0, (y - with(density) { 4.dp.toPx() }).roundToInt()) }
                                .fillMaxWidth()
                                .height(3.dp)
                                .background(accent),
                        )
                    }
                }
                // ---- 右：追加できる部品 ----
                Palette(
                    modifier = Modifier.weight(0.42f).fillMaxHeight(),
                    devices = allDevices.filter { CommandCatalog.isControllable(it) },
                    scenes = lists.scenes,
                    macros = others,
                    onTap = { addPart(it, -1) },
                    onDragStart = { part, pos ->
                        ghost = part
                        ghostPos = pos
                    },
                    onDrag = { delta -> ghostPos += delta },
                    onDragEnd = {
                        val part = ghost
                        if (part != null && listBounds.contains(ghostPos)) addPart(part, insertIndexAt(ghostPos.y))
                        ghost = null
                    },
                    onDragCancel = { ghost = null },
                )
            }
        }
        // ドラッグ中の部品（指に付いてくる）
        ghost?.let { part ->
            val (label, partIcon) = partLabel(part)
            Row(
                Modifier
                    .offset {
                        IntOffset(
                            (ghostPos.x - rootPos.x - 40.dp.toPx()).roundToInt(),
                            (ghostPos.y - rootPos.y - 28.dp.toPx()).roundToInt(),
                        )
                    }
                    .zIndex(10f)
                    .clip(ButtonShape)
                    .background(PanelColors.Button)
                    .border(1.dp, accent, ButtonShape)
                    .padding(horizontal = 14.dp, vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(partIcon, null, tint = accent, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
                Text(label, color = PanelColors.Text, fontSize = 14.sp)
            }
        }
    }

    pick?.let { req ->
        CommandPicker(
            device = req.device,
            onDismiss = { pick = null },
            onPick = { cmd ->
                val step = DeviceStep(deviceId = req.device.id, deviceName = req.device.name, command = cmd)
                val r = req.replaceIndex
                if (r != null && r in steps.indices) {
                    steps[r] = step.copy(uid = steps[r].uid)
                } else {
                    insert(step, req.insertIndex)
                }
                pick = null
            },
        )
    }
    waitEdit?.let { req ->
        WaitDialog(
            initial = req.seconds,
            onDismiss = { waitEdit = null },
            onConfirm = { sec ->
                val r = req.replaceIndex
                if (r != null && r in steps.indices) {
                    steps[r] = WaitStep(uid = steps[r].uid, seconds = sec)
                } else {
                    insert(WaitStep(seconds = sec), req.insertIndex)
                }
                waitEdit = null
            },
        )
    }
}

private fun partLabel(part: Part): Pair<String, ImageVector> = when (part) {
    is Part.DevicePart -> part.device.name to deviceIcon(part.device)
    Part.WaitPart -> "待つ" to PanelIcons.Clock
    is Part.ScenePart -> part.scene.name to PanelIcons.Scene
    is Part.MacroPart -> part.macro.name to PanelIcons.Steps
}

@Composable
private fun IconPicker(current: String, onPick: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        PanelButton("", icon = PanelIcons.byName(current), highlight = true) { open = true }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            FlowRow(Modifier.widthIn(max = 260.dp).padding(8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                PanelIcons.choices.forEach { (key, vector) ->
                    PanelButton("", icon = vector, highlight = key == current) {
                        onPick(key)
                        open = false
                    }
                }
            }
        }
    }
}

@Composable
private fun StepRow(
    step: Step,
    number: Int,
    label: String,
    modifier: Modifier,
    dragging: Boolean,
    onTap: () -> Unit,
    onDuplicate: () -> Unit,
    onDelete: () -> Unit,
    onSend: () -> Unit,
    onHandleDragStart: () -> Unit,
    onHandleDrag: (Float) -> Unit,
    onHandleDragEnd: () -> Unit,
) {
    val accent = LocalAccent.current
    var swipe by remember(step.uid) { mutableFloatStateOf(0f) }
    var width by remember { mutableIntStateOf(1) }
    val isWait = step is WaitStep
    val shape = if (isWait) ButtonShape else CardShape
    Row(
        modifier
            .fillMaxWidth()
            .onGloballyPositioned { width = it.size.width }
            .graphicsLayer {
                translationX = swipe
                alpha = 1f - (abs(swipe) / width).coerceAtMost(0.7f)
            }
            .then(if (isWait) Modifier.padding(horizontal = 28.dp) else Modifier)
            .clip(shape)
            .background(if (isWait) PanelColors.Bg else PanelColors.Card)
            .border(1.dp, if (dragging) accent else PanelColors.Border, shape)
            .combinedClickable(onClick = onTap, onLongClick = onDuplicate)
            // 横スワイプで削除
            .pointerInput(step.uid) {
                detectHorizontalDragGestures(
                    onDragEnd = {
                        if (abs(swipe) > width * 0.35f) onDelete() else swipe = 0f
                    },
                    onDragCancel = { swipe = 0f },
                ) { change, dx ->
                    change.consume()
                    swipe += dx
                }
            }
            .height(if (isWait) 36.dp else 56.dp)
            .padding(start = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val stepIcon = when (step) {
            is WaitStep -> PanelIcons.Clock
            is SceneStep -> PanelIcons.Scene
            is MacroStep -> PanelIcons.Steps
            is DeviceStep -> PanelIcons.Power
        }
        if (!isWait) {
            Text("$number", color = PanelColors.SubText, fontFamily = OutfitFamily, fontSize = 13.sp, modifier = Modifier.width(22.dp))
        }
        Icon(stepIcon, null, tint = if (isWait) PanelColors.SubText else PanelColors.Text, modifier = Modifier.size(if (isWait) 16.dp else 20.dp))
        Spacer(Modifier.width(10.dp))
        Text(
            label,
            color = if (isWait) PanelColors.SubText else PanelColors.Text,
            fontSize = if (isWait) 13.sp else 15.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        // このステップだけ送る（実機で動きを確かめながら組む）
        if (step is DeviceStep || step is SceneStep) {
            Box(
                Modifier
                    .size(44.dp)
                    .clip(ButtonShape)
                    .clickable(onClick = onSend),
                contentAlignment = Alignment.Center,
            ) {
                Icon(PanelIcons.Send, "このステップだけ送る", tint = accent, modifier = Modifier.size(20.dp))
            }
        }
        // 並べ替えのつまみ
        Box(
            Modifier
                .size(width = 44.dp, height = if (isWait) 36.dp else 56.dp)
                .pointerInput(step.uid) {
                    detectDragGestures(
                        onDragStart = { onHandleDragStart() },
                        onDragEnd = onHandleDragEnd,
                        onDragCancel = onHandleDragEnd,
                    ) { change, amount ->
                        change.consume()
                        onHandleDrag(amount.y)
                    }
                },
            contentAlignment = Alignment.Center,
        ) {
            Icon(PanelIcons.Handle, "並べ替え", tint = PanelColors.SubText, modifier = Modifier.size(20.dp))
        }
    }
}

@Composable
private fun Palette(
    modifier: Modifier,
    devices: List<Device>,
    scenes: List<Scene>,
    macros: List<Macro>,
    onTap: (Part) -> Unit,
    onDragStart: (Part, Offset) -> Unit,
    onDrag: (Offset) -> Unit,
    onDragEnd: () -> Unit,
    onDragCancel: () -> Unit,
) {
    PanelCard(modifier) {
        Text("追加できる部品", color = PanelColors.Text, fontSize = 15.sp, fontWeight = FontWeight.Medium)
        Spacer(Modifier.height(8.dp))
        LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxSize()) {
            item { SectionTitle("部屋の機器", Modifier.padding(top = 4.dp)) }
            if (devices.isEmpty()) item { Text("機器がありません（設定で一覧を取得）", color = PanelColors.SubText, fontSize = 13.sp) }
            items(devices, key = { "d" + it.id }) { d ->
                PartRow(Part.DevicePart(d), d.name, deviceIcon(d), onTap, onDragStart, onDrag, onDragEnd, onDragCancel)
            }
            item { SectionTitle("待つ", Modifier.padding(top = 8.dp)) }
            item { PartRow(Part.WaitPart, "待つ（秒数を指定）", PanelIcons.Clock, onTap, onDragStart, onDrag, onDragEnd, onDragCancel) }
            item { SectionTitle("シーン", Modifier.padding(top = 8.dp)) }
            items(scenes, key = { "s" + it.id }) { s ->
                PartRow(Part.ScenePart(s), s.name, PanelIcons.Scene, onTap, onDragStart, onDrag, onDragEnd, onDragCancel)
            }
            item { SectionTitle("マクロ", Modifier.padding(top = 8.dp)) }
            items(macros, key = { "m" + it.id }) { m ->
                PartRow(Part.MacroPart(m), m.name, PanelIcons.Steps, onTap, onDragStart, onDrag, onDragEnd, onDragCancel)
            }
        }
    }
}

@Composable
private fun PartRow(
    part: Part,
    label: String,
    icon: ImageVector,
    onTap: (Part) -> Unit,
    onDragStart: (Part, Offset) -> Unit,
    onDrag: (Offset) -> Unit,
    onDragEnd: () -> Unit,
    onDragCancel: () -> Unit,
) {
    var pos by remember { mutableStateOf(Offset.Zero) }
    Row(
        Modifier
            .fillMaxWidth()
            .height(44.dp)
            .onGloballyPositioned { pos = it.positionInRoot() }
            .clip(ButtonShape)
            .background(PanelColors.Button)
            .pointerInput(part) {
                detectDragGesturesAfterLongPress(
                    onDragStart = { o -> onDragStart(part, pos + o) },
                    onDragEnd = onDragEnd,
                    onDragCancel = onDragCancel,
                ) { change, amount ->
                    change.consume()
                    onDrag(amount)
                }
            }
            .clickable { onTap(part) }
            .padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, null, tint = PanelColors.Text, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(10.dp))
        Text(label, color = PanelColors.Text, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        Icon(PanelIcons.Plus, "追加", tint = PanelColors.SubText, modifier = Modifier.size(16.dp))
    }
}

@Composable
private fun CommandPicker(device: Device, onDismiss: () -> Unit, onPick: (Command) -> Unit) {
    var custom by remember { mutableStateOf("") }
    val commands = CommandCatalog.commandsFor(device)
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(device.name) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                if (commands.isNotEmpty()) {
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        commands.forEach { c -> PanelButton(c.label, icon = commandIcon(c)) { onPick(c) } }
                    }
                }
                if (CommandCatalog.supportsCustom(device)) {
                    SectionTitle("カスタムボタン（SwitchBot アプリで登録した名前）")
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedTextField(
                            value = custom,
                            onValueChange = { custom = it },
                            singleLine = true,
                            placeholder = { Text("例：電源、上、停止") },
                            modifier = Modifier.weight(1f),
                        )
                        PanelButton("追加", enabled = custom.isNotBlank()) { onPick(CommandCatalog.custom(custom.trim())) }
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text("キャンセル") } },
        containerColor = PanelColors.Card,
    )
}

/** 待ち時間のダイヤル：左右にドラッグで1秒ずつ変わる */
@Composable
private fun WaitDialog(initial: Int, onDismiss: () -> Unit, onConfirm: (Int) -> Unit) {
    var sec by remember { mutableIntStateOf(initial.coerceIn(1, MacroRules.MAX_WAIT_SECONDS)) }
    val accent = LocalAccent.current
    fun adjust(d: Int) {
        sec = (sec + d).coerceIn(1, MacroRules.MAX_WAIT_SECONDS)
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("待ち時間") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Box(
                    Modifier
                        .fillMaxWidth()
                        .height(96.dp)
                        .clip(CardShape)
                        .background(PanelColors.Button)
                        .pointerInput(Unit) {
                            var acc = 0f
                            val stepPx = 14.dp.toPx()
                            detectHorizontalDragGestures { change, dx ->
                                change.consume()
                                acc += dx
                                val n = (acc / stepPx).toInt()
                                if (n != 0) {
                                    adjust(n)
                                    acc -= n * stepPx
                                }
                            }
                        },
                    contentAlignment = Alignment.Center,
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(
                            MacroRules.formatSeconds(sec),
                            color = accent,
                            fontFamily = OutfitFamily,
                            fontWeight = FontWeight.Light,
                            fontSize = 40.sp,
                        )
                        Text("左右にドラッグで調整", color = PanelColors.SubText, fontSize = 12.sp)
                    }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf(-10, -1, 1, 10).forEach { d ->
                        PanelButton(if (d > 0) "+$d" else "$d", Modifier.weight(1f)) { adjust(d) }
                    }
                }
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf(3, 5, 10, 15, 20, 25, 30, 45, 60, 120).forEach { p ->
                        PanelButton(MacroRules.formatSeconds(p), highlight = p == sec) { sec = p }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = { onConfirm(sec) }) { Text("決定") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("キャンセル") } },
        containerColor = PanelColors.Card,
    )
}

