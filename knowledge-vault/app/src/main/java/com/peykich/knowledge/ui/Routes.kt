package com.peykich.knowledge.ui

import kotlinx.serialization.Serializable

@Serializable object HomeRoute
@Serializable object BrowseRoute
@Serializable object SettingsRoute
@Serializable data class RecordRoute(val type: String)
@Serializable data class NoteRoute(val id: String)
@Serializable object SearchRoute
