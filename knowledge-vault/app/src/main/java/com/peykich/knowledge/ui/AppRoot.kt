package com.peykich.knowledge.ui

import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.LibraryBooks
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.navigation.NavDestination.Companion.hasRoute
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.peykich.knowledge.data.AppEvents
import com.peykich.knowledge.ui.browse.BrowseScreen
import com.peykich.knowledge.ui.home.HomeScreen
import com.peykich.knowledge.ui.note.NoteScreen
import com.peykich.knowledge.ui.record.RecordScreen
import com.peykich.knowledge.ui.search.SearchScreen
import com.peykich.knowledge.ui.settings.SettingsScreen
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject

@HiltViewModel
class RootViewModel @Inject constructor(val events: AppEvents) : ViewModel()

private data class Tab(val route: Any, val label: String, val icon: ImageVector)

@Composable
fun AppRoot(root: RootViewModel = hiltViewModel()) {
    val nav = rememberNavController()
    val snackbar = remember { SnackbarHostState() }
    val entry by nav.currentBackStackEntryAsState()
    val dest = entry?.destination
    val tabs = listOf(
        Tab(HomeRoute, "ホーム", Icons.Outlined.Home),
        Tab(BrowseRoute, "ブラウズ", Icons.AutoMirrored.Outlined.LibraryBooks),
        Tab(SettingsRoute, "設定", Icons.Outlined.Settings),
    )
    val onTop = dest?.let { d -> d.hasRoute(HomeRoute::class) || d.hasRoute(BrowseRoute::class) || d.hasRoute(SettingsRoute::class) } ?: true

    LaunchedEffect(Unit) {
        root.events.messages.collect { m ->
            val r = snackbar.showSnackbar(
                message = m.text,
                actionLabel = if (m.openNoteId != null) "開く" else null,
                duration = SnackbarDuration.Short,
            )
            if (r == SnackbarResult.ActionPerformed && m.openNoteId != null) nav.navigate(NoteRoute(m.openNoteId))
        }
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbar) },
        bottomBar = {
            if (onTop) {
                NavigationBar {
                    for (t in tabs) {
                        val selected = dest?.hasRoute(t.route::class) == true
                        NavigationBarItem(
                            selected = selected,
                            onClick = {
                                nav.navigate(t.route) {
                                    popUpTo(nav.graph.findStartDestination().id) { saveState = true }
                                    launchSingleTop = true
                                    restoreState = true
                                }
                            },
                            icon = { Icon(t.icon, contentDescription = null) },
                            label = { Text(t.label) },
                        )
                    }
                }
            }
        },
    ) { padding ->
        NavHost(nav, startDestination = HomeRoute, modifier = Modifier.padding(padding).consumeWindowInsets(padding)) {
            composable<HomeRoute> {
                HomeScreen(
                    onRecord = { nav.navigate(RecordRoute(it.key)) },
                    onSearch = { nav.navigate(SearchRoute) },
                    onOpen = { nav.navigate(NoteRoute(it)) },
                    onSettings = { nav.navigate(SettingsRoute) { launchSingleTop = true } },
                )
            }
            composable<BrowseRoute> { BrowseScreen(onOpen = { nav.navigate(NoteRoute(it)) }) }
            composable<SettingsRoute> { SettingsScreen() }
            composable<RecordRoute> { RecordScreen(onClose = { nav.popBackStack() }, onSettings = { nav.navigate(SettingsRoute) }) }
            composable<NoteRoute> { NoteScreen(onBack = { nav.popBackStack() }) }
            composable<SearchRoute> { SearchScreen(onBack = { nav.popBackStack() }, onOpen = { nav.navigate(NoteRoute(it)) }) }
        }
    }
}

