package com.jetbrains.rider.plugins.unity.toolWindow.log

import com.intellij.execution.filters.Filter
import com.intellij.execution.filters.TextConsoleBuilderFactory
import com.intellij.execution.impl.ConsoleViewImpl
import com.intellij.execution.ui.ConsoleViewContentType
import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.editor.actions.ToggleUseSoftWrapsToolbarAction
import com.intellij.openapi.extensions.Extensions
import com.intellij.openapi.project.DumbAwareAction
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.ui.DoubleClickListener
import com.intellij.ui.JBSplitter
import com.intellij.ui.PopupHandler
import com.intellij.ui.components.JBScrollPane
import com.intellij.unscramble.AnalyzeStacktraceUtil
import com.jetbrains.rider.plugins.unity.editorPlugin.model.*
import com.jetbrains.rider.plugins.unity.UnityHost
import com.jetbrains.rider.settings.RiderUnitySettings
import com.jetbrains.rider.ui.RiderSimpleToolWindowWithTwoToolbarsPanel
import com.jetbrains.rider.ui.RiderUI
import com.jetbrains.rider.unitTesting.panels.RiderUnitTestSessionPanel
import com.jetbrains.rider.util.reactive.whenTrue
import java.awt.BorderLayout
import java.awt.Component
import java.awt.event.KeyAdapter
import java.awt.event.KeyEvent
import java.awt.event.MouseEvent
import javax.swing.Icon
import javax.swing.JMenuItem
import javax.swing.JPopupMenu

class UnityLogPanelView(project: Project, private val logModel: UnityLogPanelModel, unityHost: UnityHost) {
    private val console = TextConsoleBuilderFactory.getInstance()
        .createBuilder(project)
        .filters(*Extensions.getExtensions<Filter>(AnalyzeStacktraceUtil.EP_NAME, project))
        .console as ConsoleViewImpl

    private val eventList = UnityLogPanelEventList().apply {
        addListSelectionListener {
            if (selectedValue != null) {
                logModel.selectedItem = selectedValue

                console.clear()
                if (selectedIndex >= 0) {
                    console.print(selectedValue.message + "\n", ConsoleViewContentType.NORMAL_OUTPUT)
                    console.print(selectedValue.stackTrace, ConsoleViewContentType.NORMAL_OUTPUT)
                    console.scrollTo(0)
                }
            }
        }

        val eventList1 = this
        addKeyListener(object : KeyAdapter() {
            override fun keyPressed(e: KeyEvent?) {
                if (e?.keyCode == KeyEvent.VK_ENTER) {
                    e.consume()
                    getNavigatableForSelected(eventList1, project)?.navigate(true)
                }
            }
        })
        object : DoubleClickListener() {
            override fun onDoubleClick(event: MouseEvent?): Boolean {
                getNavigatableForSelected(eventList1, project)?.navigate(true)
                return true
            }
        }.installOn(this)

        var prevVal: Boolean? = null

        unityHost.play.advise(logModel.lifetime) {
            if (it != null && it && prevVal == false) {
                logModel.events.clear()
                console.clear()
            }
            prevVal = it
        }
    }

    val mainSplitterOrientation = RiderUnitySettings.BooleanViewProperty("mainSplitterOrientation")

    private val mainSplitterToggleAction = object : DumbAwareAction("Toggle Output Position", "Toggle Output pane position (right/bottom)", AllIcons.Actions.SplitVertically) {
        override fun actionPerformed(e: AnActionEvent) {
            mainSplitterOrientation.invert()
            update(e)
        }

        override fun update(e: AnActionEvent) {
            e.presentation.icon = getMainSplitterIcon()
        }
    }

    private val mainSplitter = JBSplitter().apply {
        proportion = 1f / 2
        firstComponent = JBScrollPane(eventList)
        secondComponent = RiderUI.borderPanel {
            add(console.component, BorderLayout.CENTER)
            console.editor.settings.isCaretRowShown = true
            console.clear()
            console.allowHeavyFilters()
        }
        orientation = mainSplitterOrientation.value
        divider.addMouseListener(object : PopupHandler() {
            override fun invokePopup(comp: Component?, x: Int, y: Int) {
                JPopupMenu().apply {
                    add(JMenuItem("Toggle Output Position", getMainSplitterIcon(true)).apply {
                        addActionListener({ mainSplitterOrientation.invert() })
                    })
                }.show(comp, x, y)
            }
        })
    }

    private val leftToolbar = UnityLogPanelToolbarBuilder.createLeftToolbar(logModel, mainSplitterToggleAction, console.createConsoleActions()
        .filter { it is ToggleUseSoftWrapsToolbarAction }.toList())

    private val topToolbar = UnityLogPanelToolbarBuilder.createTopToolbar()

    fun getMainSplitterIcon(invert: Boolean = false): Icon? = when (mainSplitterOrientation.value xor invert) {
        true -> RiderUnitTestSessionPanel.splitBottomIcon
        false -> RiderUnitTestSessionPanel.splitRightIcon
    }

    val panel = RiderSimpleToolWindowWithTwoToolbarsPanel(leftToolbar, topToolbar, mainSplitter)

    // TODO: optimize
    private fun refreshList(newEvents: List<RdLogEvent>) {
        eventList.riderModel.clear()
        for (event in newEvents) {
            eventList.riderModel.addElement(event)
        }

        console.clear()
        if (logModel.selectedItem != null) {
            eventList.setSelectedValue(logModel.selectedItem, true)
        } else
            eventList.ensureIndexIsVisible(eventList.itemsCount - 1)
    }

    init {
        Disposer.register(project, console)

        mainSplitterOrientation.advise(logModel.lifetime) { value ->
            mainSplitter.orientation = value
            mainSplitter.updateUI()
        }

        logModel.onChanged.advise(logModel.lifetime) { refreshList(it) }
        logModel.fire()
    }
}