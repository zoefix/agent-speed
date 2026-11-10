import QtQuick
import QtQuick.Layouts

// DESIGN 5 額度, phase 2 — ZCode's key: a secure field (`settings.quota.zcode.key`). A pasted key goes to the secret
// store when the field loses focus or Return is pressed; the field then reads `settings.quota.zcode.saved` with a
// Remove button. The key is never shown again.
RowLayout {
    id: key
    // The label column shared with the region picker below.
    property real labelWidth: 0
    spacing: 10

    Text {
        textFormat: Text.PlainText
        Layout.preferredWidth: key.labelWidth > 0 ? key.labelWidth : implicitWidth
        text: S.tr2(S.rev, "settings.quota.zcode.key")
        color: Theme.ink2
        font.pixelSize: 12
    }
    FieldInput {
        id: field
        visible: !quotas.zcodeKeySaved
        Layout.fillWidth: true
        Layout.maximumWidth: 320
        secret: true
        placeholder: S.tr2(S.rev, "settings.quota.zcode.keyPlaceholder")
        accessibleName: S.tr2(S.rev, "settings.quota.zcode.key")
        maximumLength: 512
        onCommitted: (text) => {
            const trimmed = text.trim()
            if (trimmed.length === 0) return
            quotas.saveZcodeKey(trimmed)
            field.text = ""
            field.restore()
        }
    }
    RowLayout {
        visible: quotas.zcodeKeySaved
        spacing: 6
        Glyph {
            Layout.preferredWidth: 12
            Layout.preferredHeight: 12
            name: "checkCircle"
            color: Theme.good
            Accessible.ignored: true
        }
        Text {
            textFormat: Text.PlainText
            text: S.tr2(S.rev, "settings.quota.zcode.saved")
            color: Theme.ink
            font.pixelSize: 12
        }
        LinkButton {
            text: S.tr2(S.rev, "settings.quota.zcode.remove")
            destructive: true
            onClicked: quotas.removeZcodeKey()
        }
    }
    Item { Layout.fillWidth: true }
}
