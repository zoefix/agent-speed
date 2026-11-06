import QtQuick
import QtQuick.Controls

// DESIGN 13: a host's actions — Edit…, Retry Now (only while it is failing), Copy SSH Command and Remove….
Menu {
    id: menu
    property var host: ({})
    signal editRequested()
    signal removeRequested()

    MenuItem {
        text: S.tr2(S.rev, "remote.edit")
        onTriggered: menu.editRequested()
    }
    MenuItem {
        visible: menu.host.canRetry === true
        height: visible ? implicitHeight : 0
        text: S.tr2(S.rev, "remote.retry")
        onTriggered: remote.retryNow(menu.host.hostId)
    }
    MenuItem {
        text: S.tr2(S.rev, "remote.copyCommand")
        onTriggered: hostsView.copySshCommand(menu.host.hostId)
    }
    MenuSeparator {}
    MenuItem {
        text: S.tr2(S.rev, "remote.remove")
        palette.text: Theme.danger
        onTriggered: menu.removeRequested()
    }
}
