package com.peykich.knowledge.data

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.receiveAsFlow

/** 画面をまたいで出すお知らせ（保存したあとホームで出すスナックバーなど）。 */
class AppEvents {
    data class Message(val text: String, val openNoteId: String? = null)

    private val channel = Channel<Message>(Channel.BUFFERED)
    val messages = channel.receiveAsFlow()

    fun post(message: Message) {
        channel.trySend(message)
    }
}
